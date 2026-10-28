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
    File.WriteAllText(module, "void RuntimeModule::GetGuidInternal(intptr_t module, Il2CppArray* guid) { custom_identity(); }");
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
