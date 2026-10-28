using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;

namespace AIBridge.Runtime.Editor
{
    /// <summary>保存构建时可供热修复匹配的原始 Player DLL，不把它们加载到 Editor。</summary>
    internal static class AIBridgeRuntimePatchBuildSnapshot
    {
        /// <summary>在本轮受控 pending 目录内按真实 MVID 保存编译产物和随包基线。</summary>
        internal static void Capture(string projectRoot, string snapshotDirectory)
        {
            projectRoot = Path.GetFullPath(projectRoot);
            snapshotDirectory = ValidateSnapshotDirectory(projectRoot, snapshotDirectory);
            var root = Path.Combine(snapshotDirectory, "patch-baselines");
            EnsureDirectory(projectRoot, root);
            var entries = new Dictionary<Guid, BaselineEntry>();
            foreach (var source in FindSources(projectRoot).OrderBy(value => value.Path, StringComparer.Ordinal))
                CaptureSource(projectRoot, root, source, entries);
            var index = new BaselineIndex { candidates = entries.Values.OrderBy(value => value.assemblyName, StringComparer.Ordinal)
                .ThenBy(value => value.mvid, StringComparer.Ordinal).ToArray() };
            var indexPath = Path.Combine(root, "index.json");
            ValidateNoReparsePoints(projectRoot, indexPath);
            File.WriteAllText(indexPath, JsonUtility.ToJson(index, true));
        }

        private static string ValidateSnapshotDirectory(string projectRoot, string snapshotDirectory)
        {
            var absolute = Path.GetFullPath(snapshotDirectory);
            var pending = Path.GetFullPath(Path.Combine(projectRoot, ".runtimebridge", "pending-profiles"));
            var parts = Path.GetFileName(absolute).Split('_');
            Guid buildId, nonce;
            if (!string.Equals(Path.GetDirectoryName(absolute), pending, StringComparison.OrdinalIgnoreCase)
                || parts.Length != 2 || !(Guid.TryParseExact(parts[0], "N", out buildId) || Guid.TryParseExact(parts[0], "D", out buildId)) || buildId == Guid.Empty
                || !Guid.TryParseExact(parts[1], "N", out nonce) || nonce == Guid.Empty)
                throw new ArgumentException("Patch baselines require a pending Player build GUID/nonce directory.", nameof(snapshotDirectory));
            if (!Directory.Exists(absolute))
                throw new DirectoryNotFoundException("The owning Player profile snapshot must exist before baseline capture.");
            ValidateNoReparsePoints(projectRoot, absolute);
            return absolute;
        }

        private static void CaptureSource(string projectRoot, string root, Source source, Dictionary<Guid, BaselineEntry> entries)
        {
            ValidateNoReparsePoints(projectRoot, source.Path);
            Guid mvid;
            string name;
            byte[] bytes;
            // 保持只读共享句柄，防止读取身份后、复制字节前被其他进程替换源 DLL。
            using (var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!ManagedAssemblyIdentity.TryReadMvid(source.Path, out mvid)) return;
                try { name = AssemblyName.GetAssemblyName(source.Path).Name; }
                catch (BadImageFormatException) { return; }
                catch (FileLoadException) { return; }
                using (var captured = new MemoryStream()) { input.CopyTo(captured); bytes = captured.ToArray(); }
            }
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name)
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("/") || name.Contains("\\"))
                throw new InvalidDataException("Player assembly contains an invalid file name.");
            var hash = Hash(bytes);
            BaselineEntry entry;
            if (entries.TryGetValue(mvid, out entry))
            {
                if (entry.sha256 != hash || entry.assemblyName != name)
                    throw new InvalidDataException("Different Player DLL bytes share MVID " + mvid + ".");
                entry.sources.Add(source);
                return;
            }
            var directory = Path.Combine(root, mvid.ToString("D"));
            EnsureDirectory(projectRoot, directory);
            var destination = Path.Combine(directory, name + ".dll");
            ValidateNoReparsePoints(projectRoot, destination);
            if (File.Exists(destination))
            {
                if (Hash(File.ReadAllBytes(destination)) != hash)
                    throw new InvalidDataException("Existing Player baseline bytes differ for MVID " + mvid + ".");
            }
            else File.WriteAllBytes(destination, bytes);
            entry = new BaselineEntry { assemblyName = name, mvid = mvid.ToString("D"), sha256 = hash,
                path = mvid.ToString("D") + "/" + name + ".dll" };
            entry.sources.Add(source);
            entries.Add(mvid, entry);
        }

        private static IEnumerable<Source> FindSources(string projectRoot)
        {
            var player = Path.Combine(projectRoot, "Library", "Bee", "PlayerScriptAssemblies");
            if (Directory.Exists(player))
            {
                ValidateNoReparsePoints(projectRoot, player);
                foreach (var file in Directory.GetFiles(player, "*.dll", SearchOption.TopDirectoryOnly))
                    yield return Source.For(projectRoot, file, "player-compilation");
            }
            var assets = Path.Combine(projectRoot, "Assets");
            if (!Directory.Exists(assets)) yield break;
            var pending = new Stack<KeyValuePair<string, bool>>();
            pending.Push(new KeyValuePair<string, bool>(assets, false));
            var streaming = Path.Combine(assets, "StreamingAssets");
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if ((File.GetAttributes(current.Key) & FileAttributes.ReparsePoint) != 0) continue;
                var include = current.Value || string.Equals(Path.GetFileName(current.Key), "Resources", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(current.Key, streaming, StringComparison.OrdinalIgnoreCase);
                if (include)
                    foreach (var file in Directory.GetFiles(current.Key, "*.dll.bytes", SearchOption.TopDirectoryOnly))
                        yield return Source.For(projectRoot, file,
                            IsWithin(streaming, file) ? "streaming-assets" : "packaged-resource");
                foreach (var directory in Directory.GetDirectories(current.Key))
                    pending.Push(new KeyValuePair<string, bool>(directory, include));
            }
        }

        private static void EnsureDirectory(string projectRoot, string path)
        {
            ValidateNoReparsePoints(projectRoot, path);
            Directory.CreateDirectory(path);
        }

        private static bool IsWithin(string root, string path)
        {
            return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateNoReparsePoints(string projectRoot, string path)
        {
            var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var absolute = Path.GetFullPath(path);
            if (!string.Equals(root, absolute, StringComparison.OrdinalIgnoreCase) && !IsWithin(root, absolute))
                throw new InvalidOperationException("Patch baseline path is outside the project.");
            for (var current = absolute; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Patch baseline paths cannot traverse symbolic links or junctions.");
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        [Serializable] private sealed class BaselineIndex { public int schemaVersion = 1; public BaselineEntry[] candidates; }
        [Serializable] private sealed class BaselineEntry
        {
            public string assemblyName, mvid, sha256, path;
            public List<Source> sources = new List<Source>();
        }
        [Serializable] private sealed class Source
        {
            public string path, kind;
            [NonSerialized] internal string Path;
            internal static Source For(string root, string path, string kind)
            {
                return new Source { Path = path, path = path.Substring(root.Length).TrimStart(System.IO.Path.DirectorySeparatorChar,
                    System.IO.Path.AltDirectorySeparatorChar).Replace('\\', '/'), kind = kind };
            }
        }
    }
}
