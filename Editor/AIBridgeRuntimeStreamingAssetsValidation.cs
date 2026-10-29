using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace AIBridge.Runtime.Editor
{
    /// <summary>只读检查 StreamingAssets 的原始文件和 AssetBundle 内容，不加载其中的资源或脚本。</summary>
    internal static class AIBridgeRuntimeStreamingAssetsValidation
    {
        private const int DefaultTimeoutMilliseconds = 30000;
        private static readonly byte[][] Markers =
        {
            Encoding.UTF8.GetBytes("com.sh.aibridge.Runtime"),
            Encoding.UTF8.GetBytes("AIBridgeRuntimeConfig"),
            Encoding.Unicode.GetBytes("com.sh.aibridge.Runtime"),
            Encoding.Unicode.GetBytes("AIBridgeRuntimeConfig")
        };

        /// <summary>检查目录内实际会进入 Player 的文件，包括使用任意扩展名的 AssetBundle。</summary>
        internal static void Validate(string directory)
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                ValidateFile(file);
            }
        }

        /// <summary>检查单个原始或打包文件；临时解压只写入项目 Temp 的独立目录。</summary>
        internal static void ValidateFile(string file, int timeoutMilliseconds = DefaultTimeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            ValidateRawFile(file, file);
            if (!IsAssetBundle(file)) return;

            ValidateBundle(file, file, CreateTemporaryDirectory(), timeoutMilliseconds);
        }

        /// <summary>检查 APK/AAB 中的资源流；普通资源只读扫描，AssetBundle 才复制到临时文件解压。</summary>
        internal static void ValidateStream(Stream stream, string source)
        {
            var header = new byte[9];
            var length = ReadHeader(stream, header);
            if (!IsAssetBundle(header, length))
            {
                ValidateRawStream(stream, source, header, length);
                return;
            }
            var directory = CreateTemporaryDirectory();
            var input = Path.Combine(directory, "input.bundle");
            var handedToNative = false;
            try
            {
                using (var output = File.Create(input))
                {
                    output.Write(header, 0, length);
                    stream.CopyTo(output);
                }
                handedToNative = true;
                ValidateBundle(input, source, directory, DefaultTimeoutMilliseconds);
            }
            finally
            {
                if (!handedToNative) DeleteTemporaryOutput(directory);
            }
        }

        private static string CreateTemporaryDirectory()
        {
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Temp", "AIBridgeRuntime",
                "StreamingAssetsValidation", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static void ValidateBundle(string file, string source, string directory, int timeoutMilliseconds)
        {
            var output = Path.Combine(directory, "uncompressed.bundle");
            AssetBundleRecompressOperation operation = null;
            try
            {
                operation = AssetBundle.RecompressAssetBundleAsync(file, output, BuildCompression.UncompressedRuntime, 0,
                    UnityEngine.ThreadPriority.Low);
                WaitForRecompression(operation, source, timeoutMilliseconds);
                ValidateRawFile(output, source);
            }
            finally
            {
                // 超时后原生任务仍可能写入输出；等其完成后再删除自己的临时文件。
                if (operation != null && !operation.isDone) DeferCleanup(operation, directory);
                else DeleteTemporaryOutput(directory);
            }
        }

        private static bool IsAssetBundle(string file)
        {
            using (var stream = File.OpenRead(file))
            {
                var header = new byte[9];
                return IsAssetBundle(header, ReadHeader(stream, header));
            }
        }

        private static int ReadHeader(Stream stream, byte[] header)
        {
            var length = 0;
            int read;
            while (length < header.Length && (read = stream.Read(header, length, header.Length - length)) > 0)
                length += read;
            return length;
        }

        private static bool IsAssetBundle(byte[] header, int length)
        {
            var text = Encoding.ASCII.GetString(header, 0, length);
            return text.StartsWith("UnityFS\0", StringComparison.Ordinal)
                || text.StartsWith("UnityRaw\0", StringComparison.Ordinal)
                || text.StartsWith("UnityWeb\0", StringComparison.Ordinal);
        }

        private static void WaitForRecompression(AssetBundleRecompressOperation operation, string file, int timeoutMilliseconds)
        {
            if (operation == null)
                throw new BuildFailedException("AI Bridge Release validation could not start AssetBundle recompression: " + file);
            var timer = Stopwatch.StartNew();
            while (!operation.isDone)
            {
                if (timer.ElapsedMilliseconds >= timeoutMilliseconds)
                    throw new BuildFailedException("AI Bridge Release AssetBundle validation timed out; the bundle was not approved: " + file);
                Thread.Sleep(10);
            }
            if (!operation.success || operation.result != AssetBundleLoadResult.Success)
                throw new BuildFailedException("AI Bridge Release validation could not inspect AssetBundle: " + file
                    + " (" + operation.result + ": " + operation.humanReadableResult + ")");
        }

        private static void ValidateRawFile(string file, string source)
        {
            using (var stream = File.OpenRead(file)) ValidateRawStream(stream, source, null, 0);
        }

        private static void ValidateRawStream(Stream stream, string source, byte[] prefix, int prefixLength)
        {
            var overlap = Markers.Max(marker => marker.Length) - 1;
            var buffer = new byte[65536 + overlap];
            var retained = prefixLength;
            if (prefixLength > 0) Buffer.BlockCopy(prefix, 0, buffer, 0, prefixLength);
            int read;
            while ((read = stream.Read(buffer, retained, 65536)) > 0)
            {
                var length = retained + read;
                foreach (var marker in Markers)
                {
                    for (var offset = 0; offset <= length - marker.Length; offset++)
                    {
                        if (!Matches(buffer, offset, marker)) continue;
                        throw new BuildFailedException("AI Bridge Runtime must be absent from Release StreamingAssets; found Runtime assembly or configuration data in: " + source);
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

        private static void DeferCleanup(AssetBundleRecompressOperation operation, string directory)
        {
            EditorApplication.CallbackFunction callback = null;
            callback = () =>
            {
                if (!operation.isDone) return;
                EditorApplication.update -= callback;
                DeleteTemporaryOutput(directory);
            };
            EditorApplication.update += callback;
        }

        private static void DeleteTemporaryOutput(string directory)
        {
            var temporaryRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Temp", "AIBridgeRuntime", "StreamingAssetsValidation"));
            var absolute = Path.GetFullPath(directory);
            Guid id;
            if (!string.Equals(Path.GetDirectoryName(absolute), temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(absolute), "N", out id))
                throw new InvalidOperationException("Unexpected AI Bridge validation temporary path.");
            foreach (var name in new[] { "input.bundle", "uncompressed.bundle" })
            {
                var output = Path.Combine(absolute, name);
                if (File.Exists(output)) File.Delete(output);
            }
            if (Directory.Exists(absolute)) Directory.Delete(absolute, false);
        }
    }
}
