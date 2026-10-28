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
            if (config == null || !config.AutoStart || report == null
                || report.summary.result == BuildResult.Failed || report.summary.result == BuildResult.Cancelled)
                return;
            // 此回调中的 result 可能仍是 Unknown。先保存本轮引用，防止下一次构建覆盖共享裁剪目录。
            var snapshot = CaptureProfile(report);
            Schedule(() => FinishAutomaticExport(report, snapshot, 0));
        }

        /// <summary>从本次成功构建的目标托管程序集生成离线 CLI 编译配置。</summary>
        public static string ExportProfile(BuildReport report)
        {
            if (report == null || report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("A successful Player BuildReport is required to export a Runtime Bridge profile.");
            var snapshot = CaptureProfile(report);
            try { return PublishProfile(snapshot); }
            finally { DeleteSnapshot(snapshot); }
        }

        private static ProfileSnapshot CaptureProfile(BuildReport report)
        {
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
            var output = Path.Combine(root, "pending-profiles", buildId + "_" + Guid.NewGuid().ToString("N"));
            var references = Path.Combine(output, "references");
            Directory.CreateDirectory(references);
            try
            {
                var profile = new RuntimeReferenceProfile
                {
                    target = report.summary.platform.ToString(),
                    platform = report.summary.platform.ToString(),
                    buildId = buildId,
                    unityVersion = Application.unityVersion,
                    referenceDirectory = "references",
                    references = sources.Keys.OrderBy(v => v, StringComparer.Ordinal).ToArray(),
                    defineSymbols = GetPlayerDefines(report.summary.platform),
                    assemblyFingerprints = CopyPlayerReferences(sources, references).ToArray()
                };
                File.WriteAllText(Path.Combine(output, "profile.json"), JsonUtility.ToJson(profile, true));
                AIBridgeRuntimePatchBuildSnapshot.Capture(projectRoot, output);
                return new ProfileSnapshot { ProjectRoot = projectRoot, Directory = output, Profile = profile };
            }
            catch
            {
                DeleteSnapshot(new ProfileSnapshot { ProjectRoot = projectRoot, Directory = output });
                throw;
            }
        }

        private static void FinishAutomaticExport(BuildReport report, ProfileSnapshot snapshot, int pendingTicks)
        {
            if (BuildPipeline.isBuildingPlayer)
            {
                Schedule(() => FinishAutomaticExport(report, snapshot, pendingTicks));
                return;
            }
            try
            {
                if (report == null || ReadBuildGuid(report.summary) != snapshot.Profile.buildId
                    || report.summary.platform.ToString() != snapshot.Profile.target)
                    throw new InvalidOperationException("Player BuildReport identity changed before Runtime Bridge profile publication.");
                if (report.summary.result == BuildResult.Unknown && pendingTicks < 30)
                {
                    Schedule(() => FinishAutomaticExport(report, snapshot, pendingTicks + 1));
                    return;
                }
                if (report.summary.result == BuildResult.Succeeded)
                    Debug.Log("[AI Bridge Runtime] Player reference profile exported: " + PublishProfile(snapshot));
                else if (report.summary.result == BuildResult.Unknown)
                    throw new InvalidOperationException("Player build result was not finalized; Runtime Bridge profile was not published.");
                DeleteSnapshot(snapshot);
            }
            catch (Exception exception)
            {
                Debug.LogError("[AI Bridge Runtime] Player profile export failed: " + exception.Message);
                DeleteSnapshot(snapshot);
            }
        }

        [InitializeOnLoadMethod]
        private static void ResumePendingExports()
        {
            // 脚本重载后只恢复仍能与最新成功报告精确绑定的快照。
            Schedule(() =>
            {
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var pending = Path.Combine(projectRoot, ".runtimebridge", "pending-profiles");
                if (!Directory.Exists(pending)) return;
                var report = BuildReport.GetLatestReport();
                foreach (var directory in Directory.GetDirectories(pending))
                {
                    if (!IsSnapshotPath(projectRoot, directory)) continue;
                    var snapshot = new ProfileSnapshot { ProjectRoot = projectRoot, Directory = directory };
                    try
                    {
                        snapshot.Profile = JsonUtility.FromJson<RuntimeReferenceProfile>(File.ReadAllText(Path.Combine(directory, "profile.json")));
                        if (report != null && snapshot.Profile != null
                            && snapshot.Profile.buildId == ReadBuildGuid(report.summary)
                            && snapshot.Profile.target == report.summary.platform.ToString())
                            FinishAutomaticExport(report, snapshot, 0);
                        else
                            DeleteSnapshot(snapshot);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogError("[AI Bridge Runtime] Pending Player profile recovery failed: " + exception.Message);
                        DeleteSnapshot(snapshot);
                    }
                }
            });
        }

        // 后台构建没有 Inspector 刷新时 delayCall 可能不执行；使用一次性 Editor update。
        private static void Schedule(Action action)
        {
            EditorApplication.CallbackFunction callback = null;
            callback = () => { EditorApplication.update -= callback; action(); };
            EditorApplication.update += callback;
        }

        private static string PublishProfile(ProfileSnapshot snapshot)
        {
            var profile = snapshot.Profile;
            if (profile == null || profile.referenceDirectory != "references" || profile.references == null
                || profile.assemblyFingerprints == null || profile.references.Length != profile.assemblyFingerprints.Length
                || Path.GetFileName(snapshot.Directory).Split('_')[0] != profile.buildId)
                throw new InvalidOperationException("Invalid pending Runtime Bridge profile.");
            var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in profile.references)
            {
                if (name != Path.GetFileName(name) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Invalid pending Player reference path.");
                references.Add(name, Path.Combine(snapshot.Directory, "references", name));
            }
            var actual = FingerprintReferences(references);
            if (JsonUtility.ToJson(new FingerprintSet { values = actual.ToArray() })
                != JsonUtility.ToJson(new FingerprintSet { values = profile.assemblyFingerprints }))
                throw new InvalidOperationException("Pending Player reference fingerprints changed before publication.");
            var output = Path.Combine(snapshot.ProjectRoot, ".runtimebridge", "profiles", profile.buildId);
            var path = Path.Combine(output, "profile.json");
            if (Directory.Exists(output))
            {
                if (!File.Exists(path) || File.ReadAllText(path) != File.ReadAllText(Path.Combine(snapshot.Directory, "profile.json")))
                    throw new InvalidOperationException("A different Runtime Bridge profile already exists for this Player build GUID.");
                var publishedReferences = profile.references.ToDictionary(name => name,
                    name => Path.Combine(output, "references", name), StringComparer.OrdinalIgnoreCase);
                var published = FingerprintReferences(publishedReferences);
                if (JsonUtility.ToJson(new FingerprintSet { values = published.ToArray() })
                    != JsonUtility.ToJson(new FingerprintSet { values = profile.assemblyFingerprints }))
                    throw new InvalidOperationException("Published Player reference fingerprints changed after export.");
                return path;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            Directory.Move(snapshot.Directory, output);
            return path;
        }

        private static bool IsSnapshotPath(string projectRoot, string directory)
        {
            var pending = Path.GetFullPath(Path.Combine(projectRoot, ".runtimebridge", "pending-profiles"));
            var absolute = Path.GetFullPath(directory);
            var name = Path.GetFileName(absolute);
            var separator = name.LastIndexOf('_');
            Guid nonce;
            return string.Equals(Path.GetDirectoryName(absolute), pending, StringComparison.OrdinalIgnoreCase)
                && separator > 0 && Guid.TryParseExact(name.Substring(separator + 1), "N", out nonce);
        }

        private static void DeleteSnapshot(ProfileSnapshot snapshot)
        {
            if (IsSnapshotPath(snapshot.ProjectRoot, snapshot.Directory) && Directory.Exists(snapshot.Directory))
                Directory.Delete(snapshot.Directory, true);
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
            foreach (var pair in sources.OrderBy(v => v.Key, StringComparer.Ordinal))
                File.Copy(pair.Value, Path.Combine(destination, pair.Key), true);
            return FingerprintReferences(sources.ToDictionary(pair => pair.Key, pair => Path.Combine(destination, pair.Key)));
        }

        private static List<AssemblyFingerprint> FingerprintReferences(Dictionary<string, string> sources)
        {
            var result = new List<AssemblyFingerprint>();
            foreach (var pair in sources.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                Guid mvid;
                if (!ManagedAssemblyIdentity.TryReadMvid(pair.Value, out mvid))
                    throw new BuildFailedException("Target reference is not a readable managed assembly: " + pair.Key);
                string hash;
                using (var stream = File.OpenRead(pair.Value))
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
        private sealed class FingerprintSet
        {
            public AssemblyFingerprint[] values;
        }

        private sealed class ProfileSnapshot
        {
            public string ProjectRoot;
            public string Directory;
            public RuntimeReferenceProfile Profile;
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
