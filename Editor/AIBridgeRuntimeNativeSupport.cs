using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AIBridge.Runtime.Editor
{
    /// <summary>为项目自己的 HybridCLR 工具链安装可重复验证的模块身份读取支持。</summary>
    internal static class AIBridgeRuntimeNativeSupport
    {
        private const string ModulePath = "icalls/mscorlib/System.Reflection/RuntimeModule.cpp";
        private const string RawPath = "hybridclr/metadata/RawImageBase.h";
        private const string IndexPath = "hybridclr/generated/AIBridgeModuleMvids.h";
        private static readonly StringComparison PathComparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        internal static void ClearGeneratedIndex(string projectRoot, string libil2cpp)
        {
            var path = Path.Combine(libil2cpp, IndexPath);
            if (!File.Exists(path)) return;
            ValidateLocalPath(projectRoot, path);
            if (!File.ReadAllText(path).StartsWith("// AIBRIDGE_MODULE_MVID_INDEX_V1:", StringComparison.Ordinal))
                throw new InvalidOperationException("An unrelated native module index exists; refusing to overwrite it.");
            File.WriteAllText(path, CreateIndex(new Dictionary<string, Guid>(), false), new UTF8Encoding(false));
        }

        internal static void Prepare(string projectRoot, string libil2cpp, string payloadDirectory,
            IDictionary<string, Guid> originalDheModules)
        {
            ValidateLocalPath(projectRoot, libil2cpp);
            var modulePath = Path.Combine(libil2cpp, ModulePath);
            var rawPath = Path.Combine(libil2cpp, RawPath);
            var module = File.ReadAllText(modulePath);
            var raw = File.ReadAllText(rawPath);
            var assemblyHeader = File.ReadAllText(Path.Combine(libil2cpp, "hybridclr/metadata/Assembly.h"));
            var hasDhe = assemblyHeader.Contains("IsDifferentialHybridAssembly(");
            if (originalDheModules.Count > 0 && !hasDhe)
                throw new InvalidOperationException("The installed HybridCLR native runtime does not expose DHE assembly classification.");
            var oldFunction = ReadFunction(module, "void RuntimeModule::GetGuidInternal(");
            var newFunction = File.ReadAllText(Path.Combine(payloadDirectory, "RuntimeModuleMvid.txt")).Trim();
            var legacyFunction = File.ReadAllText(Path.Combine(payloadDirectory, "LegacyRuntimeModuleMvid.txt")).Trim();
            var body = oldFunction.Substring(oldFunction.IndexOf('{') + 1).TrimEnd('}').Trim();
            var stock = Regex.IsMatch(body, "^NOT_SUPPORTED_IL2CPP\\(\\s*(?:RuntimeModule|Module)::GetGuidInternal\\s*,\\s*\"[^\"]*\"\\s*\\)\\s*;\\s*$");
            if (!stock && Normalize(oldFunction) != Normalize(newFunction) && Normalize(oldFunction) != Normalize(legacyFunction))
                throw new InvalidOperationException("Unknown RuntimeModule::GetGuidInternal implementation; native support was not changed.");
            var reader = File.ReadAllText(Path.Combine(payloadDirectory, "RawImageMvid.txt")).Trim();
            if (raw.Contains("bool TryReadModuleVersionId("))
            {
                if (Normalize(ReadFunction(raw, "bool TryReadModuleVersionId(")) != Normalize(reader))
                    throw new InvalidOperationException("A different native MVID reader already exists; refusing to replace it.");
            }
            else
            {
                foreach (var required in new[] { "_streamGuidHeap", "_imageLength", "_imageData", "GetRowSchema(", "TABLE5(Module, TableType::MODULE" })
                    if (!raw.Contains(required)) throw new InvalidOperationException("Unsupported HybridCLR RawImageBase layout: " + required);
                const string anchor = "const uint8_t* GetFieldOrParameterDefalutValueByRawIndex(";
                var index = raw.IndexOf(anchor, StringComparison.Ordinal);
                if (index < 0) throw new InvalidOperationException("Unsupported HybridCLR RawImageBase insertion point.");
                raw = raw.Insert(index, reader + "\n\n\t\t");
            }
            module = module.Replace(oldFunction, newFunction);
            var includes = "#include \"hybridclr/metadata/MetadataModule.h\"\n"
                + (hasDhe ? "#include \"hybridclr/metadata/Assembly.h\"\n" : string.Empty)
                + "#include \"hybridclr/generated/AIBridgeModuleMvids.h\"\n";
            if (!module.Contains("#include \"hybridclr/generated/AIBridgeModuleMvids.h\"")) module = includes + module;
            var changes = new Dictionary<string, string>
            {
                [modulePath] = MatchNewlines(module, File.ReadAllText(modulePath)),
                [rawPath] = MatchNewlines(raw, File.ReadAllText(rawPath)),
                [Path.Combine(libil2cpp, IndexPath)] = CreateIndex(originalDheModules, hasDhe)
            };
            var existingIndex = Path.Combine(libil2cpp, IndexPath);
            if (File.Exists(existingIndex) && !File.ReadAllText(existingIndex).StartsWith("// AIBRIDGE_MODULE_MVID_INDEX_V1:", StringComparison.Ordinal))
                throw new InvalidOperationException("An unrelated native module index exists; refusing to overwrite it.");
            WriteWithRollback(projectRoot, changes);
        }

        /// <summary>发布构建前移除已知的 AI Bridge 原生支持，保留项目其它 HybridCLR 改动。</summary>
        internal static void RemoveSupport(string projectRoot, string libil2cpp, string payloadDirectory)
        {
            ValidateLocalPath(projectRoot, libil2cpp);
            if (!Directory.Exists(libil2cpp)) return;
            var modulePath = Path.Combine(libil2cpp, ModulePath);
            var rawPath = Path.Combine(libil2cpp, RawPath);
            var indexPath = Path.Combine(libil2cpp, IndexPath);
            foreach (var path in new[] { modulePath, rawPath, indexPath }) ValidateLocalPath(projectRoot, path);
            var module = File.Exists(modulePath) ? File.ReadAllText(modulePath) : string.Empty;
            var raw = File.Exists(rawPath) ? File.ReadAllText(rawPath) : string.Empty;
            var installed = File.ReadAllText(Path.Combine(payloadDirectory, "RuntimeModuleMvid.txt")).Trim();
            var legacy = File.ReadAllText(Path.Combine(payloadDirectory, "LegacyRuntimeModuleMvid.txt")).Trim();
            var reader = File.ReadAllText(Path.Combine(payloadDirectory, "RawImageMvid.txt")).Trim();
            var current = module.Contains("void RuntimeModule::GetGuidInternal(")
                ? ReadFunction(module, "void RuntimeModule::GetGuidInternal(") : null;
            var recognized = current != null && (Normalize(current) == Normalize(installed) || Normalize(current) == Normalize(legacy));
            var hasIndex = File.Exists(indexPath);
            var hasMarker = HasBridgeMarker(module);
            var currentReader = raw.Contains("bool TryReadModuleVersionId(")
                ? ReadFunction(raw, "bool TryReadModuleVersionId(") : null;
            var recognizedReader = currentReader != null && Normalize(currentReader) == Normalize(reader);
            if (!recognized && !recognizedReader && !hasIndex && !hasMarker) return;
            if (hasMarker && !recognized && (current == null || !IsStockFunction(current)))
                throw new InvalidOperationException("AI Bridge native identity code has changed; Release build cannot remove it safely.");
            if ((recognized || hasMarker || hasIndex) && currentReader != null && !recognizedReader)
                throw new InvalidOperationException("AI Bridge native MVID reader has changed; Release build cannot remove it safely.");
            if (hasIndex) ValidateGeneratedIndex(File.ReadAllText(indexPath));
            var changes = new Dictionary<string, string>();
            if (recognized) module = module.Replace(current, FindStockFunction(projectRoot, current));
            module = RemoveBridgeIncludes(module);
            if (HasBridgeMarker(module))
                throw new InvalidOperationException("AI Bridge native markers remain; Release build was stopped.");
            if (File.Exists(modulePath)) changes[modulePath] = MatchNewlines(module, File.ReadAllText(modulePath));
            if (recognizedReader)
            {
                raw = raw.Replace(currentReader, string.Empty);
                changes[rawPath] = MatchNewlines(raw, File.ReadAllText(rawPath));
            }
            if (hasIndex) changes[indexPath] = null;
            WriteWithRollback(projectRoot, changes);
        }

        private static bool HasBridgeMarker(string source) => source.Contains("AIBRIDGE_MODULE_MVID")
            || source.Contains("AIBridgeModuleMvids") || source.Contains("aibridge_runtime::")
            || source.Contains(".TryReadModuleVersionId(")
            || source.Contains("ModuleVersionId is supported only for loaded HybridCLR interpreter images");

        private static string RemoveBridgeIncludes(string module)
        {
            var normalized = module.Replace("\r\n", "\n");
            var prefix = "#include \"hybridclr/metadata/MetadataModule.h\"\n";
            var assembly = "#include \"hybridclr/metadata/Assembly.h\"\n";
            var index = "#include \"hybridclr/generated/AIBridgeModuleMvids.h\"\n";
            // 新版安装器整块前置这些 include；只移除可确认属于该块的普通头文件引用。
            foreach (var block in new[] { prefix + assembly + index, prefix + index })
                if (normalized.StartsWith(block, StringComparison.Ordinal)) normalized = normalized.Substring(block.Length);
            normalized = Regex.Replace(normalized, @"^[ \t]*#include[ \t]+""hybridclr/generated/AIBridgeModuleMvids\.h""[ \t]*(?:\n|$)", string.Empty, RegexOptions.Multiline);
            return normalized;
        }

        private static string FindStockFunction(string projectRoot, string installed)
        {
            var backups = Path.Combine(projectRoot, ".runtimebridge", "native-support", "backups");
            ValidateLocalPath(projectRoot, backups);
            if (Directory.Exists(backups))
            {
                foreach (var directory in Directory.GetDirectories(backups).OrderByDescending(Directory.GetLastWriteTimeUtc))
                {
                    ValidateLocalPath(projectRoot, directory);
                    var candidate = Path.Combine(directory, "RuntimeModule.cpp");
                    if (!File.Exists(candidate)) continue;
                    ValidateLocalPath(projectRoot, candidate);
                    var source = File.ReadAllText(candidate);
                    if (!source.Contains("void RuntimeModule::GetGuidInternal(")) continue;
                    var function = ReadFunction(source, "void RuntimeModule::GetGuidInternal(");
                    if (IsStockFunction(function)) return function;
                }
            }
            // 旧版手工安装没有快照时，恢复 Unity 原有的“不支持”语义，不引入额外原生能力。
            var signature = installed.Substring(0, installed.IndexOf('{')).TrimEnd();
            return signature + "\n{\n    NOT_SUPPORTED_IL2CPP(Module::GetGuidInternal, \"ModuleVersionId is not supported by IL2CPP.\");\n}";
        }

        private static bool IsStockFunction(string function)
        {
            var body = function.Substring(function.IndexOf('{') + 1).TrimEnd('}').Trim();
            return Regex.IsMatch(body, "^NOT_SUPPORTED_IL2CPP\\(\\s*(?:RuntimeModule|Module)::GetGuidInternal\\s*,\\s*\"[^\"]*\"\\s*\\)\\s*;\\s*$");
        }

        private static void ValidateGeneratedIndex(string source)
        {
            var entries = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(source, @"\{ ""([^""]+)"", \{ ([^}]+) \} \},"))
            {
                var values = match.Groups[2].Value.Split(',');
                if (values.Length != 16 || values.Any(value => !Regex.IsMatch(value.Trim(), "^0x[0-9a-fA-F]{2}$"))
                    || entries.ContainsKey(match.Groups[1].Value))
                    throw new InvalidOperationException("AI Bridge native index has changed; Release build cannot remove it safely.");
                entries.Add(match.Groups[1].Value, new Guid(values.Select(value => Convert.ToByte(value.Trim().Substring(2), 16)).ToArray()));
            }
            if (Normalize(source) != Normalize(CreateIndex(entries, true)) && Normalize(source) != Normalize(CreateIndex(entries, false)))
                throw new InvalidOperationException("AI Bridge native index has changed; Release build cannot remove it safely.");
        }

        internal static string CreateIndex(IDictionary<string, Guid> entries, bool hasDhe)
        {
            var text = new StringBuilder("// AIBRIDGE_MODULE_MVID_INDEX_V1: generated from this Player's compiler outputs.\n#pragma once\n#include <cstring>\nnamespace aibridge_runtime {\n");
            text.AppendLine("struct ModuleMvid { const char* name; uint8_t value[16]; };");
            text.AppendLine("static const ModuleMvid kOriginalDheModules[] = { { nullptr, { 0 } },");
            foreach (var entry in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-')) || entry.Value == Guid.Empty)
                    throw new InvalidDataException("Invalid DHE module identity.");
                text.Append("{ \"").Append(entry.Key).Append("\", { ")
                    .Append(string.Join(", ", entry.Value.ToByteArray().Select(value => "0x" + value.ToString("x2"))))
                    .AppendLine(" } },");
            }
            text.AppendLine("};\ninline bool TryReadOriginalDheMvid(const Il2CppImage* image, uint8_t* destination) {");
            if (hasDhe)
            {
                text.AppendLine("if (image == nullptr || image->assembly == nullptr || image->assembly->token == 0 || destination == nullptr) return false;");
                text.AppendLine("if (!hybridclr::metadata::Assembly::IsDifferentialHybridAssembly(image->nameNoExt)) return false;");
                text.AppendLine("for (const auto& entry : kOriginalDheModules) if (entry.name && std::strcmp(entry.name, image->nameNoExt) == 0) { std::memcpy(destination, entry.value, 16); return true; }");
            }
            text.AppendLine("return false;\n}\n}");
            return text.ToString().Replace("\r\n", "\n");
        }

        private static void WriteWithRollback(string projectRoot, Dictionary<string, string> changes)
        {
            var originals = new Dictionary<string, byte[]>();
            foreach (var path in changes.Keys)
            {
                ValidateLocalPath(projectRoot, path);
                originals[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            // 先验证全部输入，再修改工具链；已有内容按哈希留档，不覆盖用户原来的备份。
            foreach (var entry in originals.Where(pair => pair.Value != null))
            {
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(entry.Value)).Replace("-", string.Empty).ToLowerInvariant();
                var backup = Path.Combine(projectRoot, ".runtimebridge", "native-support", "backups", hash, Path.GetFileName(entry.Key));
                ValidateLocalPath(projectRoot, backup);
                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                if (!File.Exists(backup)) File.WriteAllBytes(backup, entry.Value);
            }
            var written = new List<string>();
            try
            {
                foreach (var entry in changes)
                {
                    var bytes = entry.Value == null ? null : new UTF8Encoding(false).GetBytes(entry.Value);
                    if (bytes == null && originals[entry.Key] == null) continue;
                    if (bytes != null && originals[entry.Key] != null && bytes.SequenceEqual(originals[entry.Key])) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.Key));
                    written.Add(entry.Key);
                    if (bytes == null) File.Delete(entry.Key); else File.WriteAllBytes(entry.Key, bytes);
                }
            }
            catch
            {
                foreach (var path in written)
                    if (originals[path] == null) File.Delete(path); else File.WriteAllBytes(path, originals[path]);
                throw;
            }
        }

        internal static void ValidateLocalPath(string projectRoot, string path)
        {
            var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var absolute = Path.GetFullPath(path);
            if (!absolute.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
                throw new InvalidOperationException("Native support can only modify this project's local toolchain.");
            for (var current = absolute; current != null && !string.Equals(current, root, PathComparison); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Native support paths cannot traverse symbolic links or junctions.");
        }

        private static string ReadFunction(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0 || source.IndexOf(signature, start + signature.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("Expected exactly one native function: " + signature);
            var opening = source.IndexOf('{', start);
            var depth = 1;
            var end = opening + 1;
            if (opening < 0) throw new InvalidDataException("Native function has no body.");
            for (; depth > 0 && end < source.Length; end++)
            {
                if (source[end] == '{') depth++;
                if (source[end] == '}') depth--;
            }
            if (depth != 0) throw new InvalidDataException("Native function is incomplete.");
            return source.Substring(start, end - start);
        }

        private static string Normalize(string text) => string.Concat(text.Where(value => !char.IsWhiteSpace(value)));
        private static string MatchNewlines(string text, string original) => text.Replace("\r\n", "\n").Replace("\n", original.Contains("\r\n") ? "\r\n" : "\n");
    }
}
