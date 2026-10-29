using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIBridge.Internal.Json;
using AIBridge.Runtime.Shared;
using AIBridge.Runtime.Diagnostics;
using AIBridge.Runtime.Transports;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIBridge.Runtime
{
    internal enum AIBridgeHttpCommandState
    {
        NotFound,
        Queued,
        Executing,
        Completed,
        Cancelled
    }

    /// <summary>
    /// AIBridge Runtime MonoBehaviour singleton.
    /// Receives and processes commands from AI Code assistants during Play mode or built Player runtime.
    /// </summary>
    public partial class AIBridgeRuntime : MonoBehaviour
    {
        private const string RuntimeVersion = "1.0";
        private const string RuntimeDirArgument = "--aibridge-runtime-dir";
        private const string TargetIdArgument = "--aibridge-target-id";
        private const string RuntimeDirEnvironment = "AIBRIDGE_RUNTIME_DIR";
        private const string TargetIdEnvironment = "AIBRIDGE_TARGET_ID";
        private const string RuntimeDirectoryName = "runtime";
        private const string TargetsDirectoryName = "targets";
        private const string CommandsDirectoryName = "commands";
        private const string ResultsDirectoryName = "results";
        private const string ScreenshotsDirectoryName = "screenshots";
        private const string HeartbeatFileName = "heartbeat.json";
        private const string RuntimeCodeExecuteAction = "runtime.code.execute";
        private const int MaxRuntimeCodeAssemblyBytes = 8 * 1024 * 1024;
        private const int MaxRuntimeCodeResultDepth = 8;
        private const int MaxRuntimeCodeCollectionItems = 512;
        private const int MaxRetainedScreenshots = 20;
        private const int CommandPumpStaleMilliseconds = 3000;
        private const int MaxCommandIdLength = 128;

        private static readonly string[] BuiltInActions =
        {
            "runtime.ping",
            "runtime.status",
            "runtime.logs",
            "runtime.logs.clear",
            "runtime.perf",
            "runtime.screenshot",
            "runtime.observe",
            "runtime.ui.snapshot",
            "runtime.ui.find",
            "runtime.ui.raycast",
            "runtime.ui.click",
            RuntimeCodeExecuteAction,
            "runtime.handlers"
        };

        /// <summary>
        /// Singleton instance.
        /// </summary>
        public static AIBridgeRuntime Instance { get; private set; }

        public AIBridgeRuntimeSettings runtimeSettings = new AIBridgeRuntimeSettings();
        internal bool SettingsProvidedExplicitly;

        /// <summary>
        /// Polling interval in seconds.
        /// </summary>
        [Tooltip("How often to check for new commands (in seconds)")]
        public float pollIntervalSeconds = 0.1f;

        /// <summary>
        /// Maximum commands to process per frame.
        /// </summary>
        [Tooltip("Maximum number of commands to process per frame")]
        public int maxCommandsPerFrame = 5;

        /// <summary>
        /// Enable debug logging.
        /// </summary>
        [Tooltip("Enable debug logging")]
        public bool enableDebugLog = false;

        private string _runtimeRootPath;
        private string _targetPath;
        private string _commandsPath;
        private string _resultsPath;
        private string _screenshotsPath;
        private string _heartbeatPath;
        private string _targetId;
        private string _cachedProductName;
        private string _cachedApplicationVersion;
        private string _cachedUnityVersion;
        private string _cachedBuildId;
        private string _cachedPlatform;
        private string _cachedDeviceName;
        private bool _cachedIsEditor;
        private bool _cachedIsDebugBuild;

        private readonly Queue<AIBridgeRuntimeCommand> _commandQueue = new Queue<AIBridgeRuntimeCommand>();
        private readonly Dictionary<string, Assembly> _assemblyCache = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private readonly List<IAIBridgeHandler> _handlers = new List<IAIBridgeHandler>();
        private readonly List<IAIBridgeAsyncHandler> _asyncHandlers = new List<IAIBridgeAsyncHandler>();
        private readonly AIBridgeRuntimeLogBuffer _logBuffer = new AIBridgeRuntimeLogBuffer();
        private readonly object _pendingHttpResultsSyncRoot = new object();
        private readonly Dictionary<string, AIBridgeRuntimeCommandResult> _pendingHttpResults = new Dictionary<string, AIBridgeRuntimeCommandResult>();
        private readonly HashSet<string> _httpCommandIds = new HashSet<string>();
        private readonly HashSet<string> _executingHttpCommandIds = new HashSet<string>();
        private readonly HashSet<string> _cancelledHttpCommandIds = new HashSet<string>();
        private HttpRuntimeTransportServer _httpTransportServer;
        private LanRuntimeDiscoveryServer _lanDiscoveryServer;

        private float _lastPollTime;
        private float _lastHeartbeatTime;
        private float _lastOrphanCleanupTime;
        private DateTime _startedAtUtc;
        private string _serviceSessionId = Guid.NewGuid().ToString("N");
        private bool _initialized;
        private RuntimeToolHandler _runtimeTools;
        private bool _runInBackgroundChanged;
        private bool _previousRunInBackground;
        private volatile bool _runtimeServicesStopping;
        private string _runtimeStopReason;
        private long _lastMainThreadTickUtcTicks;
        private int _processingCommands;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[AIBridgeRuntime] Duplicate instance detected, destroying...");
                Destroy(gameObject);
                return;
            }

#if DEVELOPMENT_BUILD && !UNITY_EDITOR
            // 显式启用项目配置后，旧场景组件也使用同一份 Runtime 配置；不修改场景资产。
            if (!SettingsProvidedExplicitly)
            {
                var configuration = AIBridgeRuntimeConfig.Load();
                if (configuration != null && configuration.AutoStart)
                    runtimeSettings = configuration.Settings.Clone();
            }
#endif
            Instance = this;
            DontDestroyOnLoad(gameObject);
            _startedAtUtc = DateTime.UtcNow;
            MarkRuntimeServicesRunning();
            Initialize();
        }

        private void OnEnable()
        {
            if (Instance != this || !_initialized)
            {
                return;
            }

            MarkRuntimeServicesRunning();
            ApplyRunInBackgroundIfNeeded();
            StartHttpTransportIfNeeded();
            StartLanDiscoveryIfNeeded();
            WriteHeartbeat();
        }

        private void OnDisable()
        {
            StopRuntimeServices("disabled");
        }

        private void OnApplicationQuit()
        {
            StopRuntimeServices("application_quit");
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                StopRuntimeServices("destroyed");
                Instance = null;
                _logBuffer.Dispose();
                LogDebug("Destroyed");
            }
        }

        private void Update()
        {
            if (!_initialized)
            {
                return;
            }

            MarkMainThreadTick();
            _runtimeTools?.Tick();
            WriteHeartbeatIfDue();
            CleanupOrphanResultsIfDue();

            var now = Time.realtimeSinceStartup;
            if (now - _lastPollTime >= pollIntervalSeconds)
            {
                _lastPollTime = now;
                ScanForCommands();
            }

            var processed = 0;
            while (processed < maxCommandsPerFrame)
            {
                AIBridgeRuntimeCommand cmd = null;
                lock (_commandQueue)
                {
                    if (_commandQueue.Count == 0)
                    {
                        break;
                    }

                    cmd = _commandQueue.Dequeue();
                }

                if (!TryBeginCommand(cmd))
                {
                    processed++;
                    continue;
                }

                Interlocked.Increment(ref _processingCommands);
                try
                {
                    ProcessCommand(cmd);
                }
                finally
                {
                    Interlocked.Decrement(ref _processingCommands);
                    MarkMainThreadTick();
                }

                processed++;
            }
        }

        /// <summary>
        /// Register a command handler.
        /// Handlers are called in registration order until one returns a non-null result.
        /// </summary>
        public void RegisterHandler(IAIBridgeHandler handler)
        {
            if (handler != null && !_handlers.Contains(handler))
            {
                _handlers.Add(handler);
                LogDebug($"Registered handler: {handler.GetType().Name}");
            }
        }

        /// <summary>
        /// Unregister a command handler.
        /// </summary>
        public void UnregisterHandler(IAIBridgeHandler handler)
        {
            if (_handlers.Remove(handler))
            {
                LogDebug($"Unregistered handler: {handler.GetType().Name}");
            }
        }

        /// <summary>
        /// Register an async command handler.
        /// </summary>
        public void RegisterAsyncHandler(IAIBridgeAsyncHandler handler)
        {
            if (handler != null && !_asyncHandlers.Contains(handler))
            {
                _asyncHandlers.Add(handler);
                LogDebug($"Registered async handler: {handler.GetType().Name}");
            }
        }

        /// <summary>
        /// Unregister an async command handler.
        /// </summary>
        public void UnregisterAsyncHandler(IAIBridgeAsyncHandler handler)
        {
            if (_asyncHandlers.Remove(handler))
            {
                LogDebug($"Unregistered async handler: {handler.GetType().Name}");
            }
        }

        private void Initialize()
        {
            if (!IsRuntimeBridgeEnabled())
            {
                LogDebug("Runtime Bridge disabled by settings/build type.");
                return;
            }

            _targetId = ResolveTargetId();
            CacheMainThreadRuntimeInfo();
            _runtimeRootPath = ResolveRuntimeRootPath();
            _targetPath = Path.Combine(_runtimeRootPath, TargetsDirectoryName, _targetId);
            _commandsPath = Path.Combine(_targetPath, CommandsDirectoryName);
            _resultsPath = Path.Combine(_targetPath, ResultsDirectoryName);
            _screenshotsPath = Path.Combine(_targetPath, ScreenshotsDirectoryName);
            _heartbeatPath = Path.Combine(_targetPath, HeartbeatFileName);

            try
            {
                Directory.CreateDirectory(_commandsPath);
                Directory.CreateDirectory(_resultsPath);
                Directory.CreateDirectory(_screenshotsPath);
                _logBuffer.Initialize(Math.Max(1, runtimeSettings.logBufferSize));
                RegisterHandler(new AIBridgeHotfixHandler(runtimeSettings));
                _runtimeTools = new RuntimeToolHandler(runtimeSettings);
                RegisterHandler(_runtimeTools);
                _initialized = true;
                ApplyRunInBackgroundIfNeeded();
                StartHttpTransportIfNeeded();
                StartLanDiscoveryIfNeeded();
                WriteHeartbeat();
            }
            catch (Exception e)
            {
                Debug.LogError($"[AIBridgeRuntime] Failed to create directories: {e.Message}");
            }

            LogDebug($"Initialized - Target: {_targetId}");
            LogDebug($"Initialized - Commands: {_commandsPath}");
            LogDebug($"Initialized - Results: {_resultsPath}");
        }

        private void ScanForCommands()
        {
            if (string.IsNullOrEmpty(_commandsPath) || !Directory.Exists(_commandsPath))
            {
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(_commandsPath, "*.json");
            }
            catch
            {
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var commandData = AIBridgeJson.DeserializeObject(json);
                    var cmd = AIBridgeRuntimeCommand.FromDictionary(commandData);

                    if (cmd != null)
                    {
                        lock (_commandQueue)
                        {
                            _commandQueue.Enqueue(cmd);
                        }

                        File.Delete(file);
                        LogDebug($"Queued command: {cmd.Action} - {cmd.Id}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[AIBridgeRuntime] Failed to parse command: {file}\n{e.Message}");

                    try
                    {
                        File.Move(file, file + ".error");
                    }
                    catch
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
        }

        private void ProcessCommand(AIBridgeRuntimeCommand cmd)
        {
            var commandId = cmd == null ? "unknown" : cmd.Id;
            LogDebug(cmd == null ? "Processing null command" : $"Processing command: {cmd.Action} - {cmd.Id}");

            AIBridgeRuntimeCommandResult result = null;

            try
            {
                if (!ValidateCommand(cmd, out var validationError))
                {
                    result = AIBridgeRuntimeCommandResult.FromFailure(commandId, validationError);
                    WriteResult(result);
                    return;
                }

                if (!IsCustomActionAllowed(cmd.Action))
                {
                    result = AIBridgeRuntimeCommandResult.FromFailure(
                        cmd.Id,
                        $"Runtime action is not allowed: {cmd.Action}");
                    WriteResult(result);
                    return;
                }

                if (TryHandleBuiltInCommand(cmd, out result, out var asyncStarted))
                {
                    if (!asyncStarted)
                    {
                        WriteResult(result);
                    }

                    return;
                }

                ProcessCustomCommand(cmd, out result);
            }
            catch (Exception ex)
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(commandId, $"{ex.GetType().Name}: {ex.Message}");
            }

            WriteResult(result);
        }

        private void ProcessCustomCommand(AIBridgeRuntimeCommand cmd, out AIBridgeRuntimeCommandResult result)
        {
            result = null;

            foreach (var handler in _asyncHandlers)
            {
                if (handler.SupportedActions != null && Array.IndexOf(handler.SupportedActions, cmd.Action) >= 0)
                {
                    if (handler.HandleCommandAsync(cmd, WriteResult))
                    {
                        LogDebug($"Command handled by async handler: {handler.GetType().Name}");
                        return;
                    }
                }
            }

            foreach (var handler in _handlers)
            {
                if (handler.SupportedActions != null && Array.IndexOf(handler.SupportedActions, cmd.Action) >= 0)
                {
                    result = handler.HandleCommand(cmd);
                    if (result != null)
                    {
                        LogDebug($"Command handled by handler: {handler.GetType().Name}");
                        break;
                    }
                }
            }

            if (result == null)
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(
                    cmd.Id,
                    $"No handler found for action: {cmd.Action}. Register a handler using AIBridgeRuntime.Instance.RegisterHandler()");
            }
        }

        private bool TryHandleBuiltInCommand(AIBridgeRuntimeCommand cmd, out AIBridgeRuntimeCommandResult result, out bool asyncStarted)
        {
            result = null;
            asyncStarted = false;

            switch (cmd.Action)
            {
                case "runtime.ping":
                    result = AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, new
                    {
                        targetId = _targetId,
                        pong = true,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                    return true;
                case "runtime.status":
                    result = AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, BuildStatusData());
                    return true;
                case "runtime.logs":
                case "runtime.logs.clear":
                    result = AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, BuildLogsData(cmd));
                    return true;
                case "runtime.perf":
                    StartCoroutine(SamplePerformance(cmd));
                    asyncStarted = true;
                    return true;
                case "runtime.handlers":
                    result = AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, new
                    {
                        handlers = BuildHandlerSummaries(true),
                        targetId = _targetId
                    });
                    return true;
                case "runtime.observe":
                    StartCoroutine(CaptureObservation(cmd));
                    asyncStarted = true;
                    return true;
                case "runtime.screenshot":
                    StartCoroutine(CaptureScreenshot(cmd));
                    asyncStarted = true;
                    return true;
                case "runtime.ui.snapshot":
                    result = AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, AIBridgeUiAutomation.Snapshot(
                        ReadIntParam(cmd, "maxResults", 100),
                        ReadBoolParam(cmd, "includeDisabled", false)));
                    return true;
                case "runtime.ui.find":
                    result = AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, AIBridgeUiAutomation.Find(
                        ReadStringParam(cmd, "keyword", null),
                        ReadIntParam(cmd, "maxResults", 100),
                        ReadBoolParam(cmd, "includeDisabled", false)));
                    return true;
                case "runtime.ui.raycast":
                    result = AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, AIBridgeUiAutomation.Raycast(
                        ReadFloatParam(cmd, "x", 0f),
                        ReadFloatParam(cmd, "y", 0f),
                        ReadStringParam(cmd, "path", null),
                        ReadIntParam(cmd, "instanceId", 0),
                        ReadIntParam(cmd, "maxResults", 20)));
                    return true;
                case "runtime.ui.click":
                    result = AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, AIBridgeUiAutomation.Click(
                        ReadFloatParam(cmd, "x", 0f),
                        ReadFloatParam(cmd, "y", 0f),
                        ReadStringParam(cmd, "path", null),
                        ReadIntParam(cmd, "instanceId", 0)));
                    return true;
                case RuntimeCodeExecuteAction:
                    return TryHandleRuntimeCodeExecute(cmd, out result, out asyncStarted);
                default:
                    return false;
            }
        }

        private IEnumerator SamplePerformance(AIBridgeRuntimeCommand cmd)
        {
            RuntimePerfResult perfResult = null;
            AIBridgeRuntimeCommandResult finalResult = null;
            RuntimePerfSampler sampler = null;
            IEnumerator sampling = null;

            try
            {
                sampler = new RuntimePerfSampler(cmd);
                sampling = sampler.Sample(_targetId, value => perfResult = value);
            }
            catch (Exception ex)
            {
                finalResult = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "perf_failed: " + ex.Message);
            }

            // 手动驱动采样协程，确保采样异常也能写回 result，避免 CLI 只看到 timeout。
            while (finalResult == null && sampling != null)
            {
                object current = null;
                var moveNext = false;
                try
                {
                    moveNext = sampling.MoveNext();
                    if (moveNext)
                    {
                        current = sampling.Current;
                    }
                }
                catch (Exception ex)
                {
                    finalResult = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "perf_failed: " + ex.Message);
                    break;
                }

                if (!moveNext)
                {
                    break;
                }

                yield return current;
            }

            var samplingDisposable = sampling as IDisposable;
            if (samplingDisposable != null)
            {
                try { samplingDisposable.Dispose(); } catch { }
            }

            if (sampler != null)
            {
                try { sampler.Dispose(); } catch { }
            }

            if (finalResult == null)
            {
                finalResult = perfResult != null
                    ? AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, perfResult)
                    : AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "perf_failed: sampler produced no result.");
            }

            WriteResult(finalResult);
        }

        private IEnumerator CaptureScreenshot(AIBridgeRuntimeCommand cmd)
        {
            yield return new WaitForEndOfFrame();
            AIBridgeRuntimeCommandResult result;
            try { result = AIBridgeRuntimeCommandResult.FromSuccess(cmd.Id, CaptureScreenData()); }
            catch (Exception ex) { result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, $"{ex.GetType().Name}: {ex.Message}"); }
            WriteResult(result);
        }

        private void CleanupOldScreenshots()
        {
            try
            {
                var files = new DirectoryInfo(_screenshotsPath).GetFiles("runtime_screenshot_*.png");
                Array.Sort(files, (left, right) => right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));
                for (var i = MaxRetainedScreenshots; i < files.Length; i++)
                {
                    files[i].Delete();
                }
            }
            catch
            {
            }
        }

        private bool TryHandleRuntimeCodeExecute(AIBridgeRuntimeCommand cmd, out AIBridgeRuntimeCommandResult result, out bool asyncStarted)
        {
            result = null;
            asyncStarted = false;

            string unavailableReason;
            if (!IsRuntimeCodeExecutionAvailable(out unavailableReason))
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, unavailableReason);
                return true;
            }

            if (runtimeSettings == null || !runtimeSettings.enableRuntimeCodeExecution)
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "Runtime code execution is disabled in AIBridgeRuntimeSettings.");
                return true;
            }

            if (!ReadBoolParam(cmd, "riskAccepted", false))
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "runtime.code.execute requires riskAccepted=true.");
                return true;
            }

            var assemblyBase64 = ReadStringParam(cmd, "assemblyBase64", null);
            if (string.IsNullOrWhiteSpace(assemblyBase64))
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "Missing assemblyBase64.");
                return true;
            }

            byte[] assemblyBytes;
            try
            {
                assemblyBytes = Convert.FromBase64String(assemblyBase64);
            }
            catch (Exception ex)
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "Invalid assemblyBase64: " + ex.Message);
                return true;
            }

            if (assemblyBytes.Length <= 0 || assemblyBytes.Length > MaxRuntimeCodeAssemblyBytes)
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(
                    cmd.Id,
                    "Runtime code assembly size is invalid. bytes=" + assemblyBytes.Length.ToString(CultureInfo.InvariantCulture));
                return true;
            }

            var actualSha256 = ComputeSha256(assemblyBytes);
            var expectedSha256 = ReadStringParam(cmd, "sha256", null);
            if (!string.IsNullOrEmpty(expectedSha256)
                && !string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "Assembly sha256 mismatch.");
                return true;
            }

            var startedAtUtc = DateTime.UtcNow;
            RuntimeCodeInvocationContext context = null;
            try
            {
                var assembly = LoadRuntimeCodeAssembly(assemblyBytes, actualSha256);
                var entryTypeName = ReadStringParam(cmd, "entryType", null);
                var methodName = ReadStringParam(cmd, "methodName", null);
                Type entryType;
                var method = ResolveRuntimeCodeMethod(assembly, entryTypeName, methodName, out entryType);
                if (method == null)
                {
                    result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "Runtime code entry method was not found.");
                    return true;
                }

                object[] args;
                string argumentError;
                if (!TryBuildRuntimeCodeArguments(method, cmd, out args, out argumentError))
                {
                    result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, argumentError);
                    return true;
                }

                context = new RuntimeCodeInvocationContext
                {
                    AssemblyName = assembly.GetName().Name,
                    AssemblyBytes = assemblyBytes.Length,
                    Sha256 = actualSha256,
                    EntryType = entryType == null ? null : entryType.FullName,
                    MethodName = method.Name,
                    StartedAtUtc = startedAtUtc
                };

                object returnValue;
                try
                {
                    returnValue = method.Invoke(null, args);
                }
                catch (TargetInvocationException ex)
                {
                    result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, FormatRuntimeCodeException(ex));
                    result.Data = BuildRuntimeCodeResultData(context, null, false, startedAtUtc, BuildRuntimeCodeExceptionData(ex));
                    return true;
                }

                var task = returnValue as Task;
                if (task != null)
                {
                    StartCoroutine(CompleteRuntimeCodeTask(cmd, context, task));
                    asyncStarted = true;
                    return true;
                }

                result = AIBridgeRuntimeCommandResult.FromSuccess(
                    cmd.Id,
                    BuildRuntimeCodeResultData(context, NormalizeRuntimeValue(returnValue), false, startedAtUtc, null));
                return true;
            }
            catch (Exception ex)
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, FormatRuntimeCodeException(ex));
                if (context != null)
                {
                    result.Data = BuildRuntimeCodeResultData(context, null, false, startedAtUtc, BuildRuntimeCodeExceptionData(ex));
                }

                return true;
            }
        }

        // 同一段代码(sha256 相同)复用已加载的 Assembly,避免反复发送相同探针时程序集无限累积。
        // key 使用运行时计算的 sha256,而非客户端声明值,防止伪造命中。已加载的 Assembly 无法卸载,
        // 不同代码仍会累积；到达限额时拒绝新 DLL，清空字典并不能卸载程序集。
        private Assembly LoadRuntimeCodeAssembly(byte[] assemblyBytes, string sha256)
        {
            if (!string.IsNullOrEmpty(sha256)
                && _assemblyCache.TryGetValue(sha256, out var cached)
                && cached != null)
            {
                return cached;
            }

            if (_assemblyCache.Count >= 64)
                throw new InvalidOperationException("Runtime execution assembly limit reached (64). Restart the debug Player before loading more code.");

            var assembly = Assembly.Load(assemblyBytes);
            if (!string.IsNullOrEmpty(sha256))
            {
                _assemblyCache[sha256] = assembly;
            }

            return assembly;
        }

        private static bool IsRuntimeCodeExecutionAvailable(out string reason)
        {
#if !ENABLE_IL2CPP
            // Mono Player 原生支持 Assembly.Load，不要求项目安装 HybridCLR。
            reason = null;
            return true;
#else
            // Editor 使用 Mono 做协议测试；Player 的可用性独立于 Editor 包及其编译宏。
            if (Application.isEditor)
            {
                reason = null;
                return true;
            }
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name == "HybridCLR.Runtime" && assembly.GetType("HybridCLR.RuntimeApi", false) != null)
                {
                    reason = null;
                    return true;
                }
            }
            reason = "Runtime code execution requires a HybridCLR-enabled Player. The native runtime must match the build.";
            return false;
