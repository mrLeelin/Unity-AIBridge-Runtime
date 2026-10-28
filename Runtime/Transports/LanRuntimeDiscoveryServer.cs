using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AIBridge.Internal.Json;
using UnityEngine;

namespace AIBridge.Runtime.Transports
{
    internal sealed class LanRuntimeDiscoveryServer : IDisposable
    {
        private const int DefaultDiscoveryPort = 27183;
        private const int MaxPort = 65535;
        private const int PortRetryCount = 50;
        private const string DiscoveryProtocol = "aibridge-runtime-discovery";

        private readonly AIBridgeRuntime _runtime;
        private readonly AIBridgeRuntimeSettings _settings;
        private readonly string _targetId;
        private readonly string _projectName;
        private readonly string _applicationVersion;
        private readonly string _platform;
        private readonly string _deviceName;
        private UdpClient _udpClient;
        private Thread _listenThread;
        private volatile bool _running;

        public LanRuntimeDiscoveryServer(AIBridgeRuntime runtime, AIBridgeRuntimeSettings settings)
        {
            _runtime = runtime;
            _settings = settings;
            _targetId = runtime == null ? null : runtime.TargetId;
            _projectName = Application.productName;
            _applicationVersion = Application.version;
            _platform = Application.platform.ToString();
            _deviceName = SystemInfo.deviceName;
        }

        public bool IsRunning => _running;
        public int Port { get; private set; }

        public void Start()
        {
            if (_running)
            {
                return;
            }

            var requestedPort = ResolveDiscoveryPort();
            BindUdpClient(requestedPort);
            _running = true;

            _listenThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "AIBridgeRuntimeLanDiscovery"
            };
            _listenThread.Start();
            Debug.Log("[AIBridgeRuntime] LAN discovery listening: udp://0.0.0.0:" + Port.ToString(CultureInfo.InvariantCulture));
        }

