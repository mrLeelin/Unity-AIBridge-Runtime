namespace UnityEditor.Build
{
    public sealed class BuildFailedException : Exception
    {
        public BuildFailedException(string message) : base(message) { }
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
    public enum AssetBundleLoadResult { Success, FailedDecompression }
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