#endif
        }

        private IEnumerator CompleteRuntimeCodeTask(AIBridgeRuntimeCommand cmd, RuntimeCodeInvocationContext context, Task task)
        {
            // 仅对 async Task 路径生效:超时后写回失败并停止协程空转。底层 Task 无法强制中止,可能仍在后台运行。
            // 同步阻塞(while(true)/CPU 密集)会占满主线程、Update 不被调度,此 deadline 形同虚设,属固有风险。
            var timeoutSeconds = runtimeSettings == null ? 0f : runtimeSettings.maxRuntimeCodeExecutionSeconds;
            var deadline = timeoutSeconds > 0f ? Time.realtimeSinceStartup + timeoutSeconds : float.PositiveInfinity;
            while (task != null && !task.IsCompleted)
            {
                if (Time.realtimeSinceStartup >= deadline)
                {
                    var timeoutResult = AIBridgeRuntimeCommandResult.FromFailure(
                        cmd.Id,
                        "Runtime code async task timed out after "
                            + timeoutSeconds.ToString(CultureInfo.InvariantCulture) + "s.");
                    timeoutResult.Data = BuildRuntimeCodeResultData(context, null, true, context.StartedAtUtc, null);
                    WriteResult(timeoutResult);
                    yield break;
                }

                yield return null;
            }

            AIBridgeRuntimeCommandResult result;
            if (task == null)
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "Runtime code async task was null.");
            }
            else if (task.IsFaulted)
            {
                var exception = task.Exception;
                result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, FormatRuntimeCodeException(exception));
                result.Data = BuildRuntimeCodeResultData(context, null, true, context.StartedAtUtc, BuildRuntimeCodeExceptionData(exception));
            }
            else if (task.IsCanceled)
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(cmd.Id, "Runtime code async task was canceled.");
                result.Data = BuildRuntimeCodeResultData(context, null, true, context.StartedAtUtc, null);
            }
            else
            {
                result = AIBridgeRuntimeCommandResult.FromSuccess(
                    cmd.Id,
                    BuildRuntimeCodeResultData(context, NormalizeRuntimeValue(GetTaskResult(task)), true, context.StartedAtUtc, null));
            }

            WriteResult(result);
        }

        private static MethodInfo ResolveRuntimeCodeMethod(Assembly assembly, string entryTypeName, string methodName, out Type entryType)
        {
            entryType = null;
            if (assembly == null)
            {
                return null;
            }

            var candidates = new List<Type>();
            if (!string.IsNullOrWhiteSpace(entryTypeName))
            {
                var explicitType = assembly.GetType(entryTypeName, false);
                if (explicitType != null)
                {
                    candidates.Add(explicitType);
                }
            }

            if (candidates.Count == 0)
            {
                try
                {
                    candidates.AddRange(assembly.GetTypes());
                }
                catch (ReflectionTypeLoadException ex)
                {
                    if (ex.Types != null)
                    {
                        for (var i = 0; i < ex.Types.Length; i++)
                        {
                            if (ex.Types[i] != null)
                            {
                                candidates.Add(ex.Types[i]);
                            }
                        }
                    }
                }
            }

            var preferredNames = string.IsNullOrWhiteSpace(methodName)
                ? new[] { "ExecuteAsync", "Execute" }
                : new[] { methodName };

            for (var typeIndex = 0; typeIndex < candidates.Count; typeIndex++)
            {
                var candidateType = candidates[typeIndex];
                if (candidateType == null)
                {
                    continue;
                }

                for (var nameIndex = 0; nameIndex < preferredNames.Length; nameIndex++)
                {
                    var candidateMethod = candidateType.GetMethod(
                        preferredNames[nameIndex],
                        BindingFlags.Public | BindingFlags.Static);
                    if (candidateMethod != null)
                    {
                        entryType = candidateType;
                        return candidateMethod;
                    }
                }
            }

            return null;
        }

        private static bool TryBuildRuntimeCodeArguments(MethodInfo method, AIBridgeRuntimeCommand cmd, out object[] args, out string error)
        {
            args = null;
            error = null;

            var parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                args = new object[0];
                return true;
            }

            if (parameters.Length != 1)
            {
                error = "Runtime code entry method must accept 0 or 1 parameter.";
                return false;
            }

            var parameterType = parameters[0].ParameterType;
            if (parameterType == typeof(AIBridgeRuntimeCommand))
            {
                args = new object[] { cmd };
                return true;
            }

            if (parameterType == typeof(Dictionary<string, object>))
            {
                args = new object[] { cmd.Params };
                return true;
            }

            if (parameterType == typeof(string))
            {
                args = new object[] { AIBridgeJson.Serialize(cmd.Params, pretty: false) };
                return true;
            }

            error = "Unsupported runtime code entry parameter type: " + parameterType.FullName;
            return false;
        }

        private static object BuildRuntimeCodeResultData(
            RuntimeCodeInvocationContext context,
            object returnValue,
            bool isAsync,
            DateTime startedAtUtc,
            object exception)
        {
            var elapsedMs = Math.Max(0.0, (DateTime.UtcNow - startedAtUtc).TotalMilliseconds);
            return new
            {
                action = RuntimeCodeExecuteAction,
                assemblyName = context == null ? null : context.AssemblyName,
                assemblyBytes = context == null ? 0 : context.AssemblyBytes,
                sha256 = context == null ? null : context.Sha256,
                entryType = context == null ? null : context.EntryType,
                methodName = context == null ? null : context.MethodName,
                isAsync = isAsync,
                elapsedMs = elapsedMs,
                returnValue = returnValue,
                exception = exception
            };
        }

        private static object GetTaskResult(Task task)
        {
            if (task == null)
            {
                return null;
            }

            var type = task.GetType();
            if (!type.IsGenericType)
            {
                return null;
            }

            var property = type.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
            return property == null ? null : property.GetValue(task, null);
        }

        private static string FormatRuntimeCodeException(Exception ex)
        {
            var unwrapped = UnwrapRuntimeCodeException(ex);
            return unwrapped.GetType().Name + ": " + unwrapped.Message;
        }

        private static object BuildRuntimeCodeExceptionData(Exception ex)
        {
            var unwrapped = UnwrapRuntimeCodeException(ex);
            return new
            {
                type = unwrapped.GetType().FullName,
                message = unwrapped.Message,
                stackTrace = RuntimeExceptionDetails.GetStackTrace(unwrapped)
            };
        }

        private static Exception UnwrapRuntimeCodeException(Exception ex)
        {
            if (ex == null)
            {
                return new InvalidOperationException("Unknown runtime code exception.");
            }

            var targetInvocation = ex as TargetInvocationException;
            if (targetInvocation != null && targetInvocation.InnerException != null)
            {
                return UnwrapRuntimeCodeException(targetInvocation.InnerException);
            }

            var aggregate = ex as AggregateException;
            if (aggregate != null && aggregate.InnerException != null)
            {
                return UnwrapRuntimeCodeException(aggregate.InnerException);
            }

            return ex;
        }

        private static object NormalizeRuntimeValue(object value)
        {
            return NormalizeRuntimeValue(value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        private static object NormalizeRuntimeValue(object value, int depth, HashSet<object> visited)
        {
            if (value == null)
            {
                return null;
            }

            if (depth > MaxRuntimeCodeResultDepth)
            {
                return new
                {
                    type = value.GetType().FullName,
                    value = "<max depth reached>"
                };
            }

            var type = value.GetType();
            if (type.IsPrimitive || value is string || value is decimal)
            {
                return value;
            }

            if (value is DateTime dateTime)
            {
                return dateTime.ToString("o", CultureInfo.InvariantCulture);
            }

            if (value is DateTimeOffset dateTimeOffset)
            {
                return dateTimeOffset.ToString("o", CultureInfo.InvariantCulture);
            }

            if (value is TimeSpan timeSpan)
            {
                return timeSpan.ToString();
            }

            if (value is Guid || value is Enum)
            {
                return value.ToString();
            }

            var unityObject = value as UnityEngine.Object;
            if (unityObject != null)
            {
                return new
                {
                    type = unityObject.GetType().FullName,
                    name = unityObject.name,
                    instanceId = unityObject.GetInstanceID()
                };
            }

            if (!TryAddVisited(value, visited))
            {
                return new
                {
                    type = type.FullName,
                    value = "<cycle>"
                };
            }

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var result = new Dictionary<string, object>();
                var count = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (count >= MaxRuntimeCodeCollectionItems)
                    {
                        result["__truncated"] = true;
                        break;
                    }

                    result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = NormalizeRuntimeValue(entry.Value, depth + 1, visited);
                    count++;
                }

                return result;
            }

            var enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                var result = new List<object>();
                var count = 0;
                foreach (var item in enumerable)
                {
                    if (count >= MaxRuntimeCodeCollectionItems)
                    {
                        result.Add("<truncated>");
                        break;
                    }

                    result.Add(NormalizeRuntimeValue(item, depth + 1, visited));
                    count++;
                }

                return result;
            }

            var objectResult = new Dictionary<string, object>
            {
                ["type"] = type.FullName
            };

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                objectResult[field.Name] = NormalizeRuntimeValue(field.GetValue(value), depth + 1, visited);
            }

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            for (var i = 0; i < properties.Length; i++)
            {
                var property = properties[i];
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                try
                {
                    objectResult[property.Name] = NormalizeRuntimeValue(property.GetValue(value, null), depth + 1, visited);
                }
                catch (Exception ex)
                {
                    objectResult[property.Name] = "<" + ex.GetType().Name + ">";
                }
            }

            return objectResult;
        }

        private static bool TryAddVisited(object value, HashSet<object> visited)
        {
            if (value == null)
            {
                return true;
            }

            var type = value.GetType();
            if (type.IsValueType || value is string)
            {
                return true;
            }

            return visited.Add(value);
        }

        private object BuildStatusData()
        {
            var data = new Dictionary<string, object>
            {
                ["targetId"] = _targetId,
                ["protocolVersion"] = 2,
                ["serviceSessionId"] = _serviceSessionId,
                ["transport"] = _httpTransportServer != null && _httpTransportServer.IsRunning ? "http" : "file",
                ["capabilities"] = BuildCapabilitiesData(),
                ["bindUrl"] = BuildLocalHttpUrl(),
                ["reachableUrl"] = BuildLocalHttpUrl(),
                ["httpUrl"] = BuildLocalHttpUrl(),
                ["httpPort"] = GetActualHttpPort(),
                ["httpBindAddress"] = runtimeSettings == null ? null : runtimeSettings.httpBindAddress,
                ["lanDiscoveryUdpPort"] = GetActualLanDiscoveryPort(),
                ["runtimeVersion"] = RuntimeVersion,
                ["productName"] = Application.productName,
                ["applicationVersion"] = Application.version,
                ["unityVersion"] = Application.unityVersion,
                ["buildId"] = Application.buildGUID,
                ["platform"] = Application.platform.ToString(),
                ["deviceName"] = SystemInfo.deviceName,
                ["isEditor"] = Application.isEditor,
                ["isDebugBuild"] = Debug.isDebugBuild,
                ["runInBackground"] = Application.runInBackground,
                ["keepRunningInBackground"] = runtimeSettings != null && runtimeSettings.keepRunningInBackground,
                ["activeScene"] = SceneManager.GetActiveScene().name,
                ["loadedScenes"] = GetLoadedScenes(),
                ["uptimeSeconds"] = (DateTime.UtcNow - _startedAtUtc).TotalSeconds,
                ["frameCount"] = Time.frameCount,
                ["timeScale"] = Time.timeScale,
                ["approximateFps"] = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f,
                ["logBufferCount"] = _logBuffer.Count,
                ["paths"] = new
                {
                    runtimeRoot = _runtimeRootPath,
                    targetPath = _targetPath,
                    commands = _commandsPath,
                    results = _resultsPath,
                    screenshots = _screenshotsPath
                }
            };
            AddRuntimeReadinessData(data);
            return data;
        }

        private object BuildLogsData(AIBridgeRuntimeCommand cmd)
        {
            var clear = string.Equals(cmd.Action, "runtime.logs.clear", StringComparison.OrdinalIgnoreCase)
                || ReadBoolParam(cmd, "clear", false)
                || string.Equals(ReadStringParam(cmd, "operation", null), "clear", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ReadStringParam(cmd, "mode", null), "clear", StringComparison.OrdinalIgnoreCase);

            if (clear)
            {
                var clearedCount = _logBuffer.Clear();
                return new
                {
                    logs = new AIBridgeRuntimeLogEntry[0],
                    count = 0,
                    clearedCount = clearedCount,
                    bufferCount = _logBuffer.Count,
                    targetId = _targetId
                };
            }

            var count = ReadIntParam(cmd, "tail", ReadIntParam(cmd, "count", 50));
            var logType = ReadStringParam(cmd, "logType", "all");
            var regex = ReadStringParam(cmd, "regex", null);
            var includeStackTrace = ReadBoolParam(cmd, "includeStackTrace", false);
            var sinceFrame = ReadNullableIntParam(cmd, "sinceFrame");
            var sinceTimestamp = ReadSinceTimestampParam(cmd);
            var logs = _logBuffer.GetEntries(count, logType, regex, includeStackTrace, sinceFrame, sinceTimestamp);
            return new
            {
                logs = logs,
                count = logs.Length,
                bufferCount = _logBuffer.Count,
                filters = new
                {
                    logType = logType,
                    regex = regex,
                    includeStackTrace = includeStackTrace,
                    tail = count,
                    sinceFrame = sinceFrame,
                    sinceTime = sinceTimestamp
                },
                targetId = _targetId
            };
        }

        private static bool ReadBoolParam(AIBridgeRuntimeCommand cmd, string key, bool defaultValue)
        {
            if (!TryGetCommandParam(cmd, key, out var value) || value == null)
            {
                return defaultValue;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value is int intValue)
            {
                return intValue != 0;
            }

            if (value is long longValue)
            {
                return longValue != 0L;
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "y", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "n", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return bool.TryParse(text, out var parsed) ? parsed : defaultValue;
        }

        private static int ReadIntParam(AIBridgeRuntimeCommand cmd, string key, int defaultValue)
        {
            if (!TryGetCommandParam(cmd, key, out var value) || value == null)
            {
                return defaultValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue)
            {
                return longValue < int.MinValue || longValue > int.MaxValue ? defaultValue : (int)longValue;
            }

            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : defaultValue;
        }

        private static int? ReadNullableIntParam(AIBridgeRuntimeCommand cmd, string key)
        {
            if (!TryGetCommandParam(cmd, key, out var value) || value == null)
            {
                return null;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue)
            {
                return longValue < int.MinValue || longValue > int.MaxValue ? (int?)null : (int)longValue;
            }

            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null;
        }

        private static float ReadFloatParam(AIBridgeRuntimeCommand cmd, string key, float defaultValue)
        {
            if (!TryGetCommandParam(cmd, key, out var value) || value == null)
            {
                return defaultValue;
            }

            if (value is float floatValue)
            {
                return floatValue;
            }

            if (value is double doubleValue)
            {
                return (float)doubleValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue)
            {
                return longValue;
            }

            return float.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : defaultValue;
        }

        private static string ReadStringParam(AIBridgeRuntimeCommand cmd, string key, string defaultValue)
        {
            if (!TryGetCommandParam(cmd, key, out var value) || value == null)
            {
                return defaultValue;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static long? ReadSinceTimestampParam(AIBridgeRuntimeCommand cmd)
        {
            if (TryGetCommandParam(cmd, "sinceTimestamp", out var timestamp)
                || TryGetCommandParam(cmd, "sinceTimeMs", out timestamp))
            {
                return ParseUnixTimestampMilliseconds(timestamp);
            }

            if (!TryGetCommandParam(cmd, "sinceTime", out var sinceTime) || sinceTime == null)
            {
                return null;
            }

            var numericTimestamp = ParseUnixTimestampMilliseconds(sinceTime);
            if (numericTimestamp.HasValue)
            {
                return numericTimestamp;
            }

            var text = Convert.ToString(sinceTime, CultureInfo.InvariantCulture);
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return parsed.ToUnixTimeMilliseconds();
            }

            return null;
        }

        private static long? ParseUnixTimestampMilliseconds(object value)
        {
            if (value == null)
            {
                return null;
            }

            long parsed;
            if (value is long longValue)
            {
                parsed = longValue;
            }
            else if (value is int intValue)
            {
                parsed = intValue;
            }
            else if (value is double doubleValue)
            {
                parsed = (long)Math.Round(doubleValue);
            }
            else if (value is float floatValue)
            {
                parsed = (long)Math.Round(floatValue);
            }
            else if (!long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return null;
            }

            // 允许 Unix 秒输入，内部统一为毫秒。
            return parsed > 0L && parsed < 100000000000L ? parsed * 1000L : parsed;
        }

        private static bool TryGetCommandParam(AIBridgeRuntimeCommand cmd, string key, out object value)
        {
            value = null;
            if (cmd == null || cmd.Params == null || string.IsNullOrEmpty(key))
            {
                return false;
            }

            foreach (var pair in cmd.Params)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }

            return false;
        }

        private List<object> GetLoadedScenes()
        {
            var scenes = new List<object>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes.Add(new
                {
                    name = scene.name,
                    path = scene.path,
                    buildIndex = scene.buildIndex,
                    isLoaded = scene.isLoaded,
                    rootCount = scene.rootCount
                });
            }

            return scenes;
        }

        private List<object> BuildHandlerSummaries(bool includeBuiltIns)
        {
            var handlers = new List<object>();

            if (includeBuiltIns)
            {
                handlers.Add(new
                {
                    handler = "AIBridgeRuntime.BuiltIn",
                    async = false,
                    actions = BuiltInActions
                });
            }

            for (var i = 0; i < _handlers.Count; i++)
            {
                var handler = _handlers[i];
                if (handler == null)
                {
                    continue;
                }

                handlers.Add(new
                {
                    handler = handler.GetType().FullName,
                    async = false,
                    actions = handler.SupportedActions
                });
            }

            for (var i = 0; i < _asyncHandlers.Count; i++)
            {
                var handler = _asyncHandlers[i];
                if (handler == null)
                {
                    continue;
                }

                handlers.Add(new
                {
                    handler = handler.GetType().FullName,
                    async = true,
                    actions = handler.SupportedActions
                });
            }

            return handlers;
        }

        private bool ValidateCommand(AIBridgeRuntimeCommand cmd, out string error)
        {
            error = null;
            if (cmd == null)
            {
                error = "Runtime command is null.";
                return false;
            }

            if (!IsValidCommandId(cmd.Id))
            {
                error = "Runtime command id is invalid.";
                return false;
            }

            if (string.IsNullOrEmpty(cmd.Action))
            {
                error = "Runtime command action is required.";
                return false;
            }

            if (!string.IsNullOrEmpty(runtimeSettings.authToken)
                && !string.Equals(runtimeSettings.authToken, cmd.Token, StringComparison.Ordinal))
            {
                error = "Runtime command token is invalid.";
                return false;
            }

            return true;
        }

        private bool IsCustomActionAllowed(string action)
        {
            if (string.Equals(action, RuntimeCodeExecuteAction, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IsBuiltInAction(action))
            {
                // When allowedActions is configured, built-in actions must also be explicitly listed to be allowed.
                if (runtimeSettings.allowedActions != null && runtimeSettings.allowedActions.Length > 0)
                {
                    return runtimeSettings.IsActionExplicitlyAllowed(action);
                }

                return true;
            }

            if (runtimeSettings.IsActionExplicitlyAllowed(action))
            {
                return true;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return runtimeSettings.allowedActions == null || runtimeSettings.allowedActions.Length == 0;
#else
            return false;
#endif
        }

        private bool IsRuntimeBridgeEnabled()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return runtimeSettings != null && runtimeSettings.enableRuntimeBridge;
#else
            return false;
#endif
        }

        private static bool IsBuiltInAction(string action)
        {
            for (var i = 0; i < BuiltInActions.Length; i++)
            {
                if (string.Equals(BuiltInActions[i], action, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsValidCommandId(string commandId)
        {
            if (string.IsNullOrEmpty(commandId) || commandId.Length > MaxCommandIdLength)
            {
                return false;
            }

            for (var i = 0; i < commandId.Length; i++)
            {
                var c = commandId[i];
                if ((c < 'a' || c > 'z')
                    && (c < 'A' || c > 'Z')
                    && (c < '0' || c > '9')
                    && c != '_'
                    && c != '-')
                {
                    return false;
                }
            }

            return true;
        }

        // File 传输下 CLI 超时或异步晚到的 result 文件无人取走会持续堆积;低频清理超龄文件。
        // 保守原则:仅删确实超龄者,读取修改时间或删除失败均跳过,宁可漏删不可错删。
        // 已知局限:若 CLI --timeout 大于 orphanResultRetentionSeconds,仍可能误删未读结果(UI 注明)。
        private void CleanupOrphanResultsIfDue()
        {
            const float CleanupIntervalSeconds = 30f;
            var retentionSeconds = runtimeSettings == null ? 0f : runtimeSettings.orphanResultRetentionSeconds;
            if (retentionSeconds <= 0f)
            {
                return;
            }

            if (Time.realtimeSinceStartup - _lastOrphanCleanupTime < CleanupIntervalSeconds)
            {
                return;
            }

            _lastOrphanCleanupTime = Time.realtimeSinceStartup;

            if (string.IsNullOrEmpty(_resultsPath) || !Directory.Exists(_resultsPath))
            {
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(_resultsPath, "*.json");
            }
            catch
            {
                return;
            }

            var threshold = DateTime.UtcNow.AddSeconds(-retentionSeconds);
            for (var i = 0; i < files.Length; i++)
            {
                var file = files[i];
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < threshold)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                }
            }
        }

        private void WriteHeartbeatIfDue()
        {
            var interval = runtimeSettings == null ? 1f : Mathf.Max(0.1f, runtimeSettings.heartbeatIntervalSeconds);
            if (Time.realtimeSinceStartup - _lastHeartbeatTime < interval)
            {
                return;
            }

            WriteHeartbeat();
        }

        private void WriteHeartbeat()
        {
            _lastHeartbeatTime = Time.realtimeSinceStartup;
            var heartbeat = new Dictionary<string, object>
            {
                ["targetId"] = _targetId,
                ["protocolVersion"] = 2,
                ["transport"] = "file",
                ["capabilities"] = BuildCapabilitiesData(),
                ["bindUrl"] = BuildLocalHttpUrl(),
                ["reachableUrl"] = BuildLocalHttpUrl(),
                ["httpUrl"] = BuildLocalHttpUrl(),
                ["httpPort"] = GetActualHttpPort(),
                ["httpBindAddress"] = runtimeSettings == null ? null : runtimeSettings.httpBindAddress,
                ["lanDiscoveryUdpPort"] = GetActualLanDiscoveryPort(),
                ["runtimeVersion"] = RuntimeVersion,
                ["productName"] = Application.productName,
                ["applicationVersion"] = Application.version,
                ["unityVersion"] = Application.unityVersion,
                ["buildId"] = Application.buildGUID,
                ["platform"] = Application.platform.ToString(),
                ["deviceName"] = SystemInfo.deviceName,
                ["isEditor"] = Application.isEditor,
                ["isDebugBuild"] = Debug.isDebugBuild,
                ["runInBackground"] = Application.runInBackground,
                ["keepRunningInBackground"] = runtimeSettings != null && runtimeSettings.keepRunningInBackground,
                ["activeScene"] = SceneManager.GetActiveScene().name,
                ["uptimeSeconds"] = (DateTime.UtcNow - _startedAtUtc).TotalSeconds,
                ["lastHeartbeatUtc"] = DateTime.UtcNow.ToString("o"),
                ["processId"] = TryGetProcessId(),
                ["runtimeRoot"] = _runtimeRootPath,
                ["targetPath"] = _targetPath,
                ["commandsPath"] = _commandsPath,
                ["resultsPath"] = _resultsPath,
                ["screenshotsPath"] = _screenshotsPath
            };
            AddRuntimeReadinessData(heartbeat);

            try
            {
                WriteTextAtomic(_heartbeatPath, AIBridgeJson.Serialize(heartbeat, pretty: true));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AIBridgeRuntime] Failed to write heartbeat: {ex.Message}");
            }
        }

        private void ApplyRunInBackgroundIfNeeded()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (runtimeSettings == null || !runtimeSettings.keepRunningInBackground)
            {
                return;
            }

            _previousRunInBackground = Application.runInBackground;
            if (Application.runInBackground)
            {
                return;
            }

            // Runtime Bridge 依赖帧循环刷新 heartbeat 和处理命令，失焦后保持运行可避免 CLI 误判超时。
            Application.runInBackground = true;
            _runInBackgroundChanged = true;
            LogDebug("Enabled Application.runInBackground for Runtime Bridge.");
#endif
        }

        private void RestoreRunInBackgroundIfNeeded()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!_runInBackgroundChanged)
            {
                return;
            }

            if (Application.runInBackground)
            {
                Application.runInBackground = _previousRunInBackground;
            }

            _runInBackgroundChanged = false;
