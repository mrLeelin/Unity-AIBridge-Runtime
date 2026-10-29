using AIBridge.Runtime.Editor;

var root = Path.Combine(Path.GetTempPath(), "bridge-native-" + Guid.NewGuid().ToString("N"));
var native = Path.Combine(root, "HybridCLRData/local/il2cpp/libil2cpp");
var payload = Path.GetFullPath(args[0]);
var module = Path.Combine(native, "icalls/mscorlib/System.Reflection/RuntimeModule.cpp");
var raw = Path.Combine(native, "hybridclr/metadata/RawImageBase.h");
var assembly = Path.Combine(native, "hybridclr/metadata/Assembly.h");
Directory.CreateDirectory(Path.GetDirectoryName(module));
Directory.CreateDirectory(Path.GetDirectoryName(raw));
File.WriteAllText(module, "void RuntimeModule::GetGuidInternal(intptr_t module, Il2CppArray* guid) { NOT_SUPPORTED_IL2CPP(Module::GetGuidInternal, \"unsupported\"); }");
File.WriteAllText(raw, "class RawImageBase { public: /* _streamGuidHeap _imageLength _imageData GetRowSchema( */ TABLE5(Module, TableType::MODULE, generation, name, mvid, encid, encBaseId) const uint8_t* GetFieldOrParameterDefalutValueByRawIndex(uint32_t index) const { return nullptr; } };");
File.WriteAllText(assembly, "class Assembly { public: static bool IsDifferentialHybridAssembly(const char*); };");
var entries = new Dictionary<string, Guid> { ["Business.Core"] = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff") };
var passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
void Reject(Action action, string label) { try { action(); } catch (Exception e) when (e is InvalidOperationException || e is InvalidDataException) { Check(true, label); return; } throw new Exception("FAIL: " + label); }
try
{
    AIBridgeRuntimeNativeSupport.Prepare(root, native, payload, entries);
    var firstModule = File.ReadAllText(module);
    var firstRaw = File.ReadAllText(raw);
    var firstIndex = File.ReadAllText(Path.Combine(native, "hybridclr/generated/AIBridgeModuleMvids.h"));
    Check(firstModule.Contains("AIBRIDGE_MODULE_MVID_V1"), "stock native identity function is upgraded by the package");
    Check(firstRaw.Contains("TryReadModuleVersionId"), "loaded interpreter PE provides its own MVID");
    Check(firstIndex.Contains("Business.Core") && firstIndex.Contains("0x33, 0x22, 0x11, 0x00"), "original DHE identities use raw ECMA GUID byte order");
    Check(firstIndex.Contains("IsDifferentialHybridAssembly") && firstIndex.Contains("assembly->token == 0"), "AOT and unloaded DHE placeholders cannot use original DHE fallback");
    Check(Directory.GetFiles(Path.Combine(root, ".runtimebridge/native-support/backups"), "*", SearchOption.AllDirectories).Length == 2, "existing native inputs are backed up before replacement");
    AIBridgeRuntimeNativeSupport.Prepare(root, native, payload, entries);
    Check(File.ReadAllText(module) == firstModule && File.ReadAllText(raw) == firstRaw, "repeated builds do not duplicate native support");
    AIBridgeRuntimeNativeSupport.Prepare(root, native, payload, new Dictionary<string, Guid>());
    Check(!File.ReadAllText(Path.Combine(native, "hybridclr/generated/AIBridgeModuleMvids.h")).Contains("Business.Core"), "a later build cannot retain stale DHE identities");
    AIBridgeRuntimeNativeSupport.Prepare(root, native, payload, entries);
    AIBridgeRuntimeNativeSupport.ClearGeneratedIndex(root, native);
    Check(!File.ReadAllText(Path.Combine(native, "hybridclr/generated/AIBridgeModuleMvids.h")).Contains("Business.Core"), "disabling debug for a release build clears the previous identity map");
    // Release 必须移除完整原生能力，而非只清空 DHE 映射。
    AIBridgeRuntimeNativeSupport.RemoveSupport(root, native, payload);
    Check(File.ReadAllText(module).Contains("NOT_SUPPORTED_IL2CPP(Module::GetGuidInternal, \"unsupported\");"), "Release restores the original stock identity function from backup");
    Check(!File.ReadAllText(module).Contains("AIBridge") && !File.ReadAllText(module).Contains("AIBRIDGE"), "Release removes AI Bridge includes and markers");
    Check(!File.ReadAllText(raw).Contains("TryReadModuleVersionId") && !File.Exists(Path.Combine(native, "hybridclr/generated/AIBridgeModuleMvids.h")), "Release removes the native reader and generated index");
    var cleanModule = File.ReadAllText(module);
    var cleanRaw = File.ReadAllText(raw);
    AIBridgeRuntimeNativeSupport.RemoveSupport(root, native, payload);
    Check(File.ReadAllText(module) == cleanModule && File.ReadAllText(raw) == cleanRaw, "Release cleanup is idempotent");
    AIBridgeRuntimeNativeSupport.Prepare(root, native, payload, entries);
    Check(File.ReadAllText(module).Contains("AIBRIDGE_MODULE_MVID_V1"), "a Development build can reinstall support after Release cleanup");
    File.AppendAllText(module, "\n// unrelated project native change\n");
    File.AppendAllText(raw, "\n// unrelated project reader change\n");
    AIBridgeRuntimeNativeSupport.RemoveSupport(root, native, payload);
    Check(File.ReadAllText(module).Contains("unrelated project native change") && File.ReadAllText(raw).Contains("unrelated project reader change"), "Release cleanup preserves unrelated native source edits");
    // 兼容曾经手动安装且没有原始备份的旧实现。
    File.WriteAllText(module, File.ReadAllText(Path.Combine(payload, "LegacyRuntimeModuleMvid.txt")));
    File.WriteAllText(raw, File.ReadAllText(Path.Combine(payload, "RawImageMvid.txt")));
    var backupRoot = Path.Combine(root, ".runtimebridge/native-support/backups");
    Directory.Move(backupRoot, backupRoot + "-saved");
    AIBridgeRuntimeNativeSupport.RemoveSupport(root, native, payload);
    Check(File.ReadAllText(module).Contains("NOT_SUPPORTED_IL2CPP(") && !File.ReadAllText(raw).Contains("TryReadModuleVersionId"), "Release cleans a recognized legacy patch without an original snapshot");
    File.WriteAllText(raw, cleanRaw);
    AIBridgeRuntimeNativeSupport.Prepare(root, native, payload, entries);
    File.AppendAllText(Path.Combine(native, "hybridclr/generated/AIBridgeModuleMvids.h"), "\n// unknown local index change\n");
    var modifiedIndex = File.ReadAllText(Path.Combine(native, "hybridclr/generated/AIBridgeModuleMvids.h"));
    var patchedModule = File.ReadAllText(module);
    var patchedRaw = File.ReadAllText(raw);
    Reject(() => AIBridgeRuntimeNativeSupport.RemoveSupport(root, native, payload), "Release rejects a modified AI Bridge generated index");
    Check(File.ReadAllText(module) == patchedModule && File.ReadAllText(raw) == patchedRaw && File.ReadAllText(Path.Combine(native, "hybridclr/generated/AIBridgeModuleMvids.h")) == modifiedIndex, "failed Release validation makes no partial native changes");
    File.WriteAllText(Path.Combine(native, "hybridclr/generated/AIBridgeModuleMvids.h"), firstIndex.Replace("0x33", "bad"));
    Reject(() => AIBridgeRuntimeNativeSupport.RemoveSupport(root, native, payload), "Release rejects malformed native identity bytes");
    File.WriteAllText(Path.Combine(native, "hybridclr/generated/AIBridgeModuleMvids.h"), firstIndex);
    File.WriteAllText(raw, patchedRaw.Replace("return true;", "return false; /* custom reader edit */"));
    Reject(() => AIBridgeRuntimeNativeSupport.RemoveSupport(root, native, payload), "Release rejects a modified native reader");
    File.WriteAllText(raw, patchedRaw);
    File.WriteAllText(module, patchedModule.Replace("AIBRIDGE_MODULE_MVID_V1", "AIBRIDGE_MODULE_MVID_V1_modified"));
    Reject(() => AIBridgeRuntimeNativeSupport.RemoveSupport(root, native, payload), "Release rejects a modified native identity function");
    File.WriteAllText(module, patchedModule);
    AIBridgeRuntimeNativeSupport.RemoveSupport(root, native, payload);
    File.WriteAllText(module, "#include \"hybridclr/generated/AIBridgeModuleMvids.h\"\n" + cleanModule);
    File.WriteAllText(Path.Combine(native, "hybridclr/generated/AIBridgeModuleMvids.h"), firstIndex);
    AIBridgeRuntimeNativeSupport.RemoveSupport(root, native, payload);
    Check(File.ReadAllText(module) == cleanModule && !File.Exists(Path.Combine(native, "hybridclr/generated/AIBridgeModuleMvids.h")), "Release cleans a partially reverted patch that retains its owned include and index");
    AIBridgeRuntimeNativeSupport.RemoveSupport(root, Path.Combine(root, "uninstalled/libil2cpp"), payload);
    Check(!Directory.Exists(Path.Combine(root, "uninstalled/libil2cpp")), "Release cleanup of an uninstalled SDK creates no files");
    File.WriteAllText(module, "void RuntimeModule::GetGuidInternal(intptr_t module, Il2CppArray* guid) { custom_identity(); }");
    AIBridgeRuntimeNativeSupport.RemoveSupport(root, native, payload);
    Check(File.ReadAllText(module).Contains("custom_identity"), "clean non-AI-Bridge native identity implementations are preserved");
    Reject(() => AIBridgeRuntimeNativeSupport.Prepare(root, native, payload, entries), "unknown custom native function is preserved");
    Check(File.ReadAllText(module).Contains("custom_identity"), "failed validation leaves original source untouched");
    File.WriteAllText(module, "void RuntimeModule::GetGuidInternal(intptr_t module, Il2CppArray* guid) { custom_identity(); NOT_SUPPORTED_IL2CPP(Module::GetGuidInternal, \"unsupported\"); }");
    Reject(() => AIBridgeRuntimeNativeSupport.Prepare(root, native, payload, entries), "stock marker does not authorize overwriting extra custom native code");
    Reject(() => AIBridgeRuntimeNativeSupport.ValidateLocalPath(root, Path.GetTempPath()), "global or external toolchains cannot be modified");
    Reject(() => AIBridgeRuntimeNativeSupport.CreateIndex(new Dictionary<string, Guid> { ["bad\"name"] = Guid.NewGuid() }, true), "native assembly name injection is rejected");
    Reject(() => AIBridgeRuntimeNativeSupport.CreateIndex(new Dictionary<string, Guid> { ["Business"] = Guid.Empty }, true), "empty module identities cannot enter native build");
    if (args.Length > 1)
    {
        // 用真实 SDK 的宏、字段及 public 区域验证首次安装，不依赖测试工程已有的手工 reader。
        var sdkRaw = File.ReadAllText(Path.Combine(args[1], "hybridclr/metadata/RawImageBase.h"));
        var start = sdkRaw.IndexOf("bool TryReadModuleVersionId(", StringComparison.Ordinal);
        if (start >= 0)
        {
            var opening = sdkRaw.IndexOf('{', start);
            var depth = 1;
            var end = opening + 1;
            for (; depth > 0; end++) { if (sdkRaw[end] == '{') depth++; if (sdkRaw[end] == '}') depth--; }
            sdkRaw = sdkRaw.Remove(start, end - start);
        }
        File.WriteAllText(raw, sdkRaw);
        File.WriteAllText(module, "void RuntimeModule::GetGuidInternal(intptr_t module, Il2CppArray* guid) { NOT_SUPPORTED_IL2CPP(Module::GetGuidInternal, \"unsupported\"); }");
        File.Copy(Path.Combine(args[1], "hybridclr/metadata/Assembly.h"), assembly, true);
        AIBridgeRuntimeNativeSupport.Prepare(root, native, payload, entries);
        Check(File.ReadAllText(raw).Contains("TryReadModuleVersionId"), "real SDK without the previous hand-installed reader is upgraded automatically");
    }
    Console.WriteLine("Passed " + passed + " native support checks.");
}
finally { Directory.Delete(root, true); }
