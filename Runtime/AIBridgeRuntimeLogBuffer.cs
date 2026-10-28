using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AIBridge.Runtime
{
    [Serializable]
    public class AIBridgeRuntimeLogEntry
    {
        public string type;
        public string message;
        public string stackTrace;
        public long timestamp;
        public int frame;
    }

    public sealed class AIBridgeRuntimeLogBuffer : IDisposable
    {
        private const int UnknownFrame = -1;
        private const int MaxRegexPatternLength = 256;
        private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(25);

        private readonly object _syncRoot = new object();
        private AIBridgeRuntimeLogEntry[] _entries = new AIBridgeRuntimeLogEntry[500];
        private int _capacity = 500;
        private int _start;
        private int _count;
        private bool _initialized;
        private int _mainThreadId;

        public int Count
        {
            get
            {
                lock (_syncRoot)
                {
                    return _count;
                }
            }
        }

        public void Initialize(int capacity)
        {
            if (_initialized)
            {
                return;
            }

            _capacity = Math.Max(1, capacity);
            _entries = new AIBridgeRuntimeLogEntry[_capacity];
            _start = 0;
            _count = 0;
            _mainThreadId = Environment.CurrentManagedThreadId;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
            _initialized = true;
        }

        public void Dispose()
        {
            if (!_initialized)
            {
                return;
            }

            Application.logMessageReceivedThreaded -= OnLogMessageReceived;
            _initialized = false;
        }

        public int Clear()
        {
            lock (_syncRoot)
            {
                var count = _count;
                Array.Clear(_entries, 0, _entries.Length);
                _start = 0;
                _count = 0;
                return count;
            }
        }

        public AIBridgeRuntimeLogEntry[] GetEntries(int count, string logType, string regexPattern, bool includeStackTrace)
        {
            return GetEntries(count, logType, regexPattern, includeStackTrace, null, null);
        }

        public AIBridgeRuntimeLogEntry[] GetEntries(
            int count,
            string logType,
            string regexPattern,
            bool includeStackTrace,
            int? sinceFrame,
            long? sinceTimestamp)
        {
            count = Math.Max(1, count);
            Regex regex = null;
            if (!string.IsNullOrEmpty(regexPattern))
            {
                if (regexPattern.Length > MaxRegexPatternLength)
                {
                    throw new ArgumentException("regex_pattern_too_long", nameof(regexPattern));
                }

                regex = new Regex(regexPattern, RegexOptions.None, RegexMatchTimeout);
            }

            AIBridgeRuntimeLogEntry[] snapshot;
            lock (_syncRoot)
            {
                snapshot = new AIBridgeRuntimeLogEntry[_count];
                for (var i = 0; i < _count; i++)
                {
                    snapshot[i] = _entries[(_start + i) % _capacity];
                }
            }

            var results = new List<AIBridgeRuntimeLogEntry>();
            try
            {
                for (var i = snapshot.Length - 1; i >= 0 && results.Count < count; i--)
                {
                    var entry = snapshot[i];
                    if (!MatchesLogType(logType, entry.type))
                    {
                        continue;
                    }

                    if (sinceFrame.HasValue && entry.frame != UnknownFrame && entry.frame < sinceFrame.Value)
                    {
                        continue;
                    }

                    if (sinceTimestamp.HasValue && entry.timestamp < sinceTimestamp.Value)
                    {
                        continue;
                    }

                    if (regex != null && !regex.IsMatch(entry.message ?? string.Empty))
                    {
                        continue;
                    }

                    results.Add(CloneEntry(entry, includeStackTrace));
                }
            }
            catch (RegexMatchTimeoutException ex)
            {
                throw new ArgumentException("regex_match_timeout", nameof(regexPattern), ex);
            }

            results.Reverse();
            return results.ToArray();
        }

        private void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            var entry = new AIBridgeRuntimeLogEntry
            {
                type = NormalizeLogType(type),
                message = Truncate(condition, 4096),
                stackTrace = Truncate(stackTrace, 8192),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                frame = GetFrameForCurrentThread()
            };

            lock (_syncRoot)
            {
                var index = (_start + _count) % _capacity;
                if (_count == _capacity)
                {
                    index = _start;
                    _start = (_start + 1) % _capacity;
                }
                else
                {
                    _count++;
                }

                _entries[index] = entry;
            }
        }

        private static AIBridgeRuntimeLogEntry CloneEntry(AIBridgeRuntimeLogEntry entry, bool includeStackTrace)
        {
            return new AIBridgeRuntimeLogEntry
            {
                type = entry.type,
                message = entry.message,
                stackTrace = includeStackTrace ? entry.stackTrace : null,
                timestamp = entry.timestamp,
                frame = entry.frame
            };
        }

        private int GetFrameForCurrentThread()
        {
            // logMessageReceivedThreaded may fire on worker threads; UnityEngine.Time is main-thread only.
            return Environment.CurrentManagedThreadId == _mainThreadId ? Time.frameCount : UnknownFrame;
        }

        private static bool MatchesLogType(string requestedType, string entryType)
        {
            if (string.IsNullOrEmpty(requestedType) || string.Equals(requestedType, "all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(requestedType, entryType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Error 查询默认覆盖 Unity 的 Exception/Assert，便于一次拿到真实失败日志。
            return string.Equals(requestedType, "Error", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(entryType, "Exception", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entryType, "Assert", StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeLogType(LogType type)
        {
            switch (type)
            {
                case LogType.Warning:
                    return "Warning";
                case LogType.Error:
                    return "Error";
                case LogType.Assert:
                    return "Assert";
                case LogType.Exception:
                    return "Exception";
                default:
                    return "Log";
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength) + "...";
        }
    }
}
