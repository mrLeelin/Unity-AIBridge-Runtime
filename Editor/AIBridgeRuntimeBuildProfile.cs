using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;

namespace AIBridge.Runtime.Editor
{
    /// <summary>仅在成功的测试 Player 构建后导出目标引用；不依赖正在运行的 Editor 命令服务。</summary>
    internal sealed class AIBridgeRuntimeBuildProfile : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder { get { return 10000; } }

        public void OnPreprocessBuild(BuildReport report)
        {
            var config = Resources.Load<AIBridgeRuntimeConfig>(AIBridgeRuntimeConfig.ResourceName);
            if (config == null || !config.AutoStart)
                return;
            if ((report.summary.options & BuildOptions.Development) == 0)
                throw new BuildFailedException("AI Bridge Runtime Auto Start requires a Development Build. Disable Auto Start for a release Player.");
            if (string.IsNullOrWhiteSpace(config.Settings.authToken))
                throw new BuildFailedException("AI Bridge Runtime requires a session token. Generate one in Window > AI Bridge Runtime > Settings.");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            var config = Resources.Load<AIBridgeRuntimeConfig>(AIBridgeRuntimeConfig.ResourceName);
            if (config == null || !config.AutoStart || report.summary.result != BuildResult.Succeeded)
                return;
            var profilePath = ExportProfile(report);
            Debug.Log("[AI Bridge Runtime] Player reference profile exported: " + profilePath);
        }

