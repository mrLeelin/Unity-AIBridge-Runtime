using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;
using UnityEngine;

namespace AIBridge.Runtime.Editor
{
    /// <summary>为 Development Player 保留片段编译及基础场景调试使用的成员。</summary>
    internal sealed class AIBridgeRuntimeLinkerProcessor : IUnityLinkerProcessor
    {
        public int callbackOrder => 10000;

        /// <summary>仅为开发构建提供保留文件，避免发布构建无条件保留调试依赖。</summary>
        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            if ((report.summary.options & UnityEditor.BuildOptions.Development) == 0)
                return null;
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Temp", "AIBridgeRuntime"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "CompilerSupport.link.xml");
            File.WriteAllText(path, CompilerSupportXml);
            return path;
        }

        private const string CompilerSupportXml = @"<linker>
  <assembly fullname=""mscorlib"">
    <type fullname=""System.Runtime.CompilerServices.AsyncTaskMethodBuilder*"" preserve=""all"" />
    <type fullname=""System.Runtime.CompilerServices.AsyncMethodBuilderCore*"" preserve=""all"" />
    <type fullname=""System.Runtime.CompilerServices.TaskAwaiter*"" preserve=""all"" />
    <type fullname=""System.Runtime.CompilerServices.ConfiguredTaskAwaitable*"" preserve=""all"" />
    <type fullname=""System.Runtime.CompilerServices.INotifyCompletion"" preserve=""all"" />
    <type fullname=""System.Runtime.CompilerServices.ICriticalNotifyCompletion"" preserve=""all"" />
    <type fullname=""System.Runtime.CompilerServices.IAsyncStateMachine"" preserve=""all"" />
    <type fullname=""System.Threading.Tasks.Task*"" preserve=""all"" />
  </assembly>
  <assembly fullname=""UnityEngine.CoreModule"">
    <type fullname=""UnityEngine.Profiling.Profiler"" preserve=""all"" />
    <type fullname=""Unity.Profiling.ProfilerMarker"" preserve=""all"" />
    <type fullname=""Unity.Profiling.ProfilerRecorder"" preserve=""all"" />
    <type fullname=""Unity.Profiling.ProfilerRecorderSample"" preserve=""all"" />
    <type fullname=""Unity.Profiling.ProfilerCategory"" preserve=""all"" />
    <type fullname=""UnityEngine.GameObject"" preserve=""all"" />
    <type fullname=""UnityEngine.PrimitiveType"" preserve=""all"" />
    <type fullname=""UnityEngine.Transform"" preserve=""all"" />
    <type fullname=""UnityEngine.Light"" preserve=""all"" />
    <type fullname=""UnityEngine.LightType"" preserve=""all"" />
    <type fullname=""UnityEngine.MeshFilter"" preserve=""all"" />
    <type fullname=""UnityEngine.MeshRenderer"" preserve=""all"" />
    <type fullname=""UnityEngine.Renderer"" preserve=""all"" />
    <type fullname=""UnityEngine.Material"" preserve=""all"" />
  </assembly>
  <assembly fullname=""UnityEngine.PhysicsModule"" ignoreIfMissing=""1"">
    <type fullname=""UnityEngine.Collider"" preserve=""all"" />
    <type fullname=""UnityEngine.BoxCollider"" preserve=""all"" />
  </assembly>
</linker>";
    }
}
