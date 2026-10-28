using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using AIBridge.Runtime;
using UnityEngine;
using UnityEngine.Profiling;
#if UNITY_2020_2_OR_NEWER
using Unity.Profiling;
#endif

namespace AIBridge.Runtime.Diagnostics
{
    [Serializable]
    public sealed class RuntimePerfRequest
    {
        public int durationMs;
        public int intervalMs;
        public double hitchThresholdMs;

        internal readonly List<string> Warnings = new List<string>();
    }

    [Serializable]
    public sealed class RuntimePerfResult
    {
        public string targetId;
        public int durationMs;
        public int intervalMs;
        public int sampleCount;
        public RuntimePerfFpsStats fps;
        public RuntimePerfFrameTimeStats frameTimeMs;
        public RuntimePerfMemoryStats memory;
        public RuntimePerfGcStats gc;
        public string recorderMode;
        public string[] warnings;
    }

    [Serializable]
    public sealed class RuntimePerfFpsStats
    {
        public double avg;
        public double min;
        public double max;
    }

    [Serializable]
    public sealed class RuntimePerfFrameTimeStats
    {
        public double avg;
        public double p95;
        public double p99;
        public double max;
        public int hitchCount;
        public double hitchThresholdMs;
    }

    [Serializable]
    public sealed class RuntimePerfMemoryStats
    {
        public long monoUsedBytes;
        public long gcUsedBytes;
        public long totalReservedBytes;
        public long systemUsedBytes;
    }

    [Serializable]
    public sealed class RuntimePerfGcStats
    {
        public int collectionCount0Delta;
        public long allocatedBytesDelta;
    }

    public sealed class RuntimePerfSampler : IDisposable
    {
        private const int DefaultDurationMs = 5000;
        private const int MaxDurationMs = 60000;
        private const int DefaultIntervalMs = 100;
        private const int MinIntervalMs = 16;
        private const double DefaultHitchThresholdMs = 50d;
        private const double MinHitchThresholdMs = 1d;
        private const double NanosecondsPerMillisecond = 1000000d;

        private readonly RuntimePerfRequest _request;
        private readonly List<string> _warnings = new List<string>();
        private readonly List<double> _fpsSamples = new List<double>();
        private readonly List<double> _frameTimeSamples = new List<double>();

        private int _startCollectionCount0;
        private int _startFrameCount;
        private long _startManagedMemory;
        private long _allocatedBytesDelta;
        private bool _disposed;
        private bool _usingProfilerRecorder;

#if UNITY_2020_2_OR_NEWER
        private ProfilerRecorder _mainThreadTimeRecorder;
        private ProfilerRecorder _gcAllocatedInFrameRecorder;
        private ProfilerRecorder _gcUsedMemoryRecorder;
        private ProfilerRecorder _totalReservedMemoryRecorder;
        private ProfilerRecorder _systemUsedMemoryRecorder;
#endif

        public RuntimePerfSampler(AIBridgeRuntimeCommand command)
        {
            _request = BuildRequest(command);
            for (var i = 0; i < _request.Warnings.Count; i++)
            {
                AddWarning(_request.Warnings[i]);
            }
        }

