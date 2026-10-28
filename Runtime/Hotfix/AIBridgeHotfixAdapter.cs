using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AIBridge.Runtime
{
    internal sealed class AIBridgeHotfixAdapter
    {
        private readonly Type _runtimeApi;
        private readonly Type _hotfixType;
        private readonly Type _hotfixMethod;
        private readonly MethodInfo _apply;

        private AIBridgeHotfixAdapter(Type runtimeApi, Type hotfixType, Type hotfixMethod, MethodInfo apply)
        {
            _runtimeApi = runtimeApi;
            _hotfixType = hotfixType;
            _hotfixMethod = hotfixMethod;
            _apply = apply;
        }

        internal static AIBridgeHotfixAdapter Resolve()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => candidate.GetName().Name == "HybridCLR.Runtime");
            var api = assembly?.GetType("HybridCLR.RuntimeApi", false);
            var type = assembly?.GetType("HybridCLR.HotfixType", false);
            var method = assembly?.GetType("HybridCLR.HotfixMethod", false);
            if (api == null || type == null || method == null)
                throw new InvalidOperationException("HybridCLR commercial Hotfix API is unavailable or stripped from this Player.");

            var listType = typeof(List<>).MakeGenericType(type);
            var apply = api.GetMethod("HotfixAssembly", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(byte[]), listType }, null);
            if (apply == null)
                throw new InvalidOperationException("This HybridCLR edition does not expose a compatible HotfixAssembly API.");
            return new AIBridgeHotfixAdapter(api, type, method, apply);
        }

        internal void DisableMethodInlining()
        {
            var optionType = _runtimeApi.Assembly.GetType("HybridCLR.RuntimeOptionId", true);
            var setter = _runtimeApi.GetMethod("SetRuntimeOption", BindingFlags.Public | BindingFlags.Static, null,
                new[] { optionType, typeof(int) }, null);
            if (setter == null)
                throw new InvalidOperationException("HybridCLR SetRuntimeOption API is unavailable.");
            var option = Enum.Parse(optionType, "MaxMethodInlineDepth", false);
            setter.Invoke(null, new[] { option, (object)0 });
        }

        internal Assembly ResolveTarget(AIBridgePatchRequest request)
        {
            var targets = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name == request.AssemblyName).ToArray();
            if (targets.Length != 1)
                throw new InvalidOperationException("Exactly one loaded target assembly is required: " + request.AssemblyName);
            var target = targets[0];
            if (request.BaselineMvid == Guid.Empty || target.ManifestModule.ModuleVersionId != request.BaselineMvid)
                throw new InvalidOperationException("baselineMvid does not match the loaded target assembly; rebuild the patch against this Player.");
            return target;
        }

        internal IList BuildManifest(AIBridgePatchRequest request, Assembly target)
        {
            var types = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_hotfixType));
            foreach (var selected in request.Types)
            {
                var targetType = target.GetType(selected.Name, false);
                if (targetType == null)
                    throw new InvalidOperationException("Target type is not loaded: " + selected.Name);
                var entry = Activator.CreateInstance(_hotfixType);
                SetField(entry, "name", selected.Name);
                var methods = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_hotfixMethod));
                foreach (var signature in selected.Signatures)
                    methods.Add(BuildMethod(targetType, signature));
                SetField(entry, "methods", methods);
                types.Add(entry);
            }
            return types;
        }

        private object BuildMethod(Type target, string signature)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var candidates = new List<MethodBase>(target.GetMethods(flags));
            // HybridCLR 的托管包装只对公开实例构造函数支持 signature 选择。
            candidates.AddRange(target.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            if (target.TypeInitializer != null)
                candidates.Add(target.TypeInitializer);
            var matches = candidates.Where(method => GetSignature(method) == signature).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("Expected one declared target method matching '" + signature + "' on " + target.FullName);

            var selected = matches[0];
            if (selected.IsAbstract || (selected.Attributes & MethodAttributes.PinvokeImpl) != 0)
                throw new InvalidOperationException("Only methods with managed implementations can be selected: " + signature);
            var entry = Activator.CreateInstance(_hotfixMethod);
            if (selected.IsStatic && selected is ConstructorInfo)
                SetField(entry, "name", ".cctor");
            else
                // 8.11.1 的构造函数包装实际按完整 ToString() 匹配，保留请求侧简写但发送 Void 前缀。
                SetField(entry, "signature", selected is ConstructorInfo ? selected.ToString() : signature);
            return entry;
        }

        private static string GetSignature(MethodBase method)
        {
            var signature = method.ToString();
            return method is ConstructorInfo && signature.StartsWith("Void ", StringComparison.Ordinal)
                ? signature.Substring(5) : signature;
        }

        internal void Apply(AIBridgePatchRequest request, IList manifest)
        {
            _apply.Invoke(null, new object[] { request.AssemblyName, request.AssemblyBytes, manifest });
        }

        private static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
                throw new InvalidOperationException("HybridCLR manifest field is unavailable: " + name);
            field.SetValue(target, value);
        }
    }
}
