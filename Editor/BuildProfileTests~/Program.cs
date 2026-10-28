using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AIBridge.Runtime.Editor;
using UnityEditor;
using UnityEditor.Build.Reporting;

var root = Path.Combine(Path.GetTempPath(), "runtime-build-profile-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "Assets"));
UnityEngine.Application.dataPath = Path.Combine(root, "Assets");
UnityEngine.Resources.Configuration = new AIBridge.Runtime.AIBridgeRuntimeConfig();
var stripped = Path.Combine(root, "Library/Bee/artifacts/Android/ManagedStripped");
Directory.CreateDirectory(stripped);
var originalAssembly = Assembly.GetExecutingAssembly().Location;
var alternateAssembly = typeof(JsonSerializer).Assembly.Location;
var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(originalAssembly))).ToLowerInvariant();
var passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS: " + name);
}
BuildReport NewReport()
{
    EditorApplication.delayCall = null;
    UnityEngine.Debug.Errors.Clear();
    BuildPipeline.isBuildingPlayer = true;
    UnityEditor.Compilation.CompilationPipeline.Defines = new[] { "UNITY_ANDROID", "UNITY_EDITOR", "UNITY_EDITOR_WIN" };
    foreach (var name in new[] { "mscorlib.dll", "UnityEngine.CoreModule.dll", "Unwanted.Editor.dll" })
        File.Copy(originalAssembly, Path.Combine(stripped, name), true);
    var report = new BuildReport();
    BuildReport.Latest = report;
    return report;
}
string ProfilePath(BuildReport report) => Path.GetFullPath(Path.Combine(root, ".runtimebridge/profiles", report.summary.guid.ToString(), "profile.json"));
string PendingPath() => Path.Combine(root, ".runtimebridge/pending-profiles");
int PendingCount() => Directory.Exists(PendingPath()) ? Directory.GetDirectories(PendingPath()).Length : 0;
void FinalizeReport(BuildReport report, BuildResult result)
{
    report.summary.result = result;
    BuildPipeline.isBuildingPlayer = false;
    EditorApplication.Tick();
}
void Resume()
{
    typeof(AIBridgeRuntimeBuildProfile).GetMethod("ResumePendingExports", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
    EditorApplication.Tick();
}
try
{
    var processor = new AIBridgeRuntimeBuildProfile();
    var successful = NewReport();
    processor.OnPostprocessBuild(successful);
    Check(!File.Exists(ProfilePath(successful)) && PendingCount() == 1, "Unknown callback result captures references without publishing a profile");
    EditorApplication.Tick();
    Check(!File.Exists(ProfilePath(successful)), "a running build cannot publish its pending profile");
    File.Copy(alternateAssembly, Path.Combine(stripped, "mscorlib.dll"), true);
    UnityEditor.Compilation.CompilationPipeline.Defines = new[] { "UNITY_WRONG_LATER_BUILD" };
    BuildReport.Latest = new BuildReport();
    FinalizeReport(successful, BuildResult.Succeeded);
    Check(File.Exists(ProfilePath(successful)) && PendingCount() == 0, "final success publishes the original held report even after another report becomes latest");
    using (var json = JsonDocument.Parse(File.ReadAllText(ProfilePath(successful))))
    {
        var data = json.RootElement;
        var fingerprint = data.GetProperty("assemblyFingerprints").EnumerateArray().Single(v => v.GetProperty("name").GetString() == "mscorlib");
        Check(fingerprint.GetProperty("sha256").GetString() == originalHash, "reference fingerprints remain bound to the callback snapshot after ManagedStripped is overwritten");
        Check(data.GetProperty("defineSymbols").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "UNITY_ANDROID" }), "Player defines are snapshotted and exclude every UNITY_EDITOR variant");
        Check(!data.GetProperty("references").EnumerateArray().Any(v => v.GetString().Contains("Editor")), "Editor assemblies are excluded from the snapshot");
    }
    var copied = Path.Combine(Path.GetDirectoryName(ProfilePath(successful)), "references/mscorlib.dll");
    Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copied))).ToLowerInvariant() == originalHash, "published DLL bytes match the captured fingerprint");

    var failed = NewReport();
    processor.OnPostprocessBuild(failed);
    FinalizeReport(failed, BuildResult.Failed);
    Check(!File.Exists(ProfilePath(failed)) && PendingCount() == 0, "failed final builds discard their pending snapshot without publication");

    var cancelled = NewReport();
    processor.OnPostprocessBuild(cancelled);
    FinalizeReport(cancelled, BuildResult.Cancelled);
    Check(!File.Exists(ProfilePath(cancelled)) && PendingCount() == 0, "cancelled builds do not publish profiles");

    var changed = NewReport();
    processor.OnPostprocessBuild(changed);
    changed.summary.guid = Guid.NewGuid();
    FinalizeReport(changed, BuildResult.Succeeded);
    Check(!File.Exists(ProfilePath(changed)) && PendingCount() == 0 && UnityEngine.Debug.Errors.Any(v => v.Contains("identity changed")), "a changed report GUID is rejected");

    var mutated = NewReport();
    processor.OnPostprocessBuild(mutated);
    var captured = Directory.GetDirectories(PendingPath()).Single();
    File.Copy(alternateAssembly, Path.Combine(captured, "references/mscorlib.dll"), true);
    FinalizeReport(mutated, BuildResult.Succeeded);
    Check(!File.Exists(ProfilePath(mutated)) && PendingCount() == 0 && UnityEngine.Debug.Errors.Any(v => v.Contains("fingerprints changed")), "modified pending reference bytes cannot be published under old fingerprints");

    var reload = NewReport();
    processor.OnPostprocessBuild(reload);
    EditorApplication.delayCall = null;
    reload.summary.result = BuildResult.Succeeded;
    BuildPipeline.isBuildingPlayer = false;
    Resume();
    Check(File.Exists(ProfilePath(reload)) && PendingCount() == 0, "a domain reload resumes the matching finalized successful report");

    var reloadFailed = NewReport();
    processor.OnPostprocessBuild(reloadFailed);
    EditorApplication.delayCall = null;
    reloadFailed.summary.result = BuildResult.Failed;
    BuildPipeline.isBuildingPlayer = false;
    Resume();
    Check(!File.Exists(ProfilePath(reloadFailed)) && PendingCount() == 0, "domain reload recovery does not publish a failed build");

    var stale = NewReport();
    processor.OnPostprocessBuild(stale);
    EditorApplication.delayCall = null;
    BuildReport.Latest = new BuildReport { summary = new BuildSummary { result = BuildResult.Succeeded } };
    BuildPipeline.isBuildingPlayer = false;
    Resume();
    Check(!File.Exists(ProfilePath(stale)) && PendingCount() == 0, "recovery cannot bind a stale snapshot to another build GUID");

    var unknown = NewReport();
    processor.OnPostprocessBuild(unknown);
    BuildPipeline.isBuildingPlayer = false;
    for (var index = 0; index < 31; index++) EditorApplication.Tick();
    Check(!File.Exists(ProfilePath(unknown)) && PendingCount() == 0 && UnityEngine.Debug.Errors.Any(v => v.Contains("not finalized")), "a permanently unknown result is never treated as success");

    var manual = NewReport();
    manual.summary.result = BuildResult.Succeeded;
    var path = AIBridgeRuntimeBuildProfile.ExportProfile(manual);
    Check(path == ProfilePath(manual) && File.Exists(path), "explicit successful-report export remains available");
    Check(AIBridgeRuntimeBuildProfile.ExportProfile(manual) == path && PendingCount() == 0, "repeated identical export is idempotent");
    File.Copy(alternateAssembly, Path.Combine(Path.GetDirectoryName(path), "references/mscorlib.dll"), true);
    var alteredPublished = false;
    try { AIBridgeRuntimeBuildProfile.ExportProfile(manual); }
    catch (InvalidOperationException error) { alteredPublished = error.Message.Contains("Published Player reference fingerprints changed"); }
    Check(alteredPublished && PendingCount() == 0, "existing published DLL tampering is rejected even when profile JSON is unchanged");
    var rejected = false;
    manual.summary.result = BuildResult.Unknown;
    try { AIBridgeRuntimeBuildProfile.ExportProfile(manual); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "explicit export still rejects unfinished build reports");

    var linker = new AIBridgeRuntimeLinkerProcessor();
    var development = NewReport();
    var linkerFile = linker.GenerateAdditionalLinkXmlFile(development, new UnityEditor.UnityLinker.UnityLinkerBuildPipelineData());
    var linkerXml = System.Xml.Linq.XDocument.Load(linkerFile);
    Check(linkerXml.Descendants("type").Any(t => (string)t.Attribute("fullname") == "System.Runtime.CompilerServices.AsyncTaskMethodBuilder*"), "Development linker callback preserves snippet compiler async builders");
    Check(new[] { "UnityEngine.GameObject", "UnityEngine.PrimitiveType", "UnityEngine.Transform", "UnityEngine.Light", "UnityEngine.MeshFilter", "UnityEngine.MeshRenderer", "UnityEngine.BoxCollider" }
        .All(name => linkerXml.Descendants("type").Any(t => (string)t.Attribute("fullname") == name && (string)t.Attribute("preserve") == "all")),
        "Development linker callback retains primitive creation, transforms, lighting and required components");
    development.summary.options = BuildOptions.None;
    Check(linker.GenerateAdditionalLinkXmlFile(development, new UnityEditor.UnityLinker.UnityLinkerBuildPipelineData()) == null, "release linker callback contributes no debugging compiler preservation file");
    Console.WriteLine("Passed " + passed + " Player profile/linker lifecycle checks; Unity callbacks were stubbed, no Android build was run.");
}
finally
{
    var absolute = Path.GetFullPath(root);
    if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(absolute).StartsWith("runtime-build-profile-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unexpected fixture cleanup path.");
    Directory.Delete(absolute, true);
}
