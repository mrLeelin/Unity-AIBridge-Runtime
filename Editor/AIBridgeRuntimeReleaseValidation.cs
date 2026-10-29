using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;

namespace AIBridge.Runtime.Editor
{
    /// <summary>检查本轮发布构建的程序集输入、打包资源和产物，发现 Runtime 残留即停止构建。</summary>
    internal static class AIBridgeRuntimeReleaseValidation
    {
        internal const string AssemblyName = "com.sh.aibridge.Runtime";
        private const string PackageRuntimePath = "Packages/com.sh.aibridge.runtime/Runtime/";
        private static readonly byte[][] MetadataMarkers =
        {
            Encoding.UTF8.GetBytes(AssemblyName + "\0"),
            Encoding.UTF8.GetBytes(AssemblyName + ".dll\0")
        };

        /// <summary>检查构建回调给出的本轮程序集集合，不读取可能来自旧包的 Library 目录。</summary>
        internal static void ValidateAssemblyPaths(IEnumerable<string> assemblies)
        {
            if (assemblies == null) throw new ArgumentNullException(nameof(assemblies));
            foreach (var assembly in assemblies)
                RejectBridgeFile(assembly);
        }

        /// <summary>检查 UnityLinker 实际接收的程序集目录。</summary>
        internal static void ValidateLinkerInput(UnityLinkerBuildPipelineData data)
        {
            if (data == null || string.IsNullOrEmpty(data.inputDirectory) || !Directory.Exists(data.inputDirectory))
                throw new BuildFailedException("AI Bridge Release validation could not locate this build's UnityLinker input directory.");
            ValidateAssemblyPaths(Directory.EnumerateFiles(data.inputDirectory, "*.dll", SearchOption.TopDirectoryOnly));
        }

        /// <summary>检查本次 BuildReport 和对应输出中的托管程序集、配置及 IL2CPP 元数据。</summary>
        internal static void ValidateBuildReport(BuildReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            foreach (var packed in report.packedAssets ?? Array.Empty<PackedAssets>())
            {
                foreach (var asset in packed.contents ?? Array.Empty<PackedAssetInfo>())
                {
                    if (IsBridgeAsset(asset.sourceAssetPath, asset.type))
                        Fail("packed asset", asset.sourceAssetPath);
                }
            }

            var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in report.GetFiles())
            {
                RejectBridgeFile(file.path);
                InspectFile(file.path, inspected);
            }
            InspectOutput(report.summary.outputPath, inspected);
        }

        private static bool IsBridgeAsset(string assetPath, Type type)
        {
            if (type != null && type.Assembly.GetName().Name == AssemblyName) return true;
            var normalized = (assetPath ?? string.Empty).Replace('\\', '/');
            return normalized.StartsWith(PackageRuntimePath, StringComparison.OrdinalIgnoreCase)
                || normalized.IndexOf("/Unity-AIBridge-Runtime/Runtime/", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(Path.GetFileName(normalized), "AIBridgeRuntimeConfig.asset", StringComparison.OrdinalIgnoreCase);
        }

        private static void InspectOutput(string outputPath, HashSet<string> inspected)
        {
            if (string.IsNullOrEmpty(outputPath))
                throw new BuildFailedException("AI Bridge Release validation requires this build's output path.");
            var absolute = Path.GetFullPath(outputPath);
            if (!File.Exists(absolute) && !Directory.Exists(absolute))
                throw new BuildFailedException("AI Bridge Release validation could not locate this build's output: " + absolute);
            InspectFile(absolute, inspected);
            // 导出工程和 .app 本身就是产物目录；桌面可执行文件只检查其同名 Data 目录。
            var directory = Directory.Exists(absolute) ? absolute
                : Path.Combine(Path.GetDirectoryName(absolute), Path.GetFileNameWithoutExtension(absolute) + "_Data");
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                RejectBridgeFile(file);
                InspectFile(file, inspected);
            }
        }

        private static void InspectFile(string file, HashSet<string> inspected)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
            var absolute = Path.GetFullPath(file);
            if (!inspected.Add(absolute)) return;
            var name = Path.GetFileName(file);
            if (absolute.Replace('\\', '/').IndexOf("/StreamingAssets/", StringComparison.OrdinalIgnoreCase) >= 0)
                AIBridgeRuntimeStreamingAssetsValidation.ValidateFile(file);
            if (string.Equals(name, "global-metadata.dat", StringComparison.OrdinalIgnoreCase))
            {
                using (var stream = File.OpenRead(file)) ValidateMetadata(stream, file);
            }
            else if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".aab", StringComparison.OrdinalIgnoreCase))
                ValidateAndroidArchive(file);
        }

        private static void ValidateAndroidArchive(string file)
        {
            using (var archive = ZipFile.OpenRead(file))
            {
                foreach (var entry in archive.Entries)
                {
                    RejectBridgeFile(entry.FullName);
                    var normalized = entry.FullName.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(entry.Name)
                        && (normalized.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
                            || normalized.IndexOf("/assets/", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        using (var stream = entry.Open()) AIBridgeRuntimeStreamingAssetsValidation.ValidateStream(stream, file + "!" + entry.FullName);
                    }
                    if (!string.Equals(entry.Name, "global-metadata.dat", StringComparison.OrdinalIgnoreCase)) continue;
                    using (var stream = entry.Open()) ValidateMetadata(stream, file + "!" + entry.FullName);
                }
            }
        }

        private static void RejectBridgeFile(string file)
        {
            var name = Path.GetFileName((file ?? string.Empty).Replace('\\', '/'));
            if (string.Equals(name, AssemblyName + ".dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, AssemblyName + ".dll.bytes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, AssemblyName + ".cpp", StringComparison.OrdinalIgnoreCase))
                Fail("assembly", file);
        }

        private static void ValidateMetadata(Stream stream, string source)
        {
            // 保留缓冲区尾部，确保程序集字符串横跨读取边界时也能被识别。
            var overlap = MetadataMarkers.Max(marker => marker.Length) - 1;
            var buffer = new byte[65536 + overlap];
            var retained = 0;
            int read;
            while ((read = stream.Read(buffer, retained, 65536)) > 0)
            {
                var length = retained + read;
                foreach (var marker in MetadataMarkers)
                {
                    for (var offset = 0; offset <= length - marker.Length; offset++)
                    {
                        if (!Matches(buffer, offset, marker)) continue;
                        Fail("IL2CPP metadata assembly", source);
                    }
                }
                retained = Math.Min(overlap, length);
                Buffer.BlockCopy(buffer, length - retained, buffer, 0, retained);
            }
        }

        private static bool Matches(byte[] buffer, int offset, byte[] marker)
        {
            for (var index = 0; index < marker.Length; index++)
                if (buffer[offset + index] != marker[index]) return false;
            return true;
        }

        private static void Fail(string kind, string path)
        {
            throw new BuildFailedException("AI Bridge Runtime must be absent from Release builds; found " + kind + ": " + path);
        }
    }
}
