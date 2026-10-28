using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;
using UnityEngine;

namespace AIBridge.Runtime.Editor
{
    /// <summary>从现有项目配置接入调试能力，不添加业务启动脚本或改写热更列表。</summary>
    public static class AIBridgeRuntimeProjectIntegration
    {
        /// <summary>启用插件自身的开发包启动配置，可通过菜单或 batchmode executeMethod 调用。</summary>
        [MenuItem("Window/AI Bridge Runtime/Prepare Current Project", false, 0)]
        public static void PrepareCurrentProject()
        {
            var config = AIBridgeRuntimeSettingsWindow.CreateConfiguration();
            Undo.RecordObject(config, "Prepare AI Bridge Runtime");
            config.AutoStart = true;
            config.Settings.enableRuntimeBridge = true;
            config.Settings.enableRuntimeCodeExecution = true;
            config.Settings.enableHttpTransport = true;
            if (string.IsNullOrWhiteSpace(config.Settings.authToken)) config.Settings.authToken = Guid.NewGuid().ToString("N");
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssetIfDirty(config);
            Debug.Log("[AI Bridge Runtime] 项目已准备。使用项目原有流程构建 Development Player；无需场景组件或额外 C#。\n" + Describe());
        }

        internal static string Describe()
        {
            var settings = FindHybridSettings();
            if (settings == null) return "未发现 HybridCLR：基础调试按 Player 能力启用，IL2CPP 动态代码及方法热修复需要对应的原生支持。";
            var interpreter = ReadNames(settings, "hotUpdateAssemblies").Concat(ReadDefinitionNames(settings)).Distinct().ToArray();
            var dhe = ReadNames(settings, "differentialHybridAssemblies");
            var local = ResolveLocalIl2Cpp();
            return "解释器程序集：" + (interpreter.Length == 0 ? "未配置" : string.Join(", ", interpreter))
                + "\nDHE 程序集：" + (dhe.Length == 0 ? "未配置" : string.Join(", ", dhe))
                + "\n原生工具链：" + (local != null && Directory.Exists(Path.Combine(local, "libil2cpp", "hybridclr"))
                    ? "已发现；开发构建时自动准备身份读取支持。" : "未发现项目本地安装；请先完成项目自己的 HybridCLR 原生安装。")
                + "\n普通 AOT 程序集不会被自动转换成热更程序集。";
        }

        internal static string[] ConfiguredPatchAssemblies()
        {
            var settings = FindHybridSettings();
            return settings == null ? Array.Empty<string>() : ReadNames(settings, "hotUpdateAssemblies")
                .Concat(ReadDefinitionNames(settings)).Concat(ReadNames(settings, "differentialHybridAssemblies"))
                .Distinct(StringComparer.Ordinal).ToArray();
        }

        // UnityLinker 已收到本轮 Player 编译产物，此处准备 native 输入，早于 IL2CPP 编译。
        internal static void PrepareNativeBuild(BuildReport report)
        {
            // 旧索引无法清除时必须停止构建，不能把上一个包的身份误带入当前包。
            var local = ResolveLocalIl2Cpp();
            if (local != null) AIBridgeRuntimeNativeSupport.ClearGeneratedIndex(ProjectRoot, Path.Combine(local, "libil2cpp"));
            try { PrepareNativeBuildCore(report); }
            catch (Exception exception)
            {
                Debug.LogWarning("[AI Bridge Runtime] 自动身份支持未启用：" + exception.Message
                    + " 构建继续使用项目现有工具链；方法热修复仍需该 Player 提供可靠的程序集身份。");
            }
        }

