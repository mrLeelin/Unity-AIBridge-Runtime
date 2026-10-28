using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AIBridge.Internal.Json;

namespace AIBridge.Runtime.Editor
{
    /// <summary>CLI 响应的可读摘要；原始 JSON 由窗口另行保留。</summary>
    internal sealed class RuntimeResultSummary
    {
        public string Title = string.Empty;
        public string Value = string.Empty;
        public string Detail = string.Empty;
        public bool HasValue;
        public bool Success;
        public RuntimeLogEntry[] Logs = Array.Empty<RuntimeLogEntry>();
    }

    /// <summary>日志展示项，保留异常堆栈供窗口展开。</summary>
    internal sealed class RuntimeLogEntry
    {
        public string Type = string.Empty;
        public string Message = string.Empty;
        public string StackTrace = string.Empty;
    }

    /// <summary>独立于 IMGUI 和通信的结果格式化及 Player profile 匹配逻辑。</summary>
    internal static class AIBridgeRuntimePresentation
    {
        /// <summary>将 CLI 响应转为摘要，明确保留 null、false 和零等有效返回值。</summary>
        internal static RuntimeResultSummary Summarize(string raw, bool succeeded, string operation)
        {
            var result = new RuntimeResultSummary { Success = succeeded, Title = Label(operation) + (succeeded ? "完成" : "失败") };
            if (string.IsNullOrWhiteSpace(raw)) return result;
            if (!raw.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                result.Detail = raw;
                return result;
            }
            try
            {
                var envelope = AIBridgeJson.DeserializeObject(raw);
                if (envelope == null) throw new FormatException("响应不是 JSON 对象。");
                if (Get(envelope, "success") is bool responseSuccess && !responseSuccess)
                    result.Success = false;
                var data = Get(envelope, "data") as Dictionary<string, object>;
                if (!result.Success)
                {
                    result.Title = Label(operation) + "失败";
                    var error = Get(envelope, "error") ?? Get(data, "error") ?? Get(data, "exception");
                    result.Detail = error == null ? "操作未成功，请展开原始响应。" : Format(error);
                    return result;
                }
                return SummarizeData(result, envelope, data);
            }
            catch (Exception exception) when (exception is FormatException || exception is OverflowException || exception is IndexOutOfRangeException)
            {
                result.Success = false;
                result.Title = "响应格式异常";
                result.Detail = "无法解析结果，请展开原始响应。";
                return result;
            }
        }

        private static RuntimeResultSummary SummarizeData(RuntimeResultSummary result,
            Dictionary<string, object> envelope, Dictionary<string, object> data)
        {
            object value;
            if (TryGet(data, "returnValue", out value))
            {
                result.Title = "代码执行成功";
                SetValue(result, value);
                var elapsed = Get(data, "elapsedMs");
                result.Detail = elapsed == null ? "返回值来自当前 Runtime 目标。" : "耗时 " + Format(elapsed) + " ms";
            }
            else if (Get(data, "logs") is IList logs)
            {
                var entries = new List<RuntimeLogEntry>();
                foreach (var item in logs)
                {
                    var entry = item as Dictionary<string, object>;
                    if (entry == null) continue;
                    entries.Add(new RuntimeLogEntry { Type = Text(entry, "type"), Message = Text(entry, "message"), StackTrace = Text(entry, "stackTrace") });
                }
                result.Logs = entries.ToArray();
                result.Title = "日志读取完成";
                SetValue(result, entries.Count.ToString(CultureInfo.InvariantCulture) + " 条日志");
            }
            else if (Get(envelope, "devices") is IList devices)
                SetValue(result, devices.Count.ToString(CultureInfo.InvariantCulture) + " 台设备");
            else if (Get(data, "entries") is IList history)
                SetValue(result, history.Count.ToString(CultureInfo.InvariantCulture) + " 条补丁记录");
            else if (Get(data, "patch") is Dictionary<string, object> patch)
            {
                var applied = Get(patch, "applied") is bool flag && flag;
                result.Title = Get(data, "deduplicated") is bool duplicate && duplicate ? "补丁已应用，无需重复" : applied ? "方法补丁应用成功" : "补丁未应用";
                SetValue(result, Text(patch, "patchId"));
            }
            else if (Get(data, "available") is bool available)
            {
                result.Title = available ? "方法热修复可用" : "方法热修复不可用";
                result.Detail = Text(data, "reason");
            }
            else if (Get(data, "ready") is bool ready)
            {
                result.Title = ready ? "Runtime 已就绪" : "Runtime 尚未就绪";
                SetValue(result, Text(data, "productName"));
                result.Detail = Text(data, "platform");
            }
            return result;
        }

