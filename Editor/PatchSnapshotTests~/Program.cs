using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AIBridge.Runtime.Editor;

var root = Path.Combine(Path.GetTempPath(), "runtime-patch-snapshot-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var project = Path.Combine(root, "project");
Directory.CreateDirectory(Path.Combine(project, "Assets"));
var passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS: " + name);
}
string Build(string directory, int value)
{
    var path = Path.Combine(root, directory);
    Directory.CreateDirectory(path);
    File.WriteAllText(Path.Combine(path, "Fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>SnapshotFixture</AssemblyName><GenerateAssemblyInfo>false</GenerateAssemblyInfo></PropertyGroup></Project>");
    File.WriteAllText(Path.Combine(path, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
    File.WriteAllText(Path.Combine(path, "Source.cs"), "public sealed class Probe { public int ReadValue() => " + value + "; }");
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "build", Path.Combine(path, "Fixture.csproj"), "--nologo", "--verbosity", "quiet",
        "-p:NuGetAudit=false", "-p:RestoreConfigFile=" + Path.Combine(path, "NuGet.Config"),
        "-p:RestorePackagesPath=" + Path.Combine(root, "packages") }) start.ArgumentList.Add(argument);
    using (var process = Process.Start(start))
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) throw new TimeoutException("Fixture compiler did not finish.");
        if (process.ExitCode != 0) throw new InvalidOperationException(stdout.Result + stderr.Result);
    }
    return Path.Combine(path, "bin/Debug/net8.0/SnapshotFixture.dll");
}
string Pending(string suffix = null)
{
    var path = Path.Combine(project, ".runtimebridge/pending-profiles", suffix ?? Guid.NewGuid().ToString("N") + "_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}
void Copy(string source, string relative)
{
    var destination = Path.Combine(project, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(destination));
    File.Copy(source, destination, true);
}
bool Reject(Action action)
{
    try { action(); return false; }
    catch (ArgumentException) { return true; }
    catch (InvalidDataException) { return true; }
    catch (InvalidOperationException) { return true; }
}
try
{
    var original = Build("baseline", 100);
    var latest = Build("current", 666);
    ManagedAssemblyIdentity.TryReadMvid(original, out var originalMvid);
    ManagedAssemblyIdentity.TryReadMvid(latest, out var latestMvid);
    Check(originalMvid != latestMvid, "fixture contains old packaged bytes and a distinct latest Player compilation");
    Copy(latest, "Library/Bee/PlayerScriptAssemblies/CurrentAlias.dll");
    Copy(original, "Assets/Game/Resources/Raw/OriginalAlias.dll.bytes");
    Copy(original, "Assets/StreamingAssets/Hotfix/StreamAlias.dll.bytes");
    Copy(Assembly.GetExecutingAssembly().Location, "Library/ScriptAssemblies/EditorOnly.dll");
    Copy(Assembly.GetExecutingAssembly().Location, "Assets/Unpacked/NotAResource.dll.bytes");
    Directory.CreateDirectory(Path.Combine(project, "Assets/Game/Resources/Raw"));
    File.WriteAllText(Path.Combine(project, "Assets/Game/Resources/Raw/not-managed.dll.bytes"), "This is not a PE file.");
    Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "SnapshotFixture"), "fixture target has not been loaded in the test process");
    var snapshot = Pending();
    AIBridgeRuntimePatchBuildSnapshot.Capture(project, snapshot);
    var savedOriginal = Path.Combine(snapshot, "patch-baselines", originalMvid.ToString("D"), "SnapshotFixture.dll");
    var savedLatest = Path.Combine(snapshot, "patch-baselines", latestMvid.ToString("D"), "SnapshotFixture.dll");
    Check(File.Exists(savedOriginal) && File.Exists(savedLatest), "both packaged baseline and current compiler MVIDs are retained");
    Check(File.ReadAllBytes(savedOriginal).SequenceEqual(File.ReadAllBytes(original)), "packaged baseline bytes are copied exactly");
    Check(File.ReadAllBytes(savedLatest).SequenceEqual(File.ReadAllBytes(latest)), "Player compiler bytes are copied exactly");
    Check(Directory.GetFiles(Path.Combine(snapshot, "patch-baselines"), "*.dll", SearchOption.AllDirectories).Length == 2,
        "assembly manifest names determine output paths, duplicate sources are deduplicated and Editor/unpacked files are excluded");
    Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "SnapshotFixture"), "snapshot does not load target assemblies into the host");
    using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(snapshot, "patch-baselines/index.json"))))
    {
        var candidates = document.RootElement.GetProperty("candidates").EnumerateArray().ToArray();
        var packaged = candidates.Single(c => c.GetProperty("mvid").GetString() == originalMvid.ToString("D"));
        var kinds = packaged.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("kind").GetString()).ToArray();
        Check(kinds.Contains("packaged-resource") && kinds.Contains("streaming-assets"), "origin index distinguishes resources and streaming assets for a shared baseline");
        var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))).ToLowerInvariant();
        Check(packaged.GetProperty("sha256").GetString() == expectedHash, "origin index binds exact baseline SHA256");
        Check(candidates.Single(c => c.GetProperty("mvid").GetString() == latestMvid.ToString("D"))
            .GetProperty("sources")[0].GetProperty("kind").GetString() == "player-compilation", "compiled Player candidate has explicit provenance");
    }
    AIBridgeRuntimePatchBuildSnapshot.Capture(project, snapshot);
    Check(Directory.GetFiles(Path.Combine(snapshot, "patch-baselines"), "*.dll", SearchOption.AllDirectories).Length == 2, "repeated capture is idempotent");
    var dashedBuild = Pending(Guid.NewGuid().ToString("D") + "_" + Guid.NewGuid().ToString("N"));
    AIBridgeRuntimePatchBuildSnapshot.Capture(project, dashedBuild);
    Check(File.Exists(Path.Combine(dashedBuild, "patch-baselines/index.json")), "standard dashed Player build GUIDs are accepted without loosening nonce validation");
    Check(Reject(() => AIBridgeRuntimePatchBuildSnapshot.Capture(project, Path.Combine(project, ".runtimebridge/profiles", Guid.NewGuid().ToString("N")))), "published profile and outside pending destinations are rejected");
    var invalid = Pending("not-a-build_" + Guid.NewGuid().ToString("N"));
    Check(Reject(() => AIBridgeRuntimePatchBuildSnapshot.Capture(project, invalid)), "invalid build GUID directory is rejected");
    Check(!Directory.Exists(Path.Combine(invalid, "patch-baselines")), "invalid destination produces no baseline directory");
    var altered = File.ReadAllBytes(original).Concat(new byte[] { 1 }).ToArray();
    File.WriteAllBytes(Path.Combine(project, "Assets/Game/Resources/Raw/conflict.dll.bytes"), altered);
    var conflicting = Pending();
    Check(Reject(() => AIBridgeRuntimePatchBuildSnapshot.Capture(project, conflicting)), "same MVID with different bytes is rejected before publishing an index");
    Check(!File.Exists(Path.Combine(conflicting, "patch-baselines/index.json")), "conflicting capture never produces a success index");
    File.Delete(Path.Combine(project, "Assets/Game/Resources/Raw/conflict.dll.bytes"));
    File.WriteAllBytes(savedOriginal, altered);
    Check(Reject(() => AIBridgeRuntimePatchBuildSnapshot.Capture(project, snapshot)), "existing captured baseline corruption is rejected rather than overwritten");
    Console.WriteLine("Passed " + passed + " baseline snapshot checks; no Unity Editor or Player was run.");
}
finally
{
    var absolute = Path.GetFullPath(root);
    if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(absolute).StartsWith("runtime-patch-snapshot-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unexpected fixture cleanup path.");
    Directory.Delete(absolute, true);
}