        /// <summary>从本次成功构建的目标托管程序集生成离线 CLI 编译配置。</summary>
        public static string ExportProfile(BuildReport report)
        {
            if (report == null || report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("A successful Player BuildReport is required to export a Runtime Bridge profile.");
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var sources = FindPlayerReferences(report, projectRoot);
            if (!sources.ContainsKey("mscorlib.dll") || !sources.ContainsKey("UnityEngine.CoreModule.dll"))
                throw new BuildFailedException("Runtime Bridge could not find complete target Player references. ManagedStripped/build-report DLLs must include mscorlib.dll and UnityEngine.CoreModule.dll; Editor ScriptAssemblies are never used as a fallback.");

            var buildId = ReadBuildGuid(report.summary);
            if (string.IsNullOrWhiteSpace(buildId))
                throw new BuildFailedException("BuildReport contains no Player build GUID; a validated Runtime Bridge profile cannot be exported.");
            var root = Path.Combine(projectRoot, ".runtimebridge");
            Directory.CreateDirectory(root);
            var ignore = Path.Combine(root, ".gitignore");
            if (!File.Exists(ignore)) File.WriteAllText(ignore, "*\n!.gitignore\n");
            var output = Path.Combine(root, "profiles", buildId);
            var references = Path.Combine(output, "references");
            Directory.CreateDirectory(references);
            var fingerprints = CopyPlayerReferences(sources, references);
            var profile = new RuntimeReferenceProfile
            {
                target = report.summary.platform.ToString(),
                platform = report.summary.platform.ToString(),
                buildId = buildId,
                unityVersion = Application.unityVersion,
                referenceDirectory = "references",
                references = sources.Keys.OrderBy(v => v, StringComparer.Ordinal).ToArray(),
                defineSymbols = GetPlayerDefines(report.summary.platform),
                assemblyFingerprints = fingerprints.ToArray()
            };
            var path = Path.Combine(output, "profile.json");
            File.WriteAllText(path, JsonUtility.ToJson(profile, true));
            return path;
        }

        private static Dictionary<string, string> FindPlayerReferences(BuildReport report, string projectRoot)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // 此目录由本次成功构建的目标平台产生；不能改用 Editor ScriptAssemblies。
            var group = BuildPipeline.GetBuildTargetGroup(report.summary.platform);
            var stripped = PlayerSettings.GetScriptingBackend(NamedBuildTarget.FromBuildTargetGroup(group)) == ScriptingImplementation.IL2CPP
                ? GetManagedStrippedDirectory(report.summary.platform, projectRoot) : null;
            if (!string.IsNullOrEmpty(stripped) && Directory.Exists(stripped))
            {
                foreach (var path in Directory.GetFiles(stripped, "*.dll", SearchOption.TopDirectoryOnly))
                    AddManagedReference(result, path, projectRoot);
            }
            foreach (var file in report.GetFiles())
            {
                if (!file.path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                var normalized = file.path.Replace('\\', '/');
                var role = file.role ?? string.Empty;
                if (role.IndexOf("Managed", StringComparison.OrdinalIgnoreCase) < 0
                    && normalized.IndexOf("/Managed/", StringComparison.OrdinalIgnoreCase) < 0
                    && normalized.IndexOf("/ManagedStripped/", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                AddManagedReference(result, file.path, projectRoot);
            }
            return result;
        }

        private static void AddManagedReference(Dictionary<string, string> references, string path, string projectRoot)
        {
            var absolute = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(projectRoot, path));
            var normalized = absolute.Replace('\\', '/');
            var name = Path.GetFileName(absolute);
            if (!File.Exists(absolute) || references.ContainsKey(name)
                || normalized.IndexOf("/Library/ScriptAssemblies/", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Editor", StringComparison.OrdinalIgnoreCase) >= 0)
                return;
            Guid mvid;
            if (ManagedAssemblyIdentity.TryReadMvid(absolute, out mvid))
                references.Add(name, absolute);
        }

        private static List<AssemblyFingerprint> CopyPlayerReferences(Dictionary<string, string> sources, string destination)
        {
            var result = new List<AssemblyFingerprint>();
            foreach (var pair in sources.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                var output = Path.Combine(destination, pair.Key);
                File.Copy(pair.Value, output, true);
                Guid mvid;
                if (!ManagedAssemblyIdentity.TryReadMvid(output, out mvid))
                    throw new BuildFailedException("Target reference is not a readable managed assembly: " + pair.Key);
                string hash;
                using (var stream = File.OpenRead(output))
                using (var sha = SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
                result.Add(new AssemblyFingerprint { name = Path.GetFileNameWithoutExtension(pair.Key), mvid = mvid.ToString("D"), sha256 = hash });
            }
            return result;
        }

        private static string[] GetPlayerDefines(BuildTarget target)
        {
            if (target != EditorUserBuildSettings.activeBuildTarget)
                throw new BuildFailedException("Active build target changed before Runtime Bridge profile export; exact Player defines are unavailable.");
            return CompilationPipeline.GetAssemblies(AssembliesType.Player)
                .SelectMany(assembly => assembly.defines)
                .Where(value => !value.StartsWith("UNITY_EDITOR", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        private static string ReadBuildGuid(BuildSummary summary)
        {
            var type = summary.GetType();
            var property = type.GetProperty("guid", BindingFlags.Public | BindingFlags.Instance);
            var value = property != null ? property.GetValue(summary, null) : type.GetField("guid", BindingFlags.Public | BindingFlags.Instance)?.GetValue(summary);
            var guid = value == null ? string.Empty : value.ToString();
            return !string.IsNullOrEmpty(guid) && guid.Any(c => c != '0' && c != '-')
                && guid.All(c => char.IsLetterOrDigit(c) || c == '-') ? guid : string.Empty;
        }

        private static string GetManagedStrippedDirectory(BuildTarget target, string root)
        {
            string name;
            switch (target)
            {
                case BuildTarget.Android: name = "Android"; break;
                case BuildTarget.iOS: name = "iOS"; break;
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64: name = "WinPlayerBuildProgram"; break;
                case BuildTarget.StandaloneLinux64: name = "LinuxPlayerBuildProgram"; break;
                case BuildTarget.StandaloneOSX: name = "MacStandalonePlayerBuildProgram"; break;
                case BuildTarget.WebGL: name = "WebGL"; break;
                default: return null;
            }
            return Path.Combine(root, "Library", "Bee", "artifacts", name, "ManagedStripped");
        }

        [Serializable]
        private sealed class RuntimeReferenceProfile
        {
            public int schemaVersion = 1;
            public string target;
            public string platform;
            public string buildId;
            public string unityVersion;
            public string referenceDirectory;
            public string[] references;
            public string[] defineSymbols;
            public AssemblyFingerprint[] assemblyFingerprints;
        }

        [Serializable]
        private sealed class AssemblyFingerprint
        {
            public string name;
            public string mvid;
            public string sha256;
        }
    }
}
