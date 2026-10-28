namespace UnityEngine
{
    public enum RuntimePlatform { WindowsEditor }
    public static class Application
    {
        public static string dataPath;
        public static RuntimePlatform platform = RuntimePlatform.WindowsEditor;
    }
}
namespace UnityEditor
{
    public static class EditorApplication { public static string applicationContentsPath = ""; }
    public static class EditorPrefs { public static string GetString(string key, string fallback) => fallback; }
}
namespace UnityEditor.PackageManager
{
    public sealed class PackageInfo
    {
        public static string Root;
        public string resolvedPath;
        public static PackageInfo FindForAssembly(System.Reflection.Assembly assembly) => new PackageInfo { resolvedPath = Root };
    }
}