        private static void PrepareNativeBuildCore(BuildReport report)
        {
            var local = ResolveLocalIl2Cpp();
            var config = Resources.Load<AIBridgeRuntimeConfig>(AIBridgeRuntimeConfig.ResourceName);
            if (config == null || !config.AutoStart || !config.Settings.enableRuntimeBridge) return;
            if ((report.summary.options & BuildOptions.Development) == 0) return;
            var target = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(report.summary.platform));
            if (PlayerSettings.GetScriptingBackend(target) != ScriptingImplementation.IL2CPP) return;
            var settings = FindHybridSettings();
            if (settings == null) return;
            var runtimeApi = FindType("HybridCLR.RuntimeApi");
            if (runtimeApi == null || !runtimeApi.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(method => method.Name == "HotfixAssembly"))
                return; // 普通 HybridCLR 的动态代码执行不需要商业 Hotfix API 或 MVID 兼容补丁。
            if (ReadBool(settings, "useGlobalIl2cpp"))
                throw new BuildFailedException("AI Bridge 自动原生支持只操作项目本地 HybridCLR 工具链，不能修改全局 Unity Editor。请使用项目本地 HybridCLR 安装。");
            if (local == null || !Directory.Exists(Path.Combine(local, "libil2cpp", "hybridclr")))
                throw new BuildFailedException("项目尚未安装 HybridCLR 原生工具链；请先通过项目现有的 HybridCLR Installer 完成安装。");
            var project = ProjectRoot;
            var modules = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var name in ReadNames(settings, "differentialHybridAssemblies"))
            {
                var path = FindPlayerDll(project, name);
                Guid mvid;
                if (path == null || !ManagedAssemblyIdentity.TryReadMvid(path, out mvid) || mvid == Guid.Empty)
                    throw new BuildFailedException("无法从本次 Player 编译产物读取 DHE 原始身份：" + name + "。不会使用 Editor DLL 或旧热更 DLL 代替。");
                modules.Add(name, mvid);
            }
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AIBridgeRuntimeProjectIntegration).Assembly);
            if (package == null) throw new BuildFailedException("Cannot resolve the AI Bridge Runtime package directory.");
            AIBridgeRuntimeNativeSupport.Prepare(project, Path.Combine(local, "libil2cpp"),
                Path.Combine(package.resolvedPath, "Tools~", "Native"), modules);
            Debug.Log("[AI Bridge Runtime] 原生身份支持已自动准备，原始 DHE 模块：" + modules.Count + "。项目热更列表及业务加载代码保持原样。");
        }

        private static string FindPlayerDll(string project, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name)) throw new BuildFailedException("Invalid configured assembly name.");
            foreach (var directory in new[] { "Library/Bee/PlayerScriptAssemblies", "Library/PlayerScriptAssemblies" })
            {
                var path = Path.Combine(project, directory, name + ".dll");
                if (!File.Exists(path)) continue;
                AIBridgeRuntimeNativeSupport.ValidateLocalPath(project, path);
                if (AssemblyName.GetAssemblyName(path).Name != name) throw new BuildFailedException("Player assembly name differs from its configured DHE name.");
                return path;
            }
            return null;
        }

        private static object FindHybridSettings()
        {
            var type = FindType("HybridCLR.Editor.Settings.HybridCLRSettings");
            return type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
        }

        private static string ResolveLocalIl2Cpp()
        {
            var path = FindType("HybridCLR.Editor.SettingsUtil")?.GetProperty("LocalIl2CppDir", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ProjectRoot, path));
        }

        private static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type != null);
        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static object ReadMember(object value, string name) => value.GetType().GetField(name)?.GetValue(value) ?? value.GetType().GetProperty(name)?.GetValue(value);
        private static bool ReadBool(object value, string name) => ReadMember(value, name) is bool flag && flag;
        private static string[] ReadNames(object settings, string field) => (ReadMember(settings, field) as IEnumerable)?.Cast<object>()
            .OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name)
            .Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();

        private static IEnumerable<string> ReadDefinitionNames(object settings)
        {
            var definitions = ReadMember(settings, "hotUpdateAssemblyDefinitions") as IEnumerable;
            if (definitions == null) yield break;
            foreach (var definition in definitions)
            {
                var asset = definition as TextAsset;
                if (asset == null) continue;
                var parsed = JsonUtility.FromJson<AssemblyDefinition>(asset.text);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.name)) yield return parsed.name;
            }
        }

        [Serializable] private sealed class AssemblyDefinition { public string name; }
    }

    /// <summary>通过包内构建回调准备身份支持，项目无需编写构建钩子。</summary>
    internal sealed class AIBridgeRuntimeNativeBuildProcessor : IUnityLinkerProcessor
    {
        public int callbackOrder => 11000;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            AIBridgeRuntimeProjectIntegration.PrepareNativeBuild(report);
            return null;
        }
    }
}
