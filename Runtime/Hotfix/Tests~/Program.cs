using AIBridge.Runtime;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;
using FakeApi = HybridCLR.RuntimeApi;

var target = typeof(Fixture).Assembly;
var bytes = File.ReadAllBytes(target.Location);
var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
var mvid = target.ManifestModule.ModuleVersionId.ToString("D");
var settings = new AIBridgeRuntimeSettings { enableRuntimeBridge = true, enableRuntimeCodeExecution = true };
var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    passed++;
    Console.WriteLine("PASS: " + label);
}
AIBridgeRuntimeCommand Request(string signature = "Int32 Compute(Int32)", string patchId = "patch-1") => new AIBridgeRuntimeCommand
{
    Id = Guid.NewGuid().ToString("N"), Action = "runtime.patch.apply",
    Params = new Dictionary<string, object>
    {
        ["assemblyName"] = target.GetName().Name,
        ["assemblyBase64"] = Convert.ToBase64String(bytes),
        ["sha256"] = hash,
        ["baselineMvid"] = mvid,
        ["riskAccepted"] = true,
        ["structureVerified"] = true,
        ["patchId"] = patchId,
        ["types"] = new List<object>
        {
            new Dictionary<string, object>
            {
                ["name"] = typeof(Fixture).FullName,
                ["methods"] = new List<object> { new Dictionary<string, object> { ["signature"] = signature } }
            }
        }
    }
};
object Field(object data, string name) => data.GetType().GetProperty(name).GetValue(data);
var handler = new AIBridgeHotfixHandler(settings);
Application.isEditor = true;
Check(!AIBridgeHotfixHandler.PrepareRuntime(), "Editor preparation is unavailable");
Check(!handler.HandleCommand(Request()).Success, "Editor patch apply is rejected instead of pretending success");
Application.isEditor = false;
Debug.isDebugBuild = false;
Check(!AIBridgeHotfixHandler.PrepareRuntime(), "non-development Player preparation is rejected");
Debug.isDebugBuild = true;
Check(AIBridgeHotfixHandler.PrepareRuntime() && FakeApi.InlineDepth == 0, "compatible runtime API disables method inlining");

foreach (var key in new[] { "riskAccepted", "structureVerified" })
{
    var request = Request(); request.Params[key] = false;
    Check(!handler.HandleCommand(request).Success, key + " is mandatory");
}
var badHash = Request(); badHash.Params["sha256"] = new string('0', 64);
Check(!handler.HandleCommand(badHash).Success, "wrong DLL hash is rejected");
var badMvid = Request(); badMvid.Params["baselineMvid"] = Guid.NewGuid().ToString("D");
Check(!handler.HandleCommand(badMvid).Success, "wrong loaded baseline MVID is rejected");
Check(!handler.HandleCommand(Request("Int32 Missing(Int32)")).Success, "missing exact target method is rejected");
var badType = Request(); ((Dictionary<string, object>)((List<object>)badType.Params["types"])[0])["name"] = "Missing.Type";
Check(!handler.HandleCommand(badType).Success, "missing target type is rejected");
var nameOnly = Request();
var methodData = (Dictionary<string, object>)((List<object>)((Dictionary<string, object>)((List<object>)nameOnly.Params["types"])[0])["methods"])[0];
methodData["name"] = "Compute";
Check(!handler.HandleCommand(nameOnly).Success, "name-based overload selection is rejected");
Check(FakeApi.Calls == 0, "all failed preflights leave native patch API untouched");

var result = handler.HandleCommand(Request());
Check(result.Success && FakeApi.Calls == 1, "valid manifest invokes native adapter once");
Check(FakeApi.LastTypes[0].methods[0].signature == "Int32 Compute(Int32)", "overloaded method retains exact selected signature");
result = handler.HandleCommand(Request());
Check(result.Success && (bool)Field(result.Data, "deduplicated") && FakeApi.Calls == 1, "identical successful patch is deduplicated");
Check(!handler.HandleCommand(Request("Int32 Compute(System.String)")).Success && FakeApi.Calls == 1, "reusing patch ID for another target selection is rejected");
var stringSignature = typeof(Fixture).GetMethod("Compute", new[] { typeof(string) }).ToString();
result = handler.HandleCommand(Request(stringSignature, "patch-2"));
Check(result.Success && FakeApi.Calls == 2, "another exact overload can be applied independently");
var history = handler.HandleCommand(new AIBridgeRuntimeCommand { Id = "history", Action = "runtime.patch.history" });
Check(((Array)Field(history.Data, "entries")).Length == 2, "history records native patch attempts");

result = handler.HandleCommand(Request(".ctor(Int32)", "constructor-patch"));
Check(result.Success, "friendly constructor signature selects the exact public constructor");
var constructorSignature = typeof(Fixture).GetConstructor(new[] { typeof(int) }).ToString();
Check(FakeApi.LastTypes[0].methods[0].signature == constructorSignature && constructorSignature == "Void .ctor(Int32)",
    "constructor manifest uses the full reflection signature expected by HybridCLR 8.11.1");

var limited = new AIBridgeHotfixHandler(settings, 1);
Check(limited.HandleCommand(Request()).Success, "bounded handler accepts first patch");
Check(limited.HandleCommand(Request()).Success, "deduplicated request remains usable at attempt limit");
var callsBeforeLimit = FakeApi.Calls;
Check(!limited.HandleCommand(Request(stringSignature, "limit-2")).Success && FakeApi.Calls == callsBeforeLimit, "attempt limit rejects additional DLL loads");

var failing = new AIBridgeHotfixHandler(settings, 1);
FakeApi.ThrowOnApply = true;
var callsBeforeFailure = FakeApi.Calls;
Check(!failing.HandleCommand(Request()).Success && FakeApi.Calls == callsBeforeFailure + 1, "native exception is not reported as successful patch");
FakeApi.ThrowOnApply = false;
Check(!failing.HandleCommand(Request()).Success && FakeApi.Calls == callsBeforeFailure + 1, "failed native attempt also consumes bounded load budget");
var failureHistory = failing.HandleCommand(new AIBridgeRuntimeCommand { Id = "history", Action = "runtime.patch.history" });
var failureEntries = (Array)Field(failureHistory.Data, "entries");
Check(!(bool)((Dictionary<string, object>)failureEntries.GetValue(0))["applied"], "failed application remains failed in patch history");

var foreignThread = Task.Run(() => handler.HandleCommand(Request("Int32 Compute(Int32)", "foreign-thread"))).GetAwaiter().GetResult();
Check(!foreignThread.Success, "patch application is restricted to constructor's Unity thread");
settings.enableRuntimeCodeExecution = false;
Check(!handler.HandleCommand(Request()).Success, "disabled runtime code execution blocks patches");
Console.WriteLine("Passed " + passed + " host-side handler regression checks; no real IL2CPP native implementation was exercised.");
return 0;

public sealed class Fixture
{
    public Fixture(int value) { }
    public int Compute(int value) => value + 1;
    public int Compute(string value) => value.Length;
}
