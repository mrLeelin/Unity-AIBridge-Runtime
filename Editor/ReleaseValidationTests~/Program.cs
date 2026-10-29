using System.IO.Compression;
using System.Text;
using AIBridge.Runtime.Editor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;

var root = Path.Combine(Path.GetTempPath(), "aibridge-release-validation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
UnityEngine.Application.dataPath = Path.Combine(root, "Assets");
var passed = 0;
void Pass(Action action, string name)
{
    action();
    passed++;
    Console.WriteLine("PASS: " + name);
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (BuildFailedException) { passed++; Console.WriteLine("PASS: " + name); return; }
    throw new Exception("FAIL: " + name);
}
BuildReport Report(string name)
{
    var output = Path.Combine(root, name);
    if (name.EndsWith(".exe", StringComparison.Ordinal)) File.WriteAllBytes(output, Array.Empty<byte>());
    return new BuildReport { summary = new BuildSummary { outputPath = output } };
}
void Archive(string file, string entryName, byte[] bytes)
{
    using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
    using var stream = zip.CreateEntry(entryName).Open();
    stream.Write(bytes);
}
try
{
    Pass(() => AIBridgeRuntimeReleaseValidation.ValidateAssemblyPaths(new[] { "UnityEngine.UI.dll", "HybridCLR.Runtime.dll", "Gameplay.dll" }), "business UI and HybridCLR assemblies remain valid");
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateAssemblyPaths(new[] { "Build/com.sh.aibridge.Runtime.dll" }), "current build assembly list rejects Runtime");
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateAssemblyPaths(new[] { "Assets/StreamingAssets/com.sh.aibridge.Runtime.dll.bytes" }), "explicit Runtime hot-update payload is rejected");

    var input = Path.Combine(root, "linker-input");
    Directory.CreateDirectory(input);
    File.WriteAllBytes(Path.Combine(input, "Gameplay.dll"), Array.Empty<byte>());
    Pass(() => AIBridgeRuntimeReleaseValidation.ValidateLinkerInput(new UnityLinkerBuildPipelineData { inputDirectory = input }), "clean actual linker directory passes");
    File.WriteAllBytes(Path.Combine(input, "com.sh.aibridge.Runtime.dll"), Array.Empty<byte>());
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateLinkerInput(new UnityLinkerBuildPipelineData { inputDirectory = input }), "actual linker Runtime input is rejected");
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateLinkerInput(new UnityLinkerBuildPipelineData()), "missing linker input fails closed");

    var clean = Report("Clean.exe");
    clean.files = new[] { new BuildFile { path = "Gameplay.dll" } };
    Pass(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(clean), "clean managed report passes without reading stale Library output");
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(Report("MissingOutput")), "missing current build output fails closed");
    var managed = Report("Managed.exe");
    managed.files = new[] { new BuildFile { path = "Managed/com.sh.aibridge.Runtime.dll" } };
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(managed), "Mono managed artifact is rejected");
    foreach (var assetPath in new[] { "Assets/Resources/AIBridgeRuntimeConfig.asset", "Assets/Editor/AIBridgeRuntime/AIBridgeRuntimeConfig.asset", "Packages/com.sh.aibridge.runtime/Runtime/AIBridgeRuntime.cs", "LocalPackages/Unity-AIBridge-Runtime/Runtime/AIBridgeRuntime.cs" })
    {
        var config = Report("Packed.exe");
        config.packedAssets = new[] { new PackedAssets { contents = new[] { new PackedAssetInfo { sourceAssetPath = assetPath } } } };
        Reject(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(config), "packed Bridge asset rejected: " + assetPath);
    }

    var metadata = Path.Combine(root, "global-metadata.dat");
    var metadataReport = Report("Metadata.exe");
    metadataReport.files = new[] { new BuildFile { path = metadata } };
    File.WriteAllBytes(metadata, Encoding.UTF8.GetBytes("Gameplay.dll\0com.sh.aibridge.RuntimeExtras.dll\0HybridCLR.Runtime\0"));
    Pass(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(metadataReport), "metadata does not reject unrelated prefix assembly names");
    File.WriteAllBytes(metadata, Encoding.UTF8.GetBytes("\0com.sh.aibridge.Runtime.dll\0"));
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(metadataReport), "IL2CPP metadata with Runtime assembly is rejected");
    File.WriteAllBytes(metadata, Enumerable.Repeat((byte)'x', 65530).Concat(Encoding.UTF8.GetBytes("com.sh.aibridge.Runtime\0")).ToArray());
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(metadataReport), "metadata marker crossing read boundary is rejected");
    File.WriteAllBytes(metadata, Encoding.UTF8.GetBytes("clean\0"));

    var apkClean = Report("clean.apk");
    Archive(apkClean.summary.outputPath, "assets/bin/Data/Managed/Metadata/global-metadata.dat", Encoding.UTF8.GetBytes("Gameplay.dll\0"));
    Pass(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(apkClean), "clean Android APK passes");
    var apkManaged = Report("managed.apk");
    Archive(apkManaged.summary.outputPath, "assets/bin/Data/Managed/com.sh.aibridge.Runtime.dll", Array.Empty<byte>());
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(apkManaged), "Android APK Runtime DLL entry is rejected");
    var aab = Report("runtime.aab");
    Archive(aab.summary.outputPath, "base/assets/bin/Data/Managed/Metadata/global-metadata.dat", Encoding.UTF8.GetBytes("com.sh.aibridge.Runtime.dll\0"));
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(aab), "Android AAB IL2CPP Runtime metadata is rejected");

    var apkRaw = Report("renamed-streaming.apk");
    Archive(apkRaw.summary.outputPath, "assets/data/renamed.payload", Encoding.UTF8.GetBytes("AIBridgeRuntimeConfig"));
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(apkRaw), "Android renamed StreamingAssets config payload is rejected");
    var apkBundle = Report("bundle-streaming.apk");
    Archive(apkBundle.summary.outputPath, "assets/data/opaque", Encoding.UTF8.GetBytes("UnityFS\0compressed fixture"));
    UnityEngine.AssetBundle.Recompress = (input, output) =>
    {
        File.WriteAllText(output, "AIBridgeRuntimeConfig");
        return new UnityEngine.AssetBundleRecompressOperation();
    };
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(apkBundle), "Android compressed StreamingAssets config payload is rejected");

    var standalone = Report("Game.exe");
    var data = Path.Combine(root, "Game_Data/il2cpp_data/Metadata");
    Directory.CreateDirectory(data);
    File.WriteAllBytes(Path.Combine(data, "global-metadata.dat"), Encoding.UTF8.GetBytes("com.sh.aibridge.Runtime.dll\0"));
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(standalone), "standalone Data directory metadata is checked");
    File.WriteAllBytes(Path.Combine(data, "global-metadata.dat"), Encoding.UTF8.GetBytes("clean\0"));
    var streaming = Path.Combine(root, "Game_Data/StreamingAssets");
    Directory.CreateDirectory(streaming);
    File.WriteAllText(Path.Combine(streaming, "renamed.bytes"), "com.sh.aibridge.Runtime");
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(standalone), "standalone renamed StreamingAssets assembly payload is checked");
    var export = Report("iOSExport");
    Directory.CreateDirectory(Path.Combine(export.summary.outputPath, "Il2CppOutputProject/Source/il2cppOutput"));
    File.WriteAllText(Path.Combine(export.summary.outputPath, "Il2CppOutputProject/Source/il2cppOutput/com.sh.aibridge.Runtime.cpp"), "generated source");
    Reject(() => AIBridgeRuntimeReleaseValidation.ValidateBuildReport(export), "exported iOS Runtime C++ translation unit is rejected");
    Console.WriteLine("Passed " + passed + " Release assembly/resource/metadata/archive checks; Unity APIs were stubbed.");
}
finally
{
    var absolute = Path.GetFullPath(root);
    if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(absolute).StartsWith("aibridge-release-validation-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unexpected fixture cleanup path.");
    Directory.Delete(absolute, true);
}