        public IEnumerator Sample(string targetId, Action<RuntimePerfResult> completed)
        {
            StartRecorders();
            _startCollectionCount0 = GC.CollectionCount(0);
            _startManagedMemory = GC.GetTotalMemory(false);
            _startFrameCount = Time.frameCount;

            RuntimePerfResult result = null;
            try
            {
                var startTime = Time.realtimeSinceStartup;
                var nextSampleTime = startTime;
                while ((Time.realtimeSinceStartup - startTime) * 1000d < _request.durationMs)
                {
                    if (Time.realtimeSinceStartup >= nextSampleTime || _frameTimeSamples.Count == 0)
                    {
                        AddSample();
                        nextSampleTime = Time.realtimeSinceStartup + (_request.intervalMs / 1000f);
                    }

                    yield return null;
                }

                if (_frameTimeSamples.Count == 0)
                {
                    AddSample();
                }

                if (Time.frameCount - _startFrameCount <= 1)
                {
                    AddWarning("Frame count barely advanced during sampling; Player may be paused or running in background.");
                }

                result = BuildResult(targetId);
            }
            finally
            {
                Dispose();
            }

            if (completed != null)
            {
                completed(result);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

#if UNITY_2020_2_OR_NEWER
            DisposeRecorder(ref _mainThreadTimeRecorder);
            DisposeRecorder(ref _gcAllocatedInFrameRecorder);
            DisposeRecorder(ref _gcUsedMemoryRecorder);
            DisposeRecorder(ref _totalReservedMemoryRecorder);
            DisposeRecorder(ref _systemUsedMemoryRecorder);
#endif
            _disposed = true;
        }

        private static RuntimePerfRequest BuildRequest(AIBridgeRuntimeCommand command)
        {
            var request = new RuntimePerfRequest
            {
                durationMs = ReadDurationMs(command),
                intervalMs = ReadIntervalMs(command),
                hitchThresholdMs = ReadHitchThresholdMs(command)
            };

            request.durationMs = ClampInt(request.durationMs, MinIntervalMs, MaxDurationMs, "duration", request.Warnings);
            request.intervalMs = ClampInt(request.intervalMs, MinIntervalMs, request.durationMs, "interval", request.Warnings);
            if (request.hitchThresholdMs < MinHitchThresholdMs)
            {
                request.Warnings.Add("hitchThresholdMs was below 1ms and was clamped to 1ms.");
                request.hitchThresholdMs = MinHitchThresholdMs;
            }

            return request;
        }

        private void AddSample()
        {
            var frameTimeMs = ReadFrameTimeMs();
            if (frameTimeMs <= 0d)
            {
                frameTimeMs = Math.Max(0d, Time.unscaledDeltaTime * 1000d);
            }

            if (frameTimeMs <= 0d)
            {
                return;
            }

            _frameTimeSamples.Add(frameTimeMs);
            _fpsSamples.Add(1000d / frameTimeMs);

#if UNITY_2020_2_OR_NEWER
            var allocatedInFrame = ReadRecorderLastValue(_gcAllocatedInFrameRecorder);
            if (allocatedInFrame > 0L)
            {
                _allocatedBytesDelta += allocatedInFrame;
            }
#endif
        }

        private double ReadFrameTimeMs()
        {
#if UNITY_2020_2_OR_NEWER
            var mainThreadTimeNs = ReadRecorderLastValue(_mainThreadTimeRecorder);
            if (mainThreadTimeNs > 0L)
            {
                return mainThreadTimeNs / NanosecondsPerMillisecond;
            }
#endif
            return Time.unscaledDeltaTime * 1000d;
        }

        private RuntimePerfResult BuildResult(string targetId)
        {
            _frameTimeSamples.Sort();
            var sortedFrameTimes = new List<double>(_frameTimeSamples);
            var sortedFps = new List<double>(_fpsSamples);
            sortedFps.Sort();

            var frameStats = new RuntimePerfFrameTimeStats
            {
                avg = Average(_frameTimeSamples),
                p95 = RuntimePercentile.Calculate(sortedFrameTimes, 95d),
                p99 = RuntimePercentile.Calculate(sortedFrameTimes, 99d),
                max = sortedFrameTimes.Count == 0 ? 0d : sortedFrameTimes[sortedFrameTimes.Count - 1],
                hitchCount = CountHitches(_frameTimeSamples, _request.hitchThresholdMs),
                hitchThresholdMs = _request.hitchThresholdMs
            };

            var fpsStats = new RuntimePerfFpsStats
            {
                avg = Average(_fpsSamples),
                min = sortedFps.Count == 0 ? 0d : sortedFps[0],
                max = sortedFps.Count == 0 ? 0d : sortedFps[sortedFps.Count - 1]
            };

            return new RuntimePerfResult
            {
                targetId = targetId,
                durationMs = _request.durationMs,
                intervalMs = _request.intervalMs,
                sampleCount = _frameTimeSamples.Count,
                fps = fpsStats,
                frameTimeMs = frameStats,
                memory = CaptureMemoryStats(),
                gc = CaptureGcStats(),
                recorderMode = _usingProfilerRecorder ? "profilerRecorder" : "basic",
                warnings = _warnings.ToArray()
            };
        }

        private RuntimePerfMemoryStats CaptureMemoryStats()
        {
            var stats = new RuntimePerfMemoryStats
            {
                monoUsedBytes = SafeProfilerValue(Profiler.GetMonoUsedSizeLong),
                gcUsedBytes = GC.GetTotalMemory(false),
                totalReservedBytes = SafeProfilerValue(Profiler.GetTotalReservedMemoryLong),
                systemUsedBytes = 0L
            };

#if UNITY_2020_2_OR_NEWER
            var recorderGcUsed = ReadRecorderLastValue(_gcUsedMemoryRecorder);
            if (recorderGcUsed > 0L)
            {
                stats.gcUsedBytes = recorderGcUsed;
            }

            var recorderTotalReserved = ReadRecorderLastValue(_totalReservedMemoryRecorder);
            if (recorderTotalReserved > 0L)
            {
                stats.totalReservedBytes = recorderTotalReserved;
            }

            var recorderSystemUsed = ReadRecorderLastValue(_systemUsedMemoryRecorder);
            if (recorderSystemUsed > 0L)
            {
                stats.systemUsedBytes = recorderSystemUsed;
            }
            else
            {
                AddWarning("System used memory recorder is unavailable; systemUsedBytes is 0.");
            }
#else
            AddWarning("System used memory recorder is unavailable; systemUsedBytes is 0.");
#endif

            return stats;
        }

        private RuntimePerfGcStats CaptureGcStats()
        {
            var collectionCount0Delta = Math.Max(0, GC.CollectionCount(0) - _startCollectionCount0);
            if (_allocatedBytesDelta <= 0L)
            {
                _allocatedBytesDelta = Math.Max(0L, GC.GetTotalMemory(false) - _startManagedMemory);
            }

            return new RuntimePerfGcStats
            {
                collectionCount0Delta = collectionCount0Delta,
                allocatedBytesDelta = _allocatedBytesDelta
            };
        }

        private void StartRecorders()
        {
#if UNITY_2020_2_OR_NEWER
            // ProfilerRecorder 计数器名称会随 Unity 版本变化，单项失败不影响整体采样。
            _usingProfilerRecorder = TryStartRecorder(ProfilerCategory.Internal, "Main Thread", out _mainThreadTimeRecorder);
            var gcAllocReady = TryStartRecorder(ProfilerCategory.Memory, "GC Allocated In Frame", out _gcAllocatedInFrameRecorder);
            var gcUsedReady = TryStartRecorder(ProfilerCategory.Memory, "GC Used Memory", out _gcUsedMemoryRecorder);
            var totalReservedReady = TryStartRecorder(ProfilerCategory.Memory, "Total Reserved Memory", out _totalReservedMemoryRecorder);
            var systemUsedReady = TryStartRecorder(ProfilerCategory.Memory, "System Used Memory", out _systemUsedMemoryRecorder);
            _usingProfilerRecorder = _usingProfilerRecorder || gcAllocReady || gcUsedReady || totalReservedReady || systemUsedReady;
            if (!_usingProfilerRecorder)
            {
                AddWarning("ProfilerRecorder counters are unavailable; used Time/Profiler basic fallback.");
            }
#else
            _usingProfilerRecorder = false;
            AddWarning("ProfilerRecorder is unavailable before Unity 2020.2; used Time/Profiler basic fallback.");
#endif
        }

#if UNITY_2020_2_OR_NEWER
        private bool TryStartRecorder(ProfilerCategory category, string counterName, out ProfilerRecorder recorder)
        {
            recorder = default(ProfilerRecorder);
            try
            {
                recorder = ProfilerRecorder.StartNew(category, counterName, 1);
                if (recorder.Valid)
                {
                    return true;
                }

                AddWarning("ProfilerRecorder counter is unavailable: " + counterName);
            }
            catch (Exception ex)
            {
                AddWarning("ProfilerRecorder counter failed: " + counterName + " (" + ex.GetType().Name + ")");
            }

            return false;
        }

        private static long ReadRecorderLastValue(ProfilerRecorder recorder)
        {
            return recorder.Valid ? recorder.LastValue : 0L;
        }

        private static void DisposeRecorder(ref ProfilerRecorder recorder)
        {
            if (recorder.Valid)
            {
                recorder.Dispose();
            }
        }
#endif

        private void AddWarning(string warning)
        {
            if (string.IsNullOrEmpty(warning) || _warnings.Contains(warning))
            {
                return;
            }

            _warnings.Add(warning);
        }

        private static int ReadDurationMs(AIBridgeRuntimeCommand command)
        {
            if (TryGetParameter(command, "durationMs", out var durationMs))
            {
                return ParseMilliseconds(durationMs, DefaultDurationMs);
            }

            if (TryGetParameter(command, "duration", out var duration))
            {
                return ParseMilliseconds(duration, DefaultDurationMs);
            }

            return DefaultDurationMs;
        }

        private static int ReadIntervalMs(AIBridgeRuntimeCommand command)
        {
            if (TryGetParameter(command, "intervalMs", out var intervalMs))
            {
                return ParseMilliseconds(intervalMs, DefaultIntervalMs);
            }

            if (TryGetParameter(command, "interval", out var interval))
            {
                return ParseMilliseconds(interval, DefaultIntervalMs);
            }

            return DefaultIntervalMs;
        }

        private static double ReadHitchThresholdMs(AIBridgeRuntimeCommand command)
        {
            if (!TryGetParameter(command, "hitchThresholdMs", out var value))
            {
                return DefaultHitchThresholdMs;
            }

            return ParseDouble(value, DefaultHitchThresholdMs);
        }

        private static bool TryGetParameter(AIBridgeRuntimeCommand command, string key, out object value)
        {
            value = null;
            if (command == null || command.Params == null || string.IsNullOrEmpty(key))
            {
                return false;
            }

            foreach (var pair in command.Params)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }

            return false;
        }