        private void BindUdpClient(int requestedPort)
        {
            requestedPort = Math.Max(1, Math.Min(MaxPort, requestedPort));
            Exception lastError = null;
            var maxCandidate = Math.Min(MaxPort, requestedPort + PortRetryCount - 1);
            // 多个 Editor/Player 同机运行时，UDP discovery 也需要和 HTTP 一样自动避让端口占用。
            for (var port = requestedPort; port <= maxCandidate; port++)
            {
                UdpClient client = null;
                try
                {
                    client = new UdpClient(port)
                    {
                        EnableBroadcast = true
                    };
                    _udpClient = client;
                    Port = port;
                    if (port != requestedPort)
                    {
                        Debug.LogWarning("[AIBridgeRuntime] LAN discovery UDP port "
                            + requestedPort.ToString(CultureInfo.InvariantCulture)
                            + " is unavailable; using "
                            + port.ToString(CultureInfo.InvariantCulture)
                            + ".");
                    }

                    return;
                }
                catch (SocketException ex)
                {
                    lastError = ex;
                    if (client != null)
                    {
                        try { client.Close(); } catch { }
                    }

                    if (!IsAddressAlreadyInUse(ex))
                    {
                        throw;
                    }
                }
                catch
                {
                    if (client != null)
                    {
                        try { client.Close(); } catch { }
                    }

                    throw;
                }
            }

            throw new InvalidOperationException(
                "No available AIBridge Runtime LAN discovery UDP port from "
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
                if (_udpClient != null)
                {
                    _udpClient.Close();
                }
            }
            catch
            {
            }

            _udpClient = null;
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    var bytes = _udpClient.Receive(ref remote);
                    string requestId;
                    string projectHint;
                    if (!TryParseDiscoveryRequest(bytes, out requestId, out projectHint) || !MatchesProjectHint(projectHint))
                    {
                        continue;
                    }

                    var response = BuildResponse(remote, requestId);
                    var responseBytes = Encoding.UTF8.GetBytes(AIBridgeJson.Serialize(response, pretty: false));
                    _udpClient.Send(responseBytes, responseBytes.Length, remote);
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

        private bool TryParseDiscoveryRequest(byte[] bytes, out string requestId, out string projectHint)
        {
            requestId = null;
            projectHint = null;
            if (bytes == null || bytes.Length == 0)
            {
                return false;
            }

            try
            {
                var payload = AIBridgeJson.DeserializeObject(Encoding.UTF8.GetString(bytes));
                if (!string.Equals(ReadString(payload, "protocol"), DiscoveryProtocol, StringComparison.Ordinal))
                {
                    return false;
                }

                requestId = ReadString(payload, "requestId");
                projectHint = ReadString(payload, "projectHint");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool MatchesProjectHint(string projectHint)
        {
            if (string.IsNullOrWhiteSpace(projectHint))
            {
                return true;
            }

            return string.Equals(projectHint.Trim(), _projectName, StringComparison.OrdinalIgnoreCase);
        }

        private Dictionary<string, object> BuildResponse(IPEndPoint remote, string requestId)
        {
            var localAddress = ResolveReachableAddress(remote);
            var httpPort = _runtime == null ? 0 : _runtime.GetActualHttpPort();
            if (httpPort <= 0)
            {
                httpPort = _settings == null || _settings.httpPort <= 0 ? 27182 : _settings.httpPort;
            }

            var reachableUrl = "http://" + localAddress + ":" + httpPort.ToString(CultureInfo.InvariantCulture);
            return new Dictionary<string, object>
            {
                ["protocol"] = DiscoveryProtocol,
                ["version"] = 1,
                ["requestId"] = requestId,
                ["targetId"] = _targetId,
                ["projectName"] = _projectName,
                ["applicationVersion"] = _applicationVersion,
                ["platform"] = _platform,
                ["deviceName"] = _deviceName,
                ["transport"] = "http",
                ["url"] = reachableUrl,
                ["reachableUrl"] = reachableUrl,
                ["bindUrl"] = BuildBindUrl(httpPort),
                ["requiresToken"] = _settings != null && !string.IsNullOrEmpty(_settings.authToken),
                ["capabilities"] = _runtime.BuildCapabilitiesData(),
                ["lastSeenUtc"] = DateTime.UtcNow.ToString("o")
            };
        }

        private string BuildBindUrl(int httpPort)
        {
            var host = _settings == null ? null : _settings.httpBindAddress;
            if (string.IsNullOrWhiteSpace(host) || host == "*" || host == "+" || host == "0.0.0.0")
            {
                host = "127.0.0.1";
            }

            return "http://" + host.Trim() + ":" + httpPort.ToString(CultureInfo.InvariantCulture);
        }

        private static string ResolveReachableAddress(IPEndPoint remote)
        {
            if (remote == null || remote.Address == null)
            {
                return GetFirstLanAddress();
            }

            Socket socket = null;
            try
            {
                socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(remote.Address, Math.Max(1, remote.Port));
                var local = socket.LocalEndPoint as IPEndPoint;
                if (local != null && local.Address != null && !IPAddress.IsLoopback(local.Address))
                {
                    return local.Address.ToString();
                }
            }
            catch
            {
            }
            finally
            {
                if (socket != null)
                {
                    try { socket.Close(); } catch { }
                }
            }

            return GetFirstLanAddress();
        }

        private static string GetFirstLanAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                for (var i = 0; i < host.AddressList.Length; i++)
                {
                    var address = host.AddressList[i];
                    if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                    {
                        return address.ToString();
                    }
                }
            }
            catch
            {
            }

            return "127.0.0.1";
        }

        private int ResolveDiscoveryPort()
        {
            var port = _settings == null ? 0 : _settings.discoveryUdpPort;
            return port <= 0 ? DefaultDiscoveryPort : port;
        }

        private static bool IsAddressAlreadyInUse(SocketException ex)
        {
            return ex != null
                && (ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    || ex.ErrorCode == 10048);
        }

        private static string ReadString(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return value.ToString();
        }
    }
}
