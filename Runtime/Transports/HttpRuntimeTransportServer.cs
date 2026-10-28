using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AIBridge.Internal.Json;
using UnityEngine;

namespace AIBridge.Runtime.Transports
{
    internal sealed class HttpRuntimeTransportServer : IDisposable
    {
        private const int DefaultPort = 27182;
        private const int MaxPort = 65535;
        private const int PortRetryCount = 50;
        private const int DefaultCommandTimeoutMs = 30000;
        private const int MinCommandTimeoutMs = 100;
        private const int MaxCommandTimeoutMs = 300000;
        private const int MaxRequestBytes = 12 * 1024 * 1024;
        private const int MaxHeaderBytes = 64 * 1024;
        private const int MaxConcurrentClients = 8;
        private const string HealthPath = "/aibridge/health";
        private const string CommandsPath = "/aibridge/commands";
        private const string ResultsPathPrefix = "/aibridge/results/";
        private const string ArtifactsPathPrefix = "/aibridge/artifacts/";

        private readonly AIBridgeRuntime _runtime;
        private readonly AIBridgeRuntimeSettings _settings;
        private readonly SemaphoreSlim _clientSlots = new SemaphoreSlim(MaxConcurrentClients, MaxConcurrentClients);
        private TcpListener _listener;
        private Thread _listenThread;
        private volatile bool _running;

        public HttpRuntimeTransportServer(AIBridgeRuntime runtime, AIBridgeRuntimeSettings settings)
        {
            _runtime = runtime;
            _settings = settings;
        }

        public bool IsRunning => _running;

        public int Port { get; private set; }

        public string Url { get; private set; }

        public void Start()
        {
            if (_running)
            {
                return;
            }

            var bindAddress = ResolveBindAddress();
            var requestedPort = ResolvePort();
            BindListener(bindAddress, requestedPort);
            _running = true;
            Url = "http://" + ResolveDisplayHost(bindAddress) + ":" + Port.ToString(CultureInfo.InvariantCulture);

            _listenThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "AIBridgeRuntimeHttpTransport"
            };
            _listenThread.Start();
            Debug.Log("[AIBridgeRuntime] HTTP transport listening: " + Url);
        }

        private void BindListener(IPAddress bindAddress, int requestedPort)
        {
            requestedPort = Math.Max(1, Math.Min(MaxPort, requestedPort));
            Exception lastError = null;
            var maxCandidate = Math.Min(MaxPort, requestedPort + PortRetryCount - 1);
            // 直接尝试绑定并在端口占用时递增，避免先检测端口再绑定产生竞态。
            for (var port = requestedPort; port <= maxCandidate; port++)
            {
                TcpListener listener = null;
                try
                {
                    listener = new TcpListener(bindAddress, port);
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    if (port != requestedPort)
                    {
                        Debug.LogWarning("[AIBridgeRuntime] HTTP port " + requestedPort.ToString(CultureInfo.InvariantCulture)
                            + " is unavailable; using " + port.ToString(CultureInfo.InvariantCulture) + ".");
                    }

                    return;
                }
                catch (SocketException ex)
                {
                    lastError = ex;
                    if (listener != null)
                    {
                        try { listener.Stop(); } catch { }
                    }

                    if (!IsAddressAlreadyInUse(ex))
                    {
                        throw;
                    }
                }
                catch
                {
                    if (listener != null)
                    {
                        try { listener.Stop(); } catch { }
                    }

                    throw;
                }
            }

            throw new InvalidOperationException(
                "No available AIBridge Runtime HTTP port from "
                + requestedPort.ToString(CultureInfo.InvariantCulture)
                + " to "
                + maxCandidate.ToString(CultureInfo.InvariantCulture)
                + ".",
                lastError);
        }

