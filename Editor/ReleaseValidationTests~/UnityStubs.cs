namespace UnityEditor.Build
{
    public sealed class BuildFailedException : Exception
    {
        public BuildFailedException(string message) : base(message) { }
    }
}
namespace UnityEditor.UnityLinker
{
    public sealed class UnityLinkerBuildPipelineData
    {
        public string inputDirectory;
    }
}
namespace UnityEditor
{
    public static class EditorApplication
    {
        public delegate void CallbackFunction();
        public static CallbackFunction update;
    }
}
namespace UnityEngine
{
    public static class Application { public static string dataPath; }
    public enum ThreadPriority { Low }
    public enum AssetBundleLoadResult { Success }
    public struct BuildCompression { public static BuildCompression UncompressedRuntime; }
    public sealed class AssetBundleRecompressOperation
    {
        public bool isDone = true;
        public bool success = true;
        public AssetBundleLoadResult result = AssetBundleLoadResult.Success;
        public string humanReadableResult = "fixture result";
    }
    public static class AssetBundle
    {
        public static Func<string, string, AssetBundleRecompressOperation> Recompress;
        public static AssetBundleRecompressOperation RecompressAssetBundleAsync(string input, string output, BuildCompression compression, uint crc, ThreadPriority priority)
            => Recompress(input, output);
    }
}
namespace UnityEditor.Build.Reporting
{
    public sealed class BuildSummary { public string outputPath; }
    public sealed class BuildFile { public string path; }
    public sealed class PackedAssetInfo { public string sourceAssetPath; public Type type; }
    public sealed class PackedAssets { public PackedAssetInfo[] contents; }
    public sealed class BuildReport
    {
        public BuildSummary summary = new();
        public PackedAssets[] packedAssets = Array.Empty<PackedAssets>();
        public BuildFile[] files = Array.Empty<BuildFile>();
        public BuildFile[] GetFiles() => files;
    }
}