#endif
        }

        public void ShutdownRuntimeBridge(string reason = null)
        {
            StopRuntimeServices(string.IsNullOrEmpty(reason) ? "shutdown" : reason);
        }

        private void MarkRuntimeServicesRunning()
        {
            if (_runtimeServicesStopping) _serviceSessionId = Guid.NewGuid().ToString("N");
            _runtimeServicesStopping = false;
            _runtimeStopReason = null;
            lock (_pendingHttpResultsSyncRoot)
            {
                _pendingHttpResults.Clear();
                _httpCommandIds.Clear();
                _executingHttpCommandIds.Clear();
            }

            MarkMainThreadTick();
        }

        private void MarkMainThreadTick()
        {
            Interlocked.Exchange(ref _lastMainThreadTickUtcTicks, DateTime.UtcNow.Ticks);
        }

        private void StopRuntimeServices(string reason)
        {
            _runtimeTools?.Dispose();
            _runtimeServicesStopping = true;
            _runtimeStopReason = string.IsNullOrEmpty(reason) ? "stopping" : reason;
            FailPendingHttpCommands("runtime_not_ready: " + _runtimeStopReason);
            StopLanDiscovery();
            StopHttpTransport();
            RestoreRunInBackgroundIfNeeded();
        }

        internal bool IsCommandPumpReady(out string reason, out long mainThreadTickAgeMs)
        {
            reason = null;
            mainThreadTickAgeMs = GetMainThreadTickAgeMs();

            if (!_initialized)
            {
                reason = "runtime_not_initialized";
                return false;
            }

            if (_runtimeServicesStopping)
            {
                reason = string.IsNullOrEmpty(_runtimeStopReason)
                    ? "runtime_stopping"
                    : "runtime_stopping: " + _runtimeStopReason;
                return false;
            }

            if (Interlocked.CompareExchange(ref _processingCommands, 0, 0) > 0)
            {
                return true;
            }

            if (mainThreadTickAgeMs > CommandPumpStaleMilliseconds)
            {
                reason = "command_pump_stale";
                return false;
            }

            return true;
        }

        private long GetMainThreadTickAgeMs()
        {
            var ticks = Interlocked.CompareExchange(ref _lastMainThreadTickUtcTicks, 0L, 0L);
            if (ticks <= 0L)
            {
                return long.MaxValue;
            }

            var age = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
            if (age.TotalMilliseconds < 0d)
            {
                return 0L;
            }

            return (long)age.TotalMilliseconds;
        }

        private string GetLastMainThreadTickUtc()
        {
            var ticks = Interlocked.CompareExchange(ref _lastMainThreadTickUtcTicks, 0L, 0L);
            return ticks <= 0L ? null : new DateTime(ticks, DateTimeKind.Utc).ToString("o");
        }

        private Dictionary<string, object> BuildRuntimeReadinessData()
        {
            string reason;
            long ageMs;
            var ready = IsCommandPumpReady(out reason, out ageMs);
            var runtimeState = ready ? "running" : (_runtimeServicesStopping ? "stopping" : "not_ready");
            return new Dictionary<string, object>
            {
                ["ready"] = ready,
                ["runtimeState"] = runtimeState,
                ["commandPumpReady"] = ready,
                ["commandPumpReason"] = ready ? null : reason,
                ["commandPumpStaleThresholdMs"] = CommandPumpStaleMilliseconds,
                ["lastMainThreadTickUtc"] = GetLastMainThreadTickUtc(),
                ["lastMainThreadTickAgeMs"] = ageMs == long.MaxValue ? (object)null : ageMs,
                ["processingCommands"] = Interlocked.CompareExchange(ref _processingCommands, 0, 0),
                ["stopReason"] = _runtimeStopReason
            };
        }

        private void AddRuntimeReadinessData(Dictionary<string, object> data)
        {
            if (data == null)
            {
                return;
            }

            var readiness = BuildRuntimeReadinessData();
            foreach (var pair in readiness)
            {
                data[pair.Key] = pair.Value;
            }
        }

        private void FailPendingHttpCommands(string error)
        {
            lock (_pendingHttpResultsSyncRoot)
            {
                if (_httpCommandIds.Count == 0)
                {
                    return;
                }

                var commandIds = new List<string>(_httpCommandIds);
                for (var i = 0; i < commandIds.Count; i++)
                {
                    var commandId = commandIds[i];
                    _pendingHttpResults[commandId] = AIBridgeRuntimeCommandResult.FromFailure(commandId, error);
                    if (!_executingHttpCommandIds.Contains(commandId))
                    {
                        _cancelledHttpCommandIds.Add(commandId);
                    }
                }

                Monitor.PulseAll(_pendingHttpResultsSyncRoot);
            }
        }

        private void WriteResult(AIBridgeRuntimeCommandResult result)
        {
            if (result == null || string.IsNullOrEmpty(_resultsPath))
            {
                return;
            }

            if (!IsValidCommandId(result.CommandId))
            {
                Debug.LogError("[AIBridgeRuntime] Refused to write result with invalid command id.");
                return;
            }

            try
            {
                var fileName = $"{result.CommandId}.json";
                var filePath = Path.Combine(_resultsPath, fileName);
                var json = AIBridgeJson.Serialize(result, pretty: true);
                var maxBytes = runtimeSettings == null ? 0 : runtimeSettings.maxResultBytes;
                if (maxBytes > 0 && Encoding.UTF8.GetByteCount(json) > maxBytes)
                {
                    result = AIBridgeRuntimeCommandResult.FromFailure(result.CommandId, "Runtime command result exceeded maxResultBytes.");
                    json = AIBridgeJson.Serialize(result, pretty: false);
                }

                CacheHttpResult(result);
                WriteTextAtomic(filePath, json);
                LogDebug($"Wrote result: {fileName}");
            }
            catch (Exception e)
            {
                CacheHttpResult(result);
                Debug.LogError($"[AIBridgeRuntime] Failed to write result: {e.Message}");
            }
        }

        private string ResolveRuntimeRootPath()
        {
            var configuredPath = runtimeSettings == null ? null : runtimeSettings.exchangeDirectory;
            if (!string.IsNullOrEmpty(configuredPath))
            {
                return Path.GetFullPath(configuredPath);
            }

            var argPath = GetCommandLineArgValue(RuntimeDirArgument);
            if (!string.IsNullOrEmpty(argPath))
            {
                return Path.GetFullPath(argPath);
            }

            var envPath = Environment.GetEnvironmentVariable(RuntimeDirEnvironment);
            if (!string.IsNullOrEmpty(envPath))
            {
                return Path.GetFullPath(envPath);
            }

            var projectRoot = TryResolveEditorProjectRoot();
            if (!string.IsNullOrEmpty(projectRoot))
            {
                return Path.Combine(projectRoot, ".aibridge", RuntimeDirectoryName);
            }

            return Path.Combine(Application.persistentDataPath, ".aibridge", RuntimeDirectoryName);
        }

        private string ResolveTargetId()
        {
            var configuredTarget = runtimeSettings == null ? null : runtimeSettings.targetId;
            if (!string.IsNullOrEmpty(configuredTarget))
            {
                return SanitizeTargetId(configuredTarget);
            }

            var argTarget = GetCommandLineArgValue(TargetIdArgument);
            if (!string.IsNullOrEmpty(argTarget))
            {
                return SanitizeTargetId(argTarget);
            }

            var envTarget = Environment.GetEnvironmentVariable(TargetIdEnvironment);
            if (!string.IsNullOrEmpty(envTarget))
            {
                return SanitizeTargetId(envTarget);
            }

            var processId = TryGetProcessId();
            var productName = string.IsNullOrEmpty(Application.productName) ? "player" : Application.productName;
            return SanitizeTargetId(productName + "_" + processId);
        }

        private static string TryResolveEditorProjectRoot()
        {
            var dataPath = Application.dataPath;
            if (string.IsNullOrEmpty(dataPath))
            {
                return null;
            }

            var normalized = dataPath.Replace('\\', '/');
            if (!normalized.EndsWith("/Assets", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return dataPath.Substring(0, dataPath.Length - "/Assets".Length);
        }

        private static string GetCommandLineArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                var prefix = name + "=";
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring(prefix.Length);
                }
            }

            return null;
        }

        private static string SanitizeTargetId(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "player";
            }

            var builder = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                {
                    builder.Append(c);
                }
                else
                {
                    builder.Append('_');
                }
            }

            return builder.ToString();
        }

        private static int TryGetProcessId()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch
            {
                return 0;
            }
        }

        internal string TargetId => _targetId;

        internal bool IsHttpTransportEnabled()
        {
            return runtimeSettings != null && runtimeSettings.enableHttpTransport;
        }

        internal bool IsLanDiscoveryEnabled()
        {
            return runtimeSettings != null && runtimeSettings.enableLanDiscovery && IsHttpTransportEnabled();
        }

        internal Dictionary<string, object> BuildHttpHealthData()
        {
            // HTTP 请求在后台线程处理，health 只读取运行时缓存字段，避免跨线程访问 Unity API。
            var health = new Dictionary<string, object>
            {
                ["targetId"] = _targetId,
                ["protocolVersion"] = 2,
                ["transport"] = "http",
                ["bindUrl"] = BuildLocalHttpUrl(),
                ["reachableUrl"] = BuildLocalHttpUrl(),
                ["httpUrl"] = BuildLocalHttpUrl(),
                ["httpPort"] = GetActualHttpPort(),
                ["httpBindAddress"] = runtimeSettings == null ? null : runtimeSettings.httpBindAddress,
                ["runtimeVersion"] = RuntimeVersion,
                ["productName"] = _cachedProductName,
                ["applicationVersion"] = _cachedApplicationVersion,
                ["unityVersion"] = _cachedUnityVersion,
                ["buildId"] = _cachedBuildId,
                ["platform"] = _cachedPlatform,
                ["deviceName"] = _cachedDeviceName,
                ["isEditor"] = _cachedIsEditor,
                ["isDebugBuild"] = _cachedIsDebugBuild,
                ["uptimeSeconds"] = (DateTime.UtcNow - _startedAtUtc).TotalSeconds,
                ["lastHeartbeatUtc"] = DateTime.UtcNow.ToString("o"),
                ["capabilities"] = BuildCapabilitiesData()
            };
            AddRuntimeReadinessData(health);
            return health;
        }

        private void CacheMainThreadRuntimeInfo()
        {
            // 这些 Unity API 只能在主线程读取；HTTP health 由后台线程处理，只能使用这里缓存的稳定元信息。
            _cachedProductName = Application.productName;
            _cachedApplicationVersion = Application.version;
            _cachedUnityVersion = Application.unityVersion;
            _cachedBuildId = Application.buildGUID;
            _cachedPlatform = Application.platform.ToString();
            _cachedDeviceName = SystemInfo.deviceName;
            _cachedIsEditor = Application.isEditor;
            _cachedIsDebugBuild = Debug.isDebugBuild;
        }

        internal bool EnqueueHttpCommand(AIBridgeRuntimeCommand command, out string error)
        {
            error = null;
            if (command == null)
            {
                error = "invalid_command";
                return false;
            }

            string reason;
            long ageMs;
            if (!IsCommandPumpReady(out reason, out ageMs))
            {
                error = reason;
                return false;
            }

            if (!string.IsNullOrEmpty(command.Id))
            {
                lock (_pendingHttpResultsSyncRoot)
                {
                    _httpCommandIds.Add(command.Id);
                }
            }

            lock (_commandQueue)
            {
                _commandQueue.Enqueue(command);
            }

            return true;
        }

        internal bool TryGetHttpResult(string commandId, bool remove, out AIBridgeRuntimeCommandResult result)
        {
            result = null;
            if (string.IsNullOrEmpty(commandId))
            {
                return false;
            }

            lock (_pendingHttpResultsSyncRoot)
            {
                if (!_pendingHttpResults.TryGetValue(commandId, out result))
                {
                    return false;
                }

                if (remove)
                {
                    _pendingHttpResults.Remove(commandId);
                    _httpCommandIds.Remove(commandId);
                    _executingHttpCommandIds.Remove(commandId);
                }

                return true;
            }
        }

        internal AIBridgeHttpCommandState GetHttpCommandState(string commandId)
        {
            if (string.IsNullOrEmpty(commandId))
            {
                return AIBridgeHttpCommandState.NotFound;
            }

            lock (_pendingHttpResultsSyncRoot)
            {
                if (_pendingHttpResults.ContainsKey(commandId))
                {
                    return AIBridgeHttpCommandState.Completed;
                }

                if (_executingHttpCommandIds.Contains(commandId))
                {
                    return AIBridgeHttpCommandState.Executing;
                }

                if (_httpCommandIds.Contains(commandId))
                {
                    return AIBridgeHttpCommandState.Queued;
                }

                return _cancelledHttpCommandIds.Contains(commandId)
                    ? AIBridgeHttpCommandState.Cancelled
                    : AIBridgeHttpCommandState.NotFound;
            }
        }

        internal AIBridgeHttpCommandState CancelQueuedHttpCommand(string commandId)
        {
            if (string.IsNullOrEmpty(commandId))
            {
                return AIBridgeHttpCommandState.NotFound;
            }

            lock (_pendingHttpResultsSyncRoot)
            {
                if (_pendingHttpResults.ContainsKey(commandId))
                {
                    return AIBridgeHttpCommandState.Completed;
                }

                if (_executingHttpCommandIds.Contains(commandId))
                {
                    return AIBridgeHttpCommandState.Executing;
                }

                if (!_httpCommandIds.Remove(commandId))
                {
                    return _cancelledHttpCommandIds.Contains(commandId)
                        ? AIBridgeHttpCommandState.Cancelled
                        : AIBridgeHttpCommandState.NotFound;
                }

                _pendingHttpResults.Remove(commandId);
                _cancelledHttpCommandIds.Add(commandId);
                return AIBridgeHttpCommandState.Cancelled;
            }
        }

        private bool TryBeginCommand(AIBridgeRuntimeCommand command)
        {
            if (command == null || string.IsNullOrEmpty(command.Id))
            {
                return true;
            }

            lock (_pendingHttpResultsSyncRoot)
            {
                if (_cancelledHttpCommandIds.Remove(command.Id))
                {
                    if (!_pendingHttpResults.ContainsKey(command.Id))
                    {
                        _httpCommandIds.Remove(command.Id);
                    }

                    return false;
                }

                if (_httpCommandIds.Contains(command.Id))
                {
                    _executingHttpCommandIds.Add(command.Id);
                }

                return true;
            }
        }

        internal bool TryReadHttpScreenshotArtifact(string filename, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(filename) || string.IsNullOrEmpty(_screenshotsPath))
            {
                return false;
            }

            var safeName = Path.GetFileName(filename);
            if (string.IsNullOrEmpty(safeName) || !string.Equals(safeName, filename, StringComparison.Ordinal))
            {
                return false;
            }

            var path = Path.Combine(_screenshotsPath, safeName);
            if (!File.Exists(path))
            {
                return false;
            }

            bytes = File.ReadAllBytes(path);
            return true;
        }

        private string BuildLocalHttpUrl()
        {
            if (_httpTransportServer == null || !_httpTransportServer.IsRunning)
            {
                return null;
            }

            return _httpTransportServer.Url;
        }

        internal int GetActualHttpPort()
        {
            if (_httpTransportServer != null && _httpTransportServer.IsRunning)
            {
                return _httpTransportServer.Port;
            }

            return runtimeSettings == null ? 0 : runtimeSettings.httpPort;
        }

        internal int GetActualLanDiscoveryPort()
        {
            if (_lanDiscoveryServer != null && _lanDiscoveryServer.IsRunning)
            {
                return _lanDiscoveryServer.Port;
            }

            return runtimeSettings == null ? 0 : runtimeSettings.discoveryUdpPort;
        }

        private void StartHttpTransportIfNeeded()
        {
            if (!IsHttpTransportEnabled())
            {
                return;
            }

            if (_httpTransportServer != null)
            {
                return;
            }

            HttpRuntimeTransportServer server = null;
            try
            {
                server = new HttpRuntimeTransportServer(this, runtimeSettings);
                server.Start();
                _httpTransportServer = server;
            }
            catch (Exception ex)
            {
                if (server != null)
                {
                    server.Dispose();
                }

                Debug.LogError("[AIBridgeRuntime] Failed to start HTTP transport: " + ex.Message);
                _httpTransportServer = null;
            }
        }

        private void StopHttpTransport()
        {
            if (_httpTransportServer == null)
            {
                return;
            }

            _httpTransportServer.Dispose();
            _httpTransportServer = null;
        }

        private void StartLanDiscoveryIfNeeded()
        {
            if (!IsLanDiscoveryEnabled())
            {
                return;
            }

            if (_lanDiscoveryServer != null)
            {
                return;
            }

            LanRuntimeDiscoveryServer server = null;
            try
            {
                server = new LanRuntimeDiscoveryServer(this, runtimeSettings);
                server.Start();
                _lanDiscoveryServer = server;
            }
            catch (Exception ex)
            {
                if (server != null)
                {
                    server.Dispose();
                }

                Debug.LogError("[AIBridgeRuntime] Failed to start LAN discovery: " + ex.Message);
                _lanDiscoveryServer = null;
            }
        }

        private void StopLanDiscovery()
        {
            if (_lanDiscoveryServer == null)
            {
                return;
            }

            _lanDiscoveryServer.Dispose();
            _lanDiscoveryServer = null;
        }

        private void CacheHttpResult(AIBridgeRuntimeCommandResult result)
        {
            if (result == null || string.IsNullOrEmpty(result.CommandId))
            {
                return;
            }

            lock (_pendingHttpResultsSyncRoot)
            {
                if (!_httpCommandIds.Contains(result.CommandId))
                {
                    return;
                }

                _executingHttpCommandIds.Remove(result.CommandId);
                _pendingHttpResults[result.CommandId] = result;
                Monitor.PulseAll(_pendingHttpResultsSyncRoot);
            }
        }

        internal Dictionary<string, object> BuildCapabilitiesData()
        {
            string runtimeCodeUnavailableReason;
            var runtimeCodeAvailable = IsRuntimeCodeExecutionAvailable(out runtimeCodeUnavailableReason);
            if (runtimeCodeAvailable && (runtimeSettings == null || !runtimeSettings.enableRuntimeCodeExecution))
            {
                runtimeCodeAvailable = false;
                runtimeCodeUnavailableReason = "Runtime code execution is disabled in AIBridgeRuntimeSettings.";
            }

            return new Dictionary<string, object>
            {
                ["perf"] = true,
                ["diagnose"] = true,
                ["screenshotPull"] = true,
                ["observe"] = true,
                ["logsClear"] = true,
                ["runtimeCodeExecute"] = runtimeCodeAvailable,
                ["runtimeCodeExecuteReason"] = runtimeCodeAvailable ? null : runtimeCodeUnavailableReason,
                ["file"] = true,
                ["http"] = _httpTransportServer != null && _httpTransportServer.IsRunning,
                ["lanDiscovery"] = _lanDiscoveryServer != null && _lanDiscoveryServer.IsRunning,
                ["ws"] = false
            };
        }

        private static string ComputeSha256(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                for (var i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        private static void WriteTextAtomic(string path, string text)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 先写临时文件再替换，避免 CLI 读到半截 JSON。
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, text, Encoding.UTF8);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }

        private void LogDebug(string message)
        {
            if (enableDebugLog)
            {
                Debug.Log($"[AIBridgeRuntime] {message}");
            }
        }

        private sealed class RuntimeCodeInvocationContext
        {
            public string AssemblyName;
            public int AssemblyBytes;
            public string Sha256;
            public string EntryType;
            public string MethodName;
            public DateTime StartedAtUtc;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