        /// <summary>按真实 profile 内容查找当前 Player 的构建引用，找不到时不选择其他版本。</summary>
        internal static string FindMatchingProfile(string[] paths, string buildId, string platform)
        {
            foreach (var path in paths ?? Array.Empty<string>())
                if (ProfileMatches(path, buildId, platform)) return path;
            return null;
        }

        /// <summary>检查 profile 的构建 GUID 和平台；引用 SHA256 仍由桌面 CLI 严格校验。</summary>
        internal static bool ProfileMatches(string path, string buildId, string platform)
        {
            var expectedId = NormalizeGuid(buildId);
            var expectedPlatform = NormalizePlatform(platform);
            if (expectedId == null || expectedPlatform == null || string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var profile = AIBridgeJson.DeserializeObject(File.ReadAllText(path));
                if (profile == null || NormalizeGuid(Text(profile, "buildId")) != expectedId) return false;
                var target = Text(profile, "target");
                var declaredPlatform = Text(profile, "platform");
                if (string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(declaredPlatform)) return false;
                if (!string.IsNullOrWhiteSpace(target) && NormalizePlatform(target) != expectedPlatform) return false;
                return string.IsNullOrWhiteSpace(declaredPlatform) || NormalizePlatform(declaredPlatform) == expectedPlatform;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException
                || exception is ArgumentException || exception is FormatException || exception is OverflowException || exception is IndexOutOfRangeException)
            {
                return false;
            }
        }

        private static string NormalizeGuid(string value)
        {
            Guid guid;
            return Guid.TryParse(value, out guid) && guid != Guid.Empty ? guid.ToString("N") : null;
        }

        private static string NormalizePlatform(string platform)
        {
            if (string.IsNullOrWhiteSpace(platform) || platform.IndexOf("Editor", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            switch (platform.Trim().ToLowerInvariant())
            {
                case "android": return "android";
                case "ios": case "iphoneplayer": return "ios";
                case "standalonewindows": case "standalonewindows64": case "windowsplayer": return "windows";
                case "standalonelinux64": case "linuxplayer": return "linux";
                case "standaloneosx": case "osxplayer": return "osx";
                case "webgl": case "webglplayer": return "webgl";
                default: return platform.Trim().ToLowerInvariant();
            }
        }

        private static string Label(string operation) => string.IsNullOrWhiteSpace(operation) ? "操作" : operation;

        private static void SetValue(RuntimeResultSummary result, object value)
        {
            result.HasValue = true;
            result.Value = Format(value);
        }

        private static string Format(object value)
        {
            if (value == null) return "null";
            if (value is string text) return text;
            if (value is bool flag) return flag ? "true" : "false";
            if (value is IFormattable number) return number.ToString(null, CultureInfo.InvariantCulture);
            return AIBridgeJson.Serialize(value, true);
        }

        private static string Text(Dictionary<string, object> dictionary, string key) => Get(dictionary, key) as string ?? string.Empty;

        private static object Get(Dictionary<string, object> dictionary, string key)
        {
            object value;
            return TryGet(dictionary, key, out value) ? value : null;
        }

        private static bool TryGet(Dictionary<string, object> dictionary, string key, out object value)
        {
            if (dictionary != null)
            {
                foreach (var pair in dictionary)
                {
                    if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                    value = pair.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }
    }
}
