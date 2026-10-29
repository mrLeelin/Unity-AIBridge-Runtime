using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AIBridge.Runtime.Shared;
using UnityEngine;

namespace AIBridge.Runtime
{
    /// <summary>在同一 Unity 帧采集画面、UI、日志和对象字段，供 AI 验证操作结果。</summary>
    public partial class AIBridgeRuntime
    {
        private IEnumerator CaptureObservation(AIBridgeRuntimeCommand command)
        {
            var includeScreenshot = ReadBoolParam(command, "includeScreenshot", true);
            if (includeScreenshot) yield return new WaitForEndOfFrame();

            AIBridgeRuntimeCommandResult result;
            try
            {
                var frame = Time.frameCount;
                var data = new Dictionary<string, object>
                {
                    ["serviceSessionId"] = _serviceSessionId,
                    ["capturedFrame"] = frame,
                    ["capturedAtUtc"] = DateTime.UtcNow.ToString("o"),
                    ["status"] = BuildStatusData(),
                    ["ui"] = ReadBoolParam(command, "includeUi", true)
                        ? AIBridgeUiAutomation.Snapshot(ReadIntParam(command, "maxResults", 100),
                            ReadBoolParam(command, "includeDisabled", false)) : null,
                    ["logs"] = ReadIntParam(command, "logCount", 20) == 0 ? new AIBridgeRuntimeLogEntry[0] : _logBuffer.GetEntries(Math.Max(0, Math.Min(200, ReadIntParam(command, "logCount", 20))),
                        ReadStringParam(command, "logType", "all"), null, true,
                        ReadNullableIntParam(command, "sinceFrame"), null),
                    ["objects"] = ObserveObjects(command),
                    ["screenshot"] = includeScreenshot ? CaptureScreenData() : null,
                    ["captureEndFrame"] = Time.frameCount
                };
                result = AIBridgeRuntimeCommandResult.FromSuccess(command.Id, data);
            }
            catch (Exception ex)
            {
                result = AIBridgeRuntimeCommandResult.FromFailure(command.Id, $"{ex.GetType().Name}: {ex.Message}");
            }
            WriteResult(result);
        }

        private object[] ObserveObjects(AIBridgeRuntimeCommand command)
        {
            if (!TryGetCommandParam(command, "objectTokens", out var raw)) return new object[0];
            if (!(raw is IList tokens) || tokens.Count > 16)
                throw new ArgumentException("objectTokens must be an array of at most 16 live handles.");
            if (!IsCustomActionAllowed("runtime.inspect"))
                throw new InvalidOperationException("runtime.inspect is not allowed.");
            var items = new object[tokens.Count];
            for (var i = 0; i < tokens.Count; i++)
            {
                if (!(tokens[i] is string token) || string.IsNullOrEmpty(token))
                    throw new ArgumentException("Each objectToken must be a non-empty string.");
                var inspected = _runtimeTools.HandleCommand(new AIBridgeRuntimeCommand
                {
                    Id = command.Id, Action = "runtime.inspect",
                    Params = new Dictionary<string, object> { ["objectToken"] = token }
                });
                items[i] = new { objectToken = token, success = inspected.Success, data = inspected.Data, error = inspected.Error };
            }
            return items;
        }

        private object CaptureScreenData()
        {
            Texture2D texture = null;
            try
            {
                Directory.CreateDirectory(_screenshotsPath);
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                if (texture == null) throw new InvalidOperationException("ScreenCapture returned no texture.");
                var capturedAtUtc = DateTime.UtcNow;
                var safeArea = Screen.safeArea;
                var filename = "runtime_screenshot_" + capturedAtUtc.ToString("yyyyMMdd_HHmmss_fff")
                    + "_" + Guid.NewGuid().ToString("N") + ".png";
                var imagePath = Path.Combine(_screenshotsPath, filename);
                var bytes = texture.EncodeToPNG();
                File.WriteAllBytes(imagePath, bytes);
                CleanupOldScreenshots();
                return new
                {
                    action = "runtime.screenshot", imagePath, filename,
                    width = texture.width, height = texture.height, fileSize = bytes.Length,
                    orientation = Screen.orientation.ToString(),
                    safeArea = new { x = safeArea.x, y = safeArea.y, width = safeArea.width, height = safeArea.height,
                        xMin = safeArea.xMin, yMin = safeArea.yMin, xMax = safeArea.xMax, yMax = safeArea.yMax },
                    screenDpi = Screen.dpi, capturedFrame = Time.frameCount,
                    capturedAtUtc = capturedAtUtc.ToString("o"), sha256 = ComputeSha256(bytes),
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
            finally
            {
                if (texture != null) Destroy(texture);
            }
        }
    }
}