        private static int ParseMilliseconds(object value, int defaultValue)
        {
            if (value == null)
            {
                return defaultValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue)
            {
                return ClampToInt(longValue, defaultValue);
            }

            if (value is float floatValue)
            {
                return ClampToInt((long)Math.Round(floatValue), defaultValue);
            }

            if (value is double doubleValue)
            {
                return ClampToInt((long)Math.Round(doubleValue), defaultValue);
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text))
            {
                return defaultValue;
            }

            text = text.Trim();
            if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                return ClampToInt((long)Math.Round(ParseDouble(text.Substring(0, text.Length - 2), defaultValue)), defaultValue);
            }

            if (text.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                return ClampToInt((long)Math.Round(ParseDouble(text.Substring(0, text.Length - 1), defaultValue) * 1000d), defaultValue);
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericMilliseconds))
            {
                return ClampToInt((long)Math.Round(numericMilliseconds), defaultValue);
            }

            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var timeSpan))
            {
                return ClampToInt((long)Math.Round(timeSpan.TotalMilliseconds), defaultValue);
            }

            return defaultValue;
        }

        private static double ParseDouble(object value, double defaultValue)
        {
            if (value == null)
            {
                return defaultValue;
            }

            if (value is double doubleValue)
            {
                return doubleValue;
            }

            if (value is float floatValue)
            {
                return floatValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue)
            {
                return longValue;
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : defaultValue;
        }

        private static int ClampToInt(long value, int defaultValue)
        {
            if (value < int.MinValue || value > int.MaxValue)
            {
                return defaultValue;
            }

            return (int)value;
        }

        private static int ClampInt(int value, int min, int max, string name, List<string> warnings)
        {
            if (value < min)
            {
                warnings.Add(name + " was below " + min + "ms and was clamped.");
                return min;
            }

            if (value > max)
            {
                warnings.Add(name + " exceeded " + max + "ms and was clamped.");
                return max;
            }

            return value;
        }

        private static double Average(List<double> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0d;
            }

            var total = 0d;
            for (var i = 0; i < values.Count; i++)
            {
                total += values[i];
            }

            return total / values.Count;
        }

        private static int CountHitches(List<double> values, double thresholdMs)
        {
            var count = 0;
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] >= thresholdMs)
                {
                    count++;
                }
            }

            return count;
        }

        private static long SafeProfilerValue(Func<long> getter)
        {
            try
            {
                return getter == null ? 0L : getter();
            }
            catch
            {
                return 0L;
            }
        }
    }
}
