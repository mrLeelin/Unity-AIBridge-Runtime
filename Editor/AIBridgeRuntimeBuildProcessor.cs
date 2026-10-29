using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIBridge.Runtime.Editor
{
    /// <summary>仅向开发包注入预加载配置，并在构建结束或失败后恢复项目设置。</summary>
    internal sealed class AIBridgeRuntimeBuildProcessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport, IFilterBuildAssemblies
    {
        private const string BackupKey = "AIBridge.Runtime.PreloadedAssetsBackup";
        public int callbackOrder => -20000;

        /// <summary>移出旧 Resources 配置，准备本次构建使用的预加载资源。</summary>
        public void OnPreprocessBuild(BuildReport report)
        {
            RestorePreloadedAssets();
            AIBridgeRuntimeSettingsWindow.MigrateLegacyConfiguration();
            ValidateResourceConfigurations();
            var development = (report.summary.options & BuildOptions.Development) != 0;
            if (!development) AIBridgeRuntimeProjectIntegration.RemoveNativeBuildSupport();
            var original = PlayerSettings.GetPreloadedAssets();
            var backup = new PreloadedAssetsBackup
            {
                assets = original.Select(asset => asset == null ? string.Empty : GlobalObjectId.GetGlobalObjectIdSlow(asset).ToString()).ToArray(),
                development = EditorUserBuildSettings.development
            };
            SessionState.SetString(BackupKey, JsonUtility.ToJson(backup));
            // Unity 的脚本索引还读取 Editor 开发开关；自定义 BuildOptions 必须与它一致。
            EditorUserBuildSettings.development = development;
            var assets = original.Where(asset => !IsBridgeObject(asset)).ToList();
            var config = AIBridgeRuntimeConfig.Load();
            if (development && config != null && config.AutoStart && config.Settings.enableRuntimeBridge)
                assets.Add(config);
            PlayerSettings.SetPreloadedAssets(assets.ToArray());
            if (!development)
            {
                ValidateReleaseResourceDependencies();
                AIBridgeRuntimeStreamingAssetsValidation.Validate(System.IO.Path.Combine(Application.dataPath, "StreamingAssets"));
            }
        }

        /// <summary>检查本次 Player 实际使用的程序集列表。</summary>
        public string[] OnFilterAssemblies(BuildOptions buildOptions, string[] assemblies)
        {
            if ((buildOptions & BuildOptions.Development) == 0)
                AIBridgeRuntimeReleaseValidation.ValidateAssemblyPaths(assemblies);
            return assemblies;
        }

        /// <summary>构建结束后立即恢复预加载设置。</summary>
        public void OnPostprocessBuild(BuildReport report) => RestorePreloadedAssets();

        [InitializeOnLoadMethod]
        private static void WatchBuildCompletion()
        {
            EditorApplication.update -= RestoreAfterBuild;
            EditorApplication.update += RestoreAfterBuild;
        }

        private static void RestoreAfterBuild()
        {
            // 失败或取消时 Unity 不保证调用 postprocess；SessionState 同时覆盖域重载。
            if (!BuildPipeline.isBuildingPlayer) RestorePreloadedAssets();
        }

        internal static void RestorePreloadedAssets()
        {
            var json = SessionState.GetString(BackupKey, string.Empty);
            if (string.IsNullOrEmpty(json)) return;
            var backup = JsonUtility.FromJson<PreloadedAssetsBackup>(json);
            var assets = backup.assets.Select(ResolveAsset).ToArray();
            PlayerSettings.SetPreloadedAssets(assets);
            EditorUserBuildSettings.development = backup.development;
            SessionState.EraseString(BackupKey);
        }

        private static UnityEngine.Object ResolveAsset(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (!GlobalObjectId.TryParse(text, out var id))
                throw new BuildFailedException("Cannot restore AI Bridge preloaded asset identity: " + text);
            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
        }

        private static void ValidateResourceConfigurations()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:AIBridgeRuntimeConfig"))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (assetPath.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0
                    || assetPath.IndexOf("/StreamingAssets/", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new BuildFailedException("Move AI Bridge configuration out of Resources/StreamingAssets before building: " + assetPath);
            }
        }

        private static void ValidateReleaseResourceDependencies()
        {
            var roots = AssetDatabase.GetAllAssetPaths().Where(assetPath =>
                assetPath.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0
                && assetPath.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) < 0)
                .Concat(AssetDatabase.GetAllAssetBundleNames().SelectMany(AssetDatabase.GetAssetPathsFromAssetBundle)).ToArray();
            var dependencies = AssetDatabase.GetDependencies(roots, true)
                .Concat(PlayerSettings.GetPreloadedAssets().Where(asset => asset != null)
                    .Select(AssetDatabase.GetAssetPath));
            foreach (var assetPath in AssetDatabase.GetDependencies(dependencies.Distinct().ToArray(), true))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                var script = asset as MonoScript;
                if (IsBridgeObject(asset) || (script != null && script.GetClass()?.Assembly == typeof(AIBridgeRuntimeConfig).Assembly))
                    throw new BuildFailedException("Release Resources/preloaded assets/AssetBundles reference AI Bridge: " + assetPath);
            }
        }

        internal static bool IsBridgeObject(UnityEngine.Object asset) => asset != null
            && asset.GetType().Assembly == typeof(AIBridgeRuntimeConfig).Assembly;

        [Serializable]
        private sealed class PreloadedAssetsBackup { public string[] assets; public bool development; }
    }

    /// <summary>仅修改构建场景副本，移除正式包中的 Bridge 组件。</summary>
    internal sealed class AIBridgeRuntimeSceneProcessor : IProcessSceneWithReport
    {
        public int callbackOrder => int.MaxValue;

        /// <summary>开发构建保留组件，正式构建剥离插件组件并检查间接配置引用。</summary>
        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null || (report.summary.options & BuildOptions.Development) != 0) return;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (!AIBridgeRuntimeBuildProcessor.IsBridgeObject(component)) continue;
                    var owner = component.gameObject;
                    UnityEngine.Object.DestroyImmediate(component);
                    if ((owner.name == "AI Bridge Runtime" || owner.name == AIBridgeRuntimeSettingsCarrier.CarrierObjectName)
                        && owner.transform.childCount == 0 && owner.GetComponents<Component>().Length == 1)
                        UnityEngine.Object.DestroyImmediate(owner);
                }
            }
            var leaked = EditorUtility.CollectDependencies(scene.GetRootGameObjects()).FirstOrDefault(AIBridgeRuntimeBuildProcessor.IsBridgeObject);
            if (leaked != null)
                throw new BuildFailedException("Release scene still references an AI Bridge asset: " + AssetDatabase.GetAssetPath(leaked));
        }
    }

    /// <summary>正式包构建完成后检查本次产物，发现调试代码或配置时直接构建失败。</summary>
    internal sealed class AIBridgeRuntimeReleaseBuildProcessor : IPostprocessBuildWithReport
    {
        public int callbackOrder => int.MaxValue;

        /// <summary>验证当前正式包的资源、程序集和元数据。</summary>
        public void OnPostprocessBuild(BuildReport report)
        {
            if ((report.summary.options & BuildOptions.Development) == 0)
                AIBridgeRuntimeReleaseValidation.ValidateBuildReport(report);
        }
    }
}
