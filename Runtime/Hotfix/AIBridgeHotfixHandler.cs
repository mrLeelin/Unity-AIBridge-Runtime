using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace AIBridge.Runtime
{
    /// <summary>
    /// 将经过桌面端结构校验的补丁应用到 Development Player 中已加载的 HybridCLR 程序集。
    /// </summary>
    public sealed class AIBridgeHotfixHandler : IAIBridgeHandler
    {
        private const int HistoryLimit = 64;
        private static readonly string[] Actions = { "runtime.patch.apply", "runtime.patch.history", "runtime.patch.capabilities" };
        private static bool _prepared;
        private static string _preparationError = "PrepareRuntime must run before target hot-update code is loaded or executed.";
        private readonly AIBridgeRuntimeSettings _settings;
        private readonly int _maxPatchCount;
        private readonly int _mainThreadId;
        private readonly Dictionary<string, Dictionary<string, object>> _applied = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _patchIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Queue<Dictionary<string, object>> _history = new Queue<Dictionary<string, object>>();
        private int _nativeAttempts;

        /// <summary>记录初始化状态；成功只表示已调用原生配置 API，不证明调用时机早于业务代码。</summary>
        public static string PreparationError => _preparationError;

        /// <summary>创建补丁接收器；失败的原生调用也计入加载次数上限，防止反复失败造成内存持续增长。</summary>
        public AIBridgeHotfixHandler(AIBridgeRuntimeSettings settings, int maxPatchCount = 32)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _maxPatchCount = Math.Max(1, Math.Min(maxPatchCount, HistoryLimit));
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>此接收器处理的真机热修复命令。</summary>
        public string[] SupportedActions => (string[])Actions.Clone();

        /// <summary>
        /// 在加载/执行目标热更新代码之前调用，禁用与动态热修复不兼容的方法内联。
        /// Editor 中 HybridCLR 的补丁实现为空，因此明确返回不可用。
        /// </summary>
        public static bool PrepareRuntime()
        {
            if (_prepared)
                return true;
            try
            {
                EnsurePlayer();
                AIBridgeHotfixAdapter.Resolve().DisableMethodInlining();
                _prepared = true;
                _preparationError = null;
            }
            catch (Exception exception)
            {
                _preparationError = Unwrap(exception).Message;
            }
            return _prepared;
        }

        /// <summary>在 Unity 主线程上校验并处理补丁请求，返回可序列化结果。</summary>
        public AIBridgeRuntimeCommandResult HandleCommand(AIBridgeRuntimeCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            switch (command.Action)
            {
                case "runtime.patch.capabilities":
                    return AIBridgeRuntimeCommandResult.FromSuccess(command.Id, BuildCapabilities());
                case "runtime.patch.history":
                    return AIBridgeRuntimeCommandResult.FromSuccess(command.Id, new
                    {
                        attempts = _nativeAttempts, limit = _maxPatchCount, entries = _history.ToArray(),
                        stateRollbackSupported = false, loadedAssembliesCanBeUnloaded = false
                    });
                case "runtime.patch.apply":
                    return Apply(command);
                default:
                    return null;
            }
        }

        private AIBridgeRuntimeCommandResult Apply(AIBridgeRuntimeCommand command)
        {
            try
            {
                EnsurePlayer();
                if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                    throw new InvalidOperationException("Patch commands must execute on Unity's main thread.");
                if (!_settings.enableRuntimeBridge || !_settings.enableRuntimeCodeExecution)
                    throw new InvalidOperationException("Runtime code execution is disabled in Runtime Bridge settings.");
                if (!_prepared)
                    throw new InvalidOperationException(_preparationError);

                var request = AIBridgePatchRequest.Parse(command);
                var adapter = AIBridgeHotfixAdapter.Resolve();
                var target = adapter.ResolveTarget(request);
                var manifest = adapter.BuildManifest(request, target);
                return ApplyValidated(command.Id, request, adapter, manifest);
            }
            catch (Exception exception)
            {
                return AIBridgeRuntimeCommandResult.FromFailure(command.Id, Unwrap(exception).Message);
            }
        }

        private AIBridgeRuntimeCommandResult ApplyValidated(string commandId, AIBridgePatchRequest request,
            AIBridgeHotfixAdapter adapter, System.Collections.IList manifest)
        {
            var key = request.GetDeduplicationKey();
            string previousKey;
            if (request.PatchId != null && _patchIds.TryGetValue(request.PatchId, out previousKey) && previousKey != key)
                throw new InvalidOperationException("patchId has already been applied to a different patch payload.");
            Dictionary<string, object> applied;
            if (_applied.TryGetValue(key, out applied))
                return AIBridgeRuntimeCommandResult.FromSuccess(commandId, new { deduplicated = true, patch = applied });
            if (_nativeAttempts >= _maxPatchCount)
                throw new InvalidOperationException("Patch attempt limit reached. Restart this test Player before applying more patches.");

            var history = BuildHistory(request);
            _nativeAttempts++;
            try
            {
                adapter.Apply(request, manifest);
                history["applied"] = true;
                _applied.Add(key, history);
                _patchIds.Add((string)history["patchId"], key);
                return AIBridgeRuntimeCommandResult.FromSuccess(commandId, new { deduplicated = false, patch = history });
            }
            catch (Exception exception)
            {
                history["error"] = Unwrap(exception).Message;
                return AIBridgeRuntimeCommandResult.FromFailure(commandId,
                    "HybridCLR patch application failed; partial application or DLL allocation cannot be excluded. " + history["error"]);
            }
            finally
            {
                _history.Enqueue(history);
                while (_history.Count > HistoryLimit)
                    _history.Dequeue();
            }
        }

        private static Dictionary<string, object> BuildHistory(AIBridgePatchRequest request)
        {
            return new Dictionary<string, object>
            {
                ["patchId"] = request.PatchId ?? Guid.NewGuid().ToString("N"),
                ["assemblyName"] = request.AssemblyName,
                ["baselineMvid"] = request.BaselineMvid.ToString("D"),
                ["sha256"] = request.Sha256,
                ["methodCount"] = request.MethodCount,
                ["methods"] = request.Types.Select(type => new { name = type.Name, signatures = type.Signatures.ToArray() }).ToArray(),
                ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
                ["applied"] = false,
                ["structuralValidation"] = "trusted-client-required; not device-guaranteed",
                ["stateRollbackSupported"] = false
            };
        }

        private object BuildCapabilities()
        {
            string reason = null;
            try
            {
                EnsurePlayer();
                AIBridgeHotfixAdapter.Resolve();
                if (!_settings.enableRuntimeBridge || !_settings.enableRuntimeCodeExecution)
                    reason = "Runtime code execution is disabled in Runtime Bridge settings.";
                else if (!_prepared)
                    reason = _preparationError;
                else if (_nativeAttempts >= _maxPatchCount)
                    reason = "Patch attempt limit reached; restart the test Player.";
            }
            catch (Exception exception)
            {
                reason = Unwrap(exception).Message;
            }
            return new
            {
                available = reason == null, reason, prepared = _prepared,
                prepareMustPrecedeTargetCode = true, preparationOrderingVerified = false,
                structuralValidation = "trusted-client-required; not device-guaranteed",
                nativeApiReturnedSuccessfully = _applied.Count > 0,
                methodBodiesOnly = true, stateRollbackSupported = false,
                loadedAssembliesCanBeUnloaded = false, attempts = _nativeAttempts, limit = _maxPatchCount
            };
        }

        private static void EnsurePlayer()
        {
            if (Application.isEditor)
                throw new InvalidOperationException("Hotfix apply is unavailable in Unity Editor: HybridCLR's Editor implementation is a no-op. Use an IL2CPP Development Player.");
            if (!Debug.isDebugBuild)
                throw new InvalidOperationException("Hotfix debugging is available only in Development Players.");
        }

        private static Exception Unwrap(Exception exception)
        {
            while (exception is TargetInvocationException && exception.InnerException != null)
                exception = exception.InnerException;
            return exception;
        }
    }
}
