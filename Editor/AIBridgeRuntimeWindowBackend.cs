using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Runtime.Editor
{
    internal sealed class RuntimeWindowResult
    {
        public bool Success;
        public string Output;
        public string Error;
        public int ExitCode = -1;
    }

    /// <summary>Runtime 管理窗口的桌面 CLI 适配层；网络与编译不占用 Unity 主线程。</summary>
    internal static class AIBridgeRuntimeWindowBackend
    {
        internal static Task<RuntimeWindowResult> RunAsync(string pythonExecutable, string[] arguments,
            string url, string token, int timeoutSeconds = 30, string temporaryCode = null)
        {
            string tool;
            string projectRoot;
            try
            {
                tool = ResolveToolPath();
                projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            }
            catch (Exception exception)
            {
                return Task.FromResult(new RuntimeWindowResult { Error = RedactToken(exception.Message, token) });
            }
            var request = (arguments ?? new string[0]).ToArray();
            return Task.Run(() => Run(pythonExecutable, tool, projectRoot, request, url, token, timeoutSeconds, temporaryCode));
        }

        private static RuntimeWindowResult Run(string python, string tool, string projectRoot, string[] arguments,
            string url, string token, int timeoutSeconds, string temporaryCode)
        {
            string codePath = null;
            try
            {
                if (arguments.Length == 0)
                    throw new ArgumentException("A Runtime CLI command is required.");
                var commandArguments = new List<string>(arguments);
                if (temporaryCode != null)
                {
                    if (arguments[0] != "exec" || arguments.Any(value => value == "--code" || value == "--file" || value == "--dll"))
                        throw new ArgumentException("Temporary source can only be attached to an exec request without another input.");
                    var root = Path.Combine(projectRoot, ".runtimebridge");
                    Directory.CreateDirectory(root);
                    var ignore = Path.Combine(root, ".gitignore");
                    if (!File.Exists(ignore)) File.WriteAllText(ignore, "*\n!.gitignore\n");
                    var directory = Path.Combine(root, "editor", "sessions");
                    Directory.CreateDirectory(directory);
                    codePath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".csx");
                    File.WriteAllText(codePath, temporaryCode, new UTF8Encoding(false));
                    commandArguments.AddRange(new[] { "--file", codePath });
                }
                timeoutSeconds = Math.Max(1, Math.Min(300, timeoutSeconds));
                if (!IsLocalCommand(arguments[0]))
                    commandArguments.AddRange(new[] { "--url", url ?? "http://127.0.0.1:27182", "--timeout", timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                return RunProcess(python, tool, projectRoot, commandArguments, url, token, timeoutSeconds);
            }
            catch (Exception exception)
            {
                return new RuntimeWindowResult { Error = RedactToken(exception.Message, token) };
            }
            finally
            {
                if (codePath != null)
                {
                    try { File.Delete(codePath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static RuntimeWindowResult RunProcess(string python, string tool, string projectRoot,
            List<string> arguments, string url, string token, int timeoutSeconds)
        {
            var start = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(python) ? "python" : python.Trim(),
                Arguments = string.Join(" ", new[] { tool }.Concat(arguments).Select(QuoteArgument)),
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // Token 只通过子进程环境传递，不进入命令行、布局文件或日志。
            start.EnvironmentVariables["RUNTIMEBRIDGE_TOKEN"] = IsLocalCommand(arguments[0]) ? string.Empty : (token ?? string.Empty);
            start.EnvironmentVariables["RUNTIMEBRIDGE_URL"] = url ?? "http://127.0.0.1:27182";
            start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            using (var process = new Process { StartInfo = start })
            {
                if (!process.Start())
                    throw new InvalidOperationException("Unable to start the Runtime desktop CLI.");
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit((timeoutSeconds + 150) * 1000))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    return new RuntimeWindowResult { Error = "Desktop CLI timed out. A request may already have reached the Player; check status/history before retrying." };
                }
                if (!Task.WaitAll(new Task[] { stdout, stderr }, 5000))
                    return new RuntimeWindowResult { Error = "Desktop CLI output did not close after process exit." };
                var output = RedactToken(stdout.Result.Trim(), token);
                var error = RedactToken(stderr.Result.Trim(), token);
                return new RuntimeWindowResult
                {
                    Success = process.ExitCode == 0,
                    ExitCode = process.ExitCode,
                    Output = output,
                    Error = process.ExitCode == 0 ? string.Empty : (string.IsNullOrEmpty(error) ? output : error)
                };
            }
        }

        private static bool IsLocalCommand(string command)
        {
            return command == "devices" || command == "device" || command == "discover" || command == "inspect-dll";
        }

        internal static string ResolveToolPath()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AIBridgeRuntimeWindowBackend).Assembly);
            var root = package == null ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Packages", "com.sh.aibridge.runtime")) : package.resolvedPath;
            var path = Path.Combine(root, "Tools~", "runtimebridge.py");
            if (!File.Exists(path)) throw new FileNotFoundException("Runtime desktop CLI was not found in the installed package.", path);
            return path;
        }

        internal static string ResolveAdbPath()
        {
            var executable = Application.platform == RuntimePlatform.WindowsEditor ? "adb.exe" : "adb";
            var configured = EditorPrefs.GetString("AndroidSdkRoot", string.Empty);
            var candidates = new[]
            {
                Path.Combine(configured, "platform-tools", executable),
                Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines", "AndroidPlayer", "SDK", "platform-tools", executable)
            };
            return candidates.FirstOrDefault(File.Exists) ?? "adb";
        }

        internal static string[] FindProfiles()
        {
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".runtimebridge", "profiles"));
            return !Directory.Exists(directory) ? new string[0] : Directory.GetFiles(directory, "profile.json", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        }

        internal static string QuoteArgument(string argument)
        {
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in argument ?? string.Empty)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        internal static string RedactToken(string text, string token)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return text ?? string.Empty;
            var variants = new[] { token, EscapeJson(token), Uri.EscapeDataString(token) }.Distinct().OrderByDescending(value => value.Length);
            foreach (var value in variants) text = text.Replace(value, "<REDACTED>");
            return text;
        }

        private static string EscapeJson(string text)
        {
            var result = new StringBuilder();
            foreach (var character in text)
            {
                switch (character)
                {
                    case '"': result.Append("\\\""); break;
                    case '\\': result.Append("\\\\"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    case '\b': result.Append("\\b"); break;
                    case '\f': result.Append("\\f"); break;
                    default: result.Append(character < 32 ? "\\u" + ((int)character).ToString("x4") : character.ToString()); break;
                }
            }
            return result.ToString();
        }

        internal static string PrettyJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || (text.TrimStart()[0] != '{' && text.TrimStart()[0] != '[')) return text ?? string.Empty;
            var output = new StringBuilder();
            var indent = 0;
            var quoted = false;
            var escaped = false;
            foreach (var character in text)
            {
                if (quoted)
                {
                    output.Append(character);
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '"') quoted = false;
                    continue;
                }
                if (character == '"') { quoted = true; output.Append(character); continue; }
                if (char.IsWhiteSpace(character)) continue;
                if (character == '{' || character == '[')
                {
                    output.Append(character).AppendLine();
                    output.Append(' ', ++indent * 2);
                }
                else if (character == '}' || character == ']')
                {
                    indent = Math.Max(0, indent - 1);
                    output.AppendLine().Append(' ', indent * 2).Append(character);
                }
                else if (character == ',') output.Append(character).AppendLine().Append(' ', indent * 2);
                else if (character == ':') output.Append(": ");
                else output.Append(character);
            }
            return output.ToString();
        }
    }
}
