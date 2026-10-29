using System.Text;
using AIBridge.Runtime.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

var root = Path.Combine(Path.GetTempPath(), "aibridge-streaming-validation-" + Guid.NewGuid().ToString("N"));
var assets = Path.Combine(root, "Assets");
var streaming = Path.Combine(assets, "StreamingAssets");
Directory.CreateDirectory(streaming);
Application.dataPath = assets;
var passed = 0;
void Pass(Action action, string name)
{
    action(); passed++; Console.WriteLine("PASS: " + name);
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (BuildFailedException) { passed++; Console.WriteLine("PASS: " + name); return; }
    throw new Exception("FAIL: " + name);
}
string Write(string name, byte[] bytes)
{
    var file = Path.Combine(streaming, name);
    File.WriteAllBytes(file, bytes);
    return file;
}
bool TemporaryFilesRemain() => Directory.Exists(Path.Combine(root, "Temp"))
    && Directory.EnumerateFiles(Path.Combine(root, "Temp"), "*", SearchOption.AllDirectories).Any();
try
{
    var clean = Write("business.bytes", Encoding.UTF8.GetBytes("Gameplay.dll\0HybridCLR.Runtime.dll\0"));
    Pass(() => AIBridgeRuntimeStreamingAssetsValidation.ValidateFile(clean), "business raw payload passes");
    var renamedDll = Write("renamed.payload", Encoding.UTF8.GetBytes("binary\0com.sh.aibridge.Runtime\0"));
    Reject(() => AIBridgeRuntimeStreamingAssetsValidation.ValidateFile(renamedDll), "renamed Runtime assembly payload is rejected");
    File.Delete(renamedDll);
    var config = Write("config.any", Encoding.Unicode.GetBytes("AIBridgeRuntimeConfig"));
    Reject(() => AIBridgeRuntimeStreamingAssetsValidation.ValidateFile(config), "UTF-16 serialized configuration is rejected");
    File.Delete(config);
    var boundary = Write("boundary", Enumerable.Repeat((byte)'x', 65530).Concat(Encoding.UTF8.GetBytes("AIBridgeRuntimeConfig")).ToArray());
    Reject(() => AIBridgeRuntimeStreamingAssetsValidation.ValidateFile(boundary), "marker across read boundary is rejected");
    File.Delete(boundary);
    Write("skipped.meta", Encoding.UTF8.GetBytes("AIBridgeRuntimeConfig"));
    Pass(() => AIBridgeRuntimeStreamingAssetsValidation.Validate(streaming), "Unity .meta files excluded from Player are skipped");

    foreach (var header in new[] { "UnityFS\0", "UnityRaw\0", "UnityWeb\0" })
    {
        var bundle = Write("opaque", Encoding.ASCII.GetBytes(header + "compressed fixture"));
        AssetBundle.Recompress = (input, output) =>
        {
            File.WriteAllBytes(output, Encoding.UTF8.GetBytes("uncompressed game data\0"));
            return new AssetBundleRecompressOperation();
        };
        Pass(() => AIBridgeRuntimeStreamingAssetsValidation.ValidateFile(bundle), header.TrimEnd('\0') + " header is recompressed without extension assumptions");
        if (TemporaryFilesRemain()) throw new Exception("Clean bundle temporary data was not removed.");
    }
    var compressed = Path.Combine(streaming, "opaque");
    AssetBundle.Recompress = (input, output) =>
    {
        File.WriteAllBytes(output, Encoding.UTF8.GetBytes("com.sh.aibridge.Runtime\0"));
        return new AssetBundleRecompressOperation();
    };
    Reject(() => AIBridgeRuntimeStreamingAssetsValidation.ValidateFile(compressed), "compressed Runtime data is rejected after recompression");
    Pass(() => { if (TemporaryFilesRemain()) throw new Exception("Rejected bundle temporary data was not removed."); }, "rejected bundle temporary data is removed");
    AssetBundle.Recompress = (input, output) => new AssetBundleRecompressOperation { success = false, result = AssetBundleLoadResult.FailedDecompression };
    Reject(() => AIBridgeRuntimeStreamingAssetsValidation.ValidateFile(compressed), "recompression failure is not approved");
    AssetBundle.Recompress = (input, output) => new AssetBundleRecompressOperation { success = true, result = AssetBundleLoadResult.FailedDecompression };
    Reject(() => AIBridgeRuntimeStreamingAssetsValidation.ValidateFile(compressed), "both recompression success and result are required");
    AssetBundle.Recompress = (input, output) => null;
    Reject(() => AIBridgeRuntimeStreamingAssetsValidation.ValidateFile(compressed), "null native operation fails closed");

    var pending = new AssetBundleRecompressOperation { isDone = false };
    AssetBundle.Recompress = (input, output) => { File.WriteAllText(output, "pending native output"); return pending; };
    Reject(() => AIBridgeRuntimeStreamingAssetsValidation.ValidateFile(compressed, 1), "native operation timeout fails closed");
    Pass(() => { if (!TemporaryFilesRemain()) throw new Exception("Active native output was deleted prematurely."); }, "active native output survives timeout until native work completes");
    pending.isDone = true;
    EditorApplication.update?.Invoke();
    Pass(() => { if (TemporaryFilesRemain() || EditorApplication.update != null) throw new Exception("Deferred cleanup did not finish."); }, "completed timed-out operation removes only owned temporary data");
    Console.WriteLine("Passed " + passed + " StreamingAssets raw/bundle/timeout/cleanup checks; native recompression was stubbed.");
}
finally
{
    var absolute = Path.GetFullPath(root);
    if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(absolute).StartsWith("aibridge-streaming-validation-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unexpected fixture cleanup path.");
    Directory.Delete(absolute, true);
}
