using System.Text.Json;

namespace UnityEngine
{
    public static class Application
    {
        public static string dataPath;
        public static string unityVersion = "6000.3.6f1";
    }
    public static class Resources
    {
        public static object Configuration;
        public static T Load<T>(string name) => (T)Configuration;
    }
    public static class Debug
    {
        public static readonly List<string> Errors = new();
        public static void Log(object value) { }
        public static void LogError(object value) => Errors.Add(value.ToString());
    }
    public static class JsonUtility
    {
        private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
        public static string ToJson(object value, bool prettyPrint = false) => JsonSerializer.Serialize(value, value.GetType(), Options);
        public static T FromJson<T>(string value) => JsonSerializer.Deserialize<T>(value, Options);
    }
}
namespace UnityEditor
{
    public enum BuildTarget { Android, iOS, StandaloneWindows, StandaloneWindows64, StandaloneLinux64, StandaloneOSX, WebGL }
    public enum BuildTargetGroup { Android }
    [Flags] public enum BuildOptions { None, Development }
    public enum ScriptingImplementation { IL2CPP }
    public sealed class InitializeOnLoadMethodAttribute : Attribute { }
    public static class BuildPipeline
    {
        public static bool isBuildingPlayer;
        public static BuildTargetGroup GetBuildTargetGroup(BuildTarget target) => BuildTargetGroup.Android;
    }
    public static class PlayerSettings
    {
        public static ScriptingImplementation GetScriptingBackend(Build.NamedBuildTarget target) => ScriptingImplementation.IL2CPP;
    }
    public static class EditorUserBuildSettings { public static BuildTarget activeBuildTarget = BuildTarget.Android; }
    public static class EditorApplication
    {
        public delegate void CallbackFunction();
        public static CallbackFunction update;
        public static Action delayCall;
        public static void Tick() { var callbacks = delayCall; delayCall = null; callbacks?.Invoke(); update?.Invoke(); }
    }
}
namespace UnityEditor.Build
{
    public readonly struct NamedBuildTarget
    {
        public static NamedBuildTarget FromBuildTargetGroup(UnityEditor.BuildTargetGroup value) => new();
    }
    public class BuildFailedException : Exception { public BuildFailedException(string message) : base(message) { } }
    public interface IPreprocessBuildWithReport { int callbackOrder { get; } void OnPreprocessBuild(Reporting.BuildReport report); }
    public interface IPostprocessBuildWithReport { int callbackOrder { get; } void OnPostprocessBuild(Reporting.BuildReport report); }
    public interface IUnityLinkerProcessor
    {
        int callbackOrder { get; }
        string GenerateAdditionalLinkXmlFile(Reporting.BuildReport report, UnityEditor.UnityLinker.UnityLinkerBuildPipelineData data);
    }
}
namespace UnityEditor.UnityLinker { public sealed class UnityLinkerBuildPipelineData { } }
namespace UnityEditor.Build.Reporting
{
    public enum BuildResult { Unknown, Succeeded, Failed, Cancelled }
    public sealed class BuildSummary
    {
        public BuildResult result;
        public Guid guid { get; set; } = Guid.NewGuid();
        public UnityEditor.BuildTarget platform = UnityEditor.BuildTarget.Android;
        public UnityEditor.BuildOptions options = UnityEditor.BuildOptions.Development;
    }
    public sealed class BuildFile { public string path; public string role; }
    public sealed class BuildReport
    {
        public BuildSummary summary = new();
        public BuildFile[] files = Array.Empty<BuildFile>();
        public static BuildReport Latest;
        public static BuildReport GetLatestReport() => Latest;
        public BuildFile[] GetFiles() => files;
    }
}
namespace UnityEditor.Compilation
{
    public enum AssembliesType { Player }
    public sealed class Assembly { public string[] defines; }
    public static class CompilationPipeline
    {
        public static string[] Defines = { "UNITY_ANDROID", "UNITY_EDITOR", "UNITY_EDITOR_WIN" };
        public static Assembly[] GetAssemblies(AssembliesType type) => new[] { new Assembly { defines = Defines } };
    }
}
namespace AIBridge.Runtime
{
    public sealed class AIBridgeRuntimeConfig
    {
        public const string ResourceName = "AIBridgeRuntimeConfig";
        public static AIBridgeRuntimeConfig Load() => (AIBridgeRuntimeConfig)UnityEngine.Resources.Configuration;
        public bool AutoStart = true;
        public SettingsData Settings = new();
        public sealed class SettingsData { public string authToken = "fixture-only"; }
    }
}
