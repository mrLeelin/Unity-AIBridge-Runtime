using System.Text.Json;
using AIBridge.Runtime.Editor;

var root = Path.Combine(Path.GetTempPath(), "runtime-window-backend-" + Guid.NewGuid().ToString("N"));
var project = Path.Combine(root, "project");
var package = Path.Combine(root, "package");
Directory.CreateDirectory(Path.Combine(project, "Assets"));
Directory.CreateDirectory(Path.Combine(package, "Tools~"));
UnityEngine.Application.dataPath = Path.Combine(project, "Assets");
UnityEditor.PackageManager.PackageInfo.Root = package;
var script = Path.Combine(package, "Tools~", "runtimebridge.py");
File.WriteAllText(script, """
import json, os, pathlib, sys
args = sys.argv[1:]
token = os.environ.get('RUNTIMEBRIDGE_TOKEN', '')
if args[0] == 'fail':
    print('failure:' + token, file=sys.stderr)
    raise SystemExit(3)
code = None
if '--file' in args:
    code = pathlib.Path(args[args.index('--file') + 1]).read_text(encoding='utf-8')
print(json.dumps({'argv':args,'token':token,'code':code}, ensure_ascii=False))
""");
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS: " + name);
}
try
{
    var token = "window-token\"quoted\\line\nprivate";
    var argument = "C:\\folder with spaces\\quoted\"name\\";
    var result = await AIBridgeRuntimeWindowBackend.RunAsync("python", new[] { "status", "--fixture-path", argument }, "http://127.0.0.1:27182", token);
    Check(result.Success, "CLI subprocess completes without blocking the caller");
    using var json = JsonDocument.Parse(result.Output);
    var receivedArgs = json.RootElement.GetProperty("argv").EnumerateArray().Select(v => v.GetString()).ToArray();
    Check(receivedArgs[2] == argument, "spaces, embedded quotes and trailing backslash survive argument quoting");
    Check(receivedArgs.Contains("--url") && receivedArgs.Contains("--timeout"), "HTTP commands receive URL and timeout");
    Check(!receivedArgs.Contains("--token") && !receivedArgs.Contains(token), "Token is absent from command-line arguments");
    Check(json.RootElement.GetProperty("token").GetString() == "<REDACTED>", "JSON-escaped Token is redacted from output");

    var code = "using System; var text = \"Refusing to overwrite\"; return 42;";
    var execution = await AIBridgeRuntimeWindowBackend.RunAsync("python", new[] { "exec", "--profile", "profile with spaces.json" }, "http://127.0.0.1:27182", "test-token", temporaryCode: code);
    using var source = JsonDocument.Parse(execution.Output);
    var executionArgs = source.RootElement.GetProperty("argv").EnumerateArray().Select(v => v.GetString()).ToArray();
    var codePath = executionArgs[Array.IndexOf(executionArgs, "--file") + 1];
    Check(source.RootElement.GetProperty("code").GetString() == code, "snippet is passed through a UTF-8 file unchanged");
    Check(!File.Exists(codePath), "owned temporary code file is removed after execution");

    var devices = await AIBridgeRuntimeWindowBackend.RunAsync("python", new[] { "devices", "--adb", "adb with spaces.exe" }, "http://127.0.0.1:27182", "secret-not-for-adb");
    using var local = JsonDocument.Parse(devices.Output);
    Check(!local.RootElement.GetProperty("argv").EnumerateArray().Any(v => v.GetString() == "--url"), "local device commands do not receive HTTP arguments");
    Check(local.RootElement.GetProperty("token").GetString() == "", "ADB helper does not inherit the Runtime Token");

    var failed = await AIBridgeRuntimeWindowBackend.RunAsync("python", new[] { "fail" }, "http://127.0.0.1:27182", "fixture-private-token");
    Check(!failed.Success && failed.ExitCode == 3 && !failed.Error.Contains("fixture-private-token"), "failed command preserves exit code and redacts stderr");
    var pretty = AIBridgeRuntimeWindowBackend.PrettyJson("{\"text\":\"brace } and \\\"quote\\\"\",\"items\":[1,2]}");
    using var prettyJson = JsonDocument.Parse(pretty);
    Check(pretty.Contains('\n') && prettyJson.RootElement.GetProperty("text").GetString() == "brace } and \"quote\"", "pretty formatter preserves quoted braces and escapes");
    Check(AIBridgeRuntimeWindowBackend.PrettyJson("plain error") == "plain error", "plain error text remains readable");

    File.Move(script, script + ".held");
    var missing = await AIBridgeRuntimeWindowBackend.RunAsync("python", new[] { "status" }, "http://127.0.0.1:27182", "not-for-error");
    Check(!missing.Success && !string.IsNullOrEmpty(missing.Error), "missing installed CLI returns an actionable failure");
    Console.WriteLine("Passed " + passed + " Runtime window backend checks; no Unity GUI or real phone was exercised.");
}
finally
{
    var resolved = Path.GetFullPath(root);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(resolved).StartsWith("runtime-window-backend-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unexpected fixture cleanup path.");
    Directory.Delete(resolved, true);
}
