using System.Globalization;
using AIBridge.Runtime.Editor;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
RuntimeResultSummary Summary(string value) => AIBridgeRuntimePresentation.Summarize("{\"Success\":true,\"Data\":{\"returnValue\":" + value + ",\"elapsedMs\":7.2}}", true, "执行代码");
var culture = CultureInfo.CurrentCulture;
try
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
    var zero = Summary("0");
    Check(zero.Success && zero.HasValue && zero.Value == "0" && zero.Detail.Contains("7.2 ms"), "zero is a real return value and elapsed time uses invariant formatting");
    Check(Summary("false").HasValue && Summary("false").Value == "false", "false is not mistaken for an absent result");
    Check(Summary("null").HasValue && Summary("null").Value == "null", "explicit null remains distinct from no returnValue property");
    Check(Summary("\"游戏已就绪\"").Value == "游戏已就绪", "string results are readable without JSON quoting");
    Check(Summary("{\"platform\":\"Android\",\"calls\":3}").Value.Contains("\"calls\": 3"), "object return values retain their fields");
    Check(Summary("[1,false,null]").Value.Contains("false") && Summary("[1,false,null]").Value.Contains("null"), "array values retain false and null elements");
    var empty = AIBridgeRuntimePresentation.Summarize("{\"Success\":true,\"Data\":{}}", true, "查询状态");
    Check(!empty.HasValue && empty.Success, "an absent value does not produce a fabricated null return");
    var failure = AIBridgeRuntimePresentation.Summarize("{\"Success\":false,\"Error\":\"bad baseline\",\"Data\":{\"returnValue\":100}}", true, "应用补丁");
    Check(!failure.Success && !failure.HasValue && failure.Detail == "bad baseline", "remote failure cannot be presented as a successful return value");
    var lowercase = AIBridgeRuntimePresentation.Summarize("{\"success\":false,\"error\":\"compile failed\"}", false, "执行代码");
    Check(!lowercase.Success && lowercase.Detail == "compile failed", "desktop lowercase errors are surfaced");
    var exitFailure = AIBridgeRuntimePresentation.Summarize("{\"Success\":true}", false, "CLI");
    Check(!exitFailure.Success, "a failing process cannot be overridden by success JSON");
    var logs = AIBridgeRuntimePresentation.Summarize("{\"Success\":true,\"Data\":{\"logs\":[{\"type\":\"Exception\",\"message\":\"boom\",\"stackTrace\":\"Frame1\\nFrame2\"}]}}", true, "读取日志");
    Check(logs.Logs.Length == 1 && logs.Logs[0].Type == "Exception" && logs.Logs[0].Message == "boom" && logs.Logs[0].StackTrace == "Frame1\nFrame2", "logs retain level, message and full stack trace");
    Check(!AIBridgeRuntimePresentation.Summarize("{broken", true, "执行代码").Success, "malformed JSON does not look successful");
    Check(!AIBridgeRuntimePresentation.Summarize("{\"Success\":tru}", true, "执行代码").Success, "truncated literals cannot crash the window");
    Check(AIBridgeRuntimePresentation.Summarize("已应用场景配置", true, "场景配置").Detail == "已应用场景配置", "local plain-text feedback remains visible");
    var dedup = AIBridgeRuntimePresentation.Summarize("{\"Success\":true,\"Data\":{\"deduplicated\":true,\"patch\":{\"applied\":true,\"patchId\":\"p1\"}}}", true, "应用补丁");
    Check(dedup.Title.Contains("无需重复") && dedup.Value == "p1", "duplicate patches receive a distinct outcome summary");
}
finally { CultureInfo.CurrentCulture = culture; }

var directory = Path.Combine(Path.GetTempPath(), "runtime-presentation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    const string id = "ebdc26822c684233b1f33a396a6e2062";
    string Write(string name, string text)
    {
        var path = Path.Combine(directory, name + ".json");
        File.WriteAllText(path, text);
        return path;
    }
    var correct = Write("misleading-folder-name", "{\"buildId\":\"" + id + "\",\"target\":\"Android\",\"platform\":\"Android\"}");
    Check(AIBridgeRuntimePresentation.ProfileMatches(correct, "EBDC2682-2C68-4233-B1F3-3A396A6E2062", "android"), "profile GUID matching ignores case and standard separators");
    Check(!AIBridgeRuntimePresentation.ProfileMatches(correct, Guid.NewGuid().ToString(), "Android"), "a different Player build is rejected");
    Check(!AIBridgeRuntimePresentation.ProfileMatches(correct, id, "WindowsPlayer"), "a different target platform is rejected");
    var conflicting = Write("conflicting", "{\"buildId\":\"" + id + "\",\"target\":\"Android\",\"platform\":\"WindowsPlayer\"}");
    Check(!AIBridgeRuntimePresentation.ProfileMatches(conflicting, id, "Android"), "conflicting declared platform and target are rejected");
    var editor = Write("editor", "{\"buildId\":\"" + id + "\",\"target\":\"WindowsEditor\"}");
    Check(!AIBridgeRuntimePresentation.ProfileMatches(editor, id, "WindowsEditor"), "Editor profiles are excluded even when both strings agree");
    var windows = Write("windows", "{\"buildId\":\"" + id + "\",\"target\":\"StandaloneWindows64\"}");
    Check(AIBridgeRuntimePresentation.ProfileMatches(windows, id, "WindowsPlayer"), "Unity Windows build and runtime platform aliases match");
    var ios = Write("ios", "{\"buildId\":\"" + id + "\",\"platform\":\"iOS\"}");
    Check(AIBridgeRuntimePresentation.ProfileMatches(ios, id, "IPhonePlayer"), "Unity iOS build and runtime platform aliases match");
    var missing = Write("missing", "{\"buildId\":\"" + id + "\"}");
    Check(!AIBridgeRuntimePresentation.ProfileMatches(missing, id, "Android"), "missing platform metadata is rejected");
    var zero = Write("zero", "{\"buildId\":\"00000000000000000000000000000000\",\"target\":\"Android\"}");
    Check(!AIBridgeRuntimePresentation.ProfileMatches(zero, "00000000000000000000000000000000", "Android"), "empty Player GUIDs never match");
    Check(AIBridgeRuntimePresentation.FindMatchingProfile(new[] { windows, missing, correct }, id, "Android") == correct, "automatic selection checks file contents instead of selecting the newest unrelated profile");
    Check(AIBridgeRuntimePresentation.FindMatchingProfile(new[] { missing, editor }, id, "Android") == null, "no match leaves profile selection empty");
    Check(!AIBridgeRuntimePresentation.ProfileMatches(Path.Combine(directory, "absent.json"), id, "Android"), "missing files are handled without window exceptions");
    var broken = Write("broken", "{\"buildId\":tru}");
    Check(!AIBridgeRuntimePresentation.ProfileMatches(broken, id, "Android"), "damaged profile JSON is safely rejected");
    Console.WriteLine("Passed " + passed + " presentation checks; no Unity Editor or Player was launched.");
}
finally
{
    var full = Path.GetFullPath(directory);
    if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
        || !Path.GetFileName(full).StartsWith("runtime-presentation-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unexpected temporary cleanup path");
    Directory.Delete(full, true);
}