        public void Dispose()
        {
            _running = false;

            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                }
            }
            catch
            {
            }

            _listener = null;
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    if (!_clientSlots.Wait(0))
                    {
                        RejectBusyClient(client);
                        continue;
                    }

                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            HandleClient(client);
                        }
                        finally
                        {
                            _clientSlots.Release();
                        }
                    });
                }
                catch
                {
                    if (_running)
                    {
                        Thread.Sleep(50);
                    }
                }
            }
        }

        private static void RejectBusyClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.SendTimeout = 1000;
                    WriteJson(client.GetStream(), 503, new Dictionary<string, object>
                    {
                        ["success"] = false,
                        ["error"] = "too_many_connections"
                    });
                }
                catch
                {
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    var request = ReadRequest(client.GetStream(), out var errorStatusCode, out var error);
                    if (request == null)
                    {
                        if (errorStatusCode > 0)
                        {
                            WriteJson(client.GetStream(), errorStatusCode, new Dictionary<string, object>
                            {
                                ["success"] = false,
                                ["error"] = error
                            });
                        }

                        return;
                    }

                    HandleRequest(client.GetStream(), request);
                }
                catch (Exception ex)
                {
                    try
                    {
                        WriteJson(client.GetStream(), 500, new Dictionary<string, object>
                        {
                            ["success"] = false,
                            ["error"] = ex.GetType().Name + ": " + ex.Message
                        });
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void HandleRequest(NetworkStream stream, HttpRequestData request)
        {
            if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Path, HealthPath, StringComparison.OrdinalIgnoreCase))
            {
                var health = _runtime.BuildHttpHealthData();
                WriteJson(stream, IsHealthReady(health) ? 200 : 503, health);
                return;
            }

            if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Path, CommandsPath, StringComparison.OrdinalIgnoreCase))
            {
                HandleCommand(stream, request);
                return;
            }

            if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                && request.Path.StartsWith(ResultsPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!ValidateAuthorization(request, stream))
                {
                    return;
                }

                var commandId = Uri.UnescapeDataString(request.Path.Substring(ResultsPathPrefix.Length));
                if (_runtime.TryGetHttpResult(commandId, true, out var result))
                {
                    WriteJson(stream, 200, result);
                    return;
                }

                var commandState = _runtime.GetHttpCommandState(commandId);
                if (commandState == AIBridgeHttpCommandState.Queued
                    || commandState == AIBridgeHttpCommandState.Executing)
                {
                    WriteJson(stream, 202, BuildPendingCommandResponse(commandId, commandState));
                    return;
                }

                WriteJson(stream, 404, new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "result_not_found"
                });
                return;
            }

            if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                && request.Path.StartsWith(ArtifactsPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!ValidateAuthorization(request, stream))
                {
                    return;
                }

                var filename = Uri.UnescapeDataString(request.Path.Substring(ArtifactsPathPrefix.Length));
                if (_runtime.TryReadHttpScreenshotArtifact(filename, out var bytes))
                {
                    WriteBinary(stream, 200, "image/png", bytes);
                    return;
                }

                WriteJson(stream, 404, new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "artifact_not_found"
                });
                return;
            }

            WriteJson(stream, 404, new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "not_found"
            });
        }

        private void HandleCommand(NetworkStream stream, HttpRequestData request)
        {
            if (!ValidateAuthorization(request, stream))
            {
                return;
            }

            Dictionary<string, object> data;
            try
            {
                data = AIBridgeJson.DeserializeObject(request.Body);
            }
            catch (Exception ex)
            {
                WriteJson(stream, 400, AIBridgeRuntimeCommandResult.FromFailure("http", "invalid_json: " + ex.Message));
                return;
            }

            var command = AIBridgeRuntimeCommand.FromDictionary(data);
            if (command == null)
            {
                WriteJson(stream, 400, AIBridgeRuntimeCommandResult.FromFailure("http", "invalid_command"));
                return;
            }

            if (string.IsNullOrEmpty(command.Id))
            {
                command.Id = "http_" + Guid.NewGuid().ToString("N");
            }

            if (!AIBridgeRuntime.IsValidCommandId(command.Id))
            {
                WriteJson(stream, 400, AIBridgeRuntimeCommandResult.FromFailure("http", "invalid_command_id"));
                return;
            }

            var bearerToken = ReadBearerToken(request);
            if (string.IsNullOrEmpty(command.Token))
            {
                command.Token = bearerToken;
            }

            string notReadyReason;
            long mainThreadAgeMs;
            if (!TryValidateRuntimeReady(out notReadyReason, out mainThreadAgeMs))
            {
                WriteJson(stream, 503, BuildRuntimeNotReadyResult(command.Id, notReadyReason, mainThreadAgeMs));
                return;
            }

            string enqueueError;
            if (!_runtime.EnqueueHttpCommand(command, out enqueueError))
            {
                WriteJson(stream, 503, BuildRuntimeNotReadyResult(command.Id, enqueueError, mainThreadAgeMs));
                return;
            }

            var timeoutMs = ResolveCommandTimeout(request);
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (_runtime.TryGetHttpResult(command.Id, true, out var result))
                {
                    WriteJson(stream, 200, result);
                    return;
                }

                if (!TryValidateRuntimeReady(out notReadyReason, out mainThreadAgeMs))
                {
                    var state = _runtime.CancelQueuedHttpCommand(command.Id);
                    if (state == AIBridgeHttpCommandState.Completed
                        && _runtime.TryGetHttpResult(command.Id, true, out result))
                    {
                        WriteJson(stream, 200, result);
                    }
                    else if (state == AIBridgeHttpCommandState.Executing)
                    {
                        WriteJson(stream, 202, BuildPendingCommandResponse(command.Id, state));
                    }
                    else
                    {
                        WriteJson(stream, 503, BuildRuntimeNotReadyResult(command.Id, notReadyReason, mainThreadAgeMs));
                    }

                    return;
                }

                // 命令必须回到 Unity 主线程执行；HTTP 线程只短轮询等待结果，避免跨线程调用 Unity API。
                Thread.Sleep(20);
            }

            var timeoutState = _runtime.CancelQueuedHttpCommand(command.Id);
            if (timeoutState == AIBridgeHttpCommandState.Completed
                && _runtime.TryGetHttpResult(command.Id, true, out var completedResult))
            {
                WriteJson(stream, 200, completedResult);
            }
            else if (timeoutState == AIBridgeHttpCommandState.Executing)
            {
                WriteJson(stream, 202, BuildPendingCommandResponse(command.Id, timeoutState));
            }
            else
            {
                var timeoutResult = AIBridgeRuntimeCommandResult.FromFailure(command.Id, "handler_timeout");
                timeoutResult.Data = new Dictionary<string, object>
                {
                    ["cancelled"] = timeoutState == AIBridgeHttpCommandState.Cancelled,
                    ["status"] = timeoutState == AIBridgeHttpCommandState.Cancelled ? "cancelled" : "not_found"
                };
                WriteJson(stream, 504, timeoutResult);
            }
        }

        private static Dictionary<string, object> BuildPendingCommandResponse(
            string commandId,
            AIBridgeHttpCommandState state)
        {
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["commandId"] = commandId,
                ["completed"] = false,
                ["status"] = state == AIBridgeHttpCommandState.Executing ? "execution_started" : "queued",
                ["resultUrl"] = ResultsPathPrefix + Uri.EscapeDataString(commandId)
            };
        }

        private bool TryValidateRuntimeReady(out string reason, out long mainThreadAgeMs)
        {
            reason = null;
            mainThreadAgeMs = long.MaxValue;
            if (!_running)
            {
                reason = "http_transport_stopping";
                return false;
            }

            return _runtime.IsCommandPumpReady(out reason, out mainThreadAgeMs);
        }

        private static bool IsHealthReady(Dictionary<string, object> health)
        {
            if (health == null)
            {
                return false;
            }

            object value;
            return !health.TryGetValue("ready", out value) || !(value is bool) || (bool)value;
        }

        private static AIBridgeRuntimeCommandResult BuildRuntimeNotReadyResult(string commandId, string reason, long mainThreadAgeMs)
        {
            var result = AIBridgeRuntimeCommandResult.FromFailure(
                commandId,
                string.IsNullOrEmpty(reason) ? "runtime_not_ready" : "runtime_not_ready: " + reason);
            result.Data = new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["lastMainThreadTickAgeMs"] = mainThreadAgeMs == long.MaxValue ? (object)null : mainThreadAgeMs
            };
            return result;
        }

        private bool ValidateAuthorization(HttpRequestData request, NetworkStream stream)
        {
            var expectedToken = _settings == null ? null : _settings.authToken;
            if (string.IsNullOrEmpty(expectedToken))
            {
                return true;
            }

            var actualToken = ReadBearerToken(request);
            if (string.Equals(expectedToken, actualToken, StringComparison.Ordinal))
            {
                return true;
            }

            WriteJson(stream, 401, new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "auth_failed",
                ["code"] = "auth_failed"
            });
            return false;
        }

        private static string ReadBearerToken(HttpRequestData request)
        {
            if (request == null || request.Headers == null)
            {
                return null;
            }

            if (!request.Headers.TryGetValue("Authorization", out var value) || string.IsNullOrEmpty(value))
            {
                return null;
            }

            const string prefix = "Bearer ";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return value.Substring(prefix.Length).Trim();
        }

        private int ResolveCommandTimeout(HttpRequestData request)
        {
            if (request.Query.TryGetValue("timeoutMs", out var raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                if (parsed < MinCommandTimeoutMs)
                {
                    return MinCommandTimeoutMs;
                }

                if (parsed > MaxCommandTimeoutMs)
                {
                    return MaxCommandTimeoutMs;
                }

                return parsed;
            }

            return DefaultCommandTimeoutMs;
        }

        private IPAddress ResolveBindAddress()
        {
            var bind = _settings == null ? null : _settings.httpBindAddress;
            if (string.IsNullOrWhiteSpace(bind))
            {
                bind = "127.0.0.1";
            }

            if (bind == "*" || bind == "+" || bind == "0.0.0.0")
            {
                return IPAddress.Any;
            }

            if (IPAddress.TryParse(bind, out var address))
            {
                return address;
            }

            var addresses = Dns.GetHostAddresses(bind);
            if (addresses != null && addresses.Length > 0)
            {
                return addresses[0];
            }

            return IPAddress.Loopback;
        }

        private int ResolvePort()
        {
            var port = _settings == null ? 0 : _settings.httpPort;
            return port <= 0 ? DefaultPort : port;
        }

        private static string ResolveDisplayHost(IPAddress bindAddress)
        {
            if (bindAddress == null || bindAddress.Equals(IPAddress.Any))
            {
                return "127.0.0.1";
            }

            return bindAddress.ToString();
        }

        private static bool IsAddressAlreadyInUse(SocketException ex)
        {
            return ex != null && ex.SocketErrorCode == SocketError.AddressAlreadyInUse;
        }

        private static HttpRequestData ReadRequest(
            NetworkStream stream,
            out int errorStatusCode,
            out string error)
        {
            errorStatusCode = 0;
            error = null;
            var bytes = new List<byte>(4096);
            var buffer = new byte[4096];
            var headerEnd = -1;
            var headerSearchStart = 3;
            var contentLength = 0;

            while (true)
            {
                if (bytes.Count >= MaxRequestBytes)
                {
                    errorStatusCode = 413;
                    error = "request_too_large";
                    return null;
                }

                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, MaxRequestBytes - bytes.Count));
                if (read <= 0)
                {
                    break;
                }

                bytes.AddRange(new ArraySegment<byte>(buffer, 0, read));

                if (headerEnd < 0)
                {
                    headerEnd = FindHeaderEnd(bytes, headerSearchStart);
                    if (headerEnd >= 0)
                    {
                        if (headerEnd > MaxHeaderBytes)
                        {
                            errorStatusCode = 413;
                            error = "headers_too_large";
                            return null;
                        }

                        var headerBytes = bytes.ToArray();
                        var headerText = Encoding.ASCII.GetString(headerBytes, 0, headerEnd);
                        if (!TryParseContentLength(headerText, out contentLength))
                        {
                            errorStatusCode = 400;
                            error = "invalid_content_length";
                            return null;
                        }

                        if (contentLength > MaxRequestBytes - headerEnd - 4)
                        {
                            errorStatusCode = 413;
                            error = "request_too_large";
                            return null;
                        }
                    }
                    else
                    {
                        if (bytes.Count > MaxHeaderBytes)
                        {
                            errorStatusCode = 413;
                            error = "headers_too_large";
                            return null;
                        }

                        headerSearchStart = Math.Max(3, bytes.Count - 3);
                    }
                }

                if (headerEnd >= 0 && bytes.Count >= headerEnd + 4 + contentLength)
                {
                    break;
                }
            }

            if (headerEnd < 0)
            {
                errorStatusCode = 400;
                error = "invalid_http_request";
                return null;
            }

            var bodyStart = headerEnd + 4;
            if (bytes.Count < bodyStart + contentLength)
            {
                errorStatusCode = 400;
                error = "incomplete_request_body";
                return null;
            }

            var allBytes = bytes.ToArray();
            var headersText = Encoding.ASCII.GetString(allBytes, 0, headerEnd);
            var request = ParseHeaders(headersText);
            if (request == null)
            {
                errorStatusCode = 400;
                error = "invalid_http_request";
                return null;
            }

            request.Body = contentLength > 0
                ? Encoding.UTF8.GetString(allBytes, bodyStart, contentLength)
                : string.Empty;
            return request;
        }

        private static int FindHeaderEnd(List<byte> bytes, int startIndex)
        {
            for (var i = Math.Max(3, startIndex); i < bytes.Count; i++)
            {
                if (bytes[i - 3] == 13 && bytes[i - 2] == 10 && bytes[i - 1] == 13 && bytes[i] == 10)
                {
                    return i - 3;
                }
            }

            return -1;
        }

        private static bool TryParseContentLength(string headersText, out int contentLength)
        {
            contentLength = 0;
            var headers = headersText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            for (var i = 1; i < headers.Length; i++)
            {
                var line = headers[i];
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, colon).Trim();
                if (!string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line.Substring(colon + 1).Trim();
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength)
                    && contentLength >= 0;
            }

            return true;
        }

        private static HttpRequestData ParseHeaders(string headersText)
        {
            var lines = headersText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return null;
            }

            var firstLine = lines[0].Split(' ');
            var request = new HttpRequestData
            {
                Method = firstLine.Length > 0 ? firstLine[0] : string.Empty,
                RawPath = firstLine.Length > 1 ? firstLine[1] : "/",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            var queryStart = request.RawPath.IndexOf('?');
            request.Path = queryStart >= 0 ? request.RawPath.Substring(0, queryStart) : request.RawPath;
            if (queryStart >= 0 && queryStart + 1 < request.RawPath.Length)
            {
                ParseQuery(request.RawPath.Substring(queryStart + 1), request.Query);
            }

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                request.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            return request;
        }

        private static void ParseQuery(string query, Dictionary<string, string> values)
        {
            var parts = query.Split('&');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                var equals = part.IndexOf('=');
                if (equals < 0)
                {
                    values[Uri.UnescapeDataString(part)] = string.Empty;
                }
                else
                {
                    values[Uri.UnescapeDataString(part.Substring(0, equals))] = Uri.UnescapeDataString(part.Substring(equals + 1));
                }
            }
        }

        private static void WriteJson(NetworkStream stream, int statusCode, object body)
        {
            var json = AIBridgeJson.Serialize(body, pretty: false);
            var bodyBytes = Encoding.UTF8.GetBytes(json);
            var header = "HTTP/1.1 " + statusCode.ToString(CultureInfo.InvariantCulture) + " " + GetStatusText(statusCode) + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                + "Connection: close\r\n"
                + "\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }

        private static void WriteBinary(NetworkStream stream, int statusCode, string contentType, byte[] bodyBytes)
        {
            if (bodyBytes == null)
            {
                bodyBytes = new byte[0];
            }

            var header = "HTTP/1.1 " + statusCode.ToString(CultureInfo.InvariantCulture) + " " + GetStatusText(statusCode) + "\r\n"
                + "Content-Type: " + contentType + "\r\n"
                + "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                + "Connection: close\r\n"
                + "\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }

        private static string GetStatusText(int statusCode)
        {
            switch (statusCode)
            {
                case 200:
                    return "OK";
                case 202:
                    return "Accepted";
                case 400:
                    return "Bad Request";
                case 401:
                    return "Unauthorized";
                case 404:
                    return "Not Found";
                case 413:
                    return "Payload Too Large";
                case 504:
                    return "Gateway Timeout";
                case 503:
                    return "Service Unavailable";
                default:
                    return "Internal Server Error";
            }
        }

        private sealed class HttpRequestData
        {
            public string Method { get; set; }
            public string RawPath { get; set; }
            public string Path { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public Dictionary<string, string> Query { get; set; }
            public string Body { get; set; }
        }
    }
}
