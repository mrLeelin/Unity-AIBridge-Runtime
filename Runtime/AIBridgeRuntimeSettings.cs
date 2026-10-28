using System;

namespace AIBridge.Runtime
{
    [Serializable]
    public class AIBridgeRuntimeSettings
    {
        public bool enableRuntimeBridge = true;
        public string exchangeDirectory;
        public string targetId;
        public string authToken;
        public string[] allowedActions = new string[0];
        public bool enableRuntimeCodeExecution = true;
        public float maxRuntimeCodeExecutionSeconds = 30f;
        public float heartbeatIntervalSeconds = 1f;
        public int logBufferSize = 500;
        public int maxResultBytes = 1048576;
        public float orphanResultRetentionSeconds = 60f;
        public bool keepRunningInBackground = true;
        public bool enableHttpTransport = true;
        public string httpBindAddress = "127.0.0.1";
        public int httpPort = 27182;
        public bool enableLanDiscovery = false;
        public int discoveryUdpPort = 27183;

        public AIBridgeRuntimeSettings Clone()
        {
            var copy = new AIBridgeRuntimeSettings();
            copy.CopyFrom(this);
            return copy;
        }

        public void CopyFrom(AIBridgeRuntimeSettings source)
        {
            if (source == null)
            {
                return;
            }

            enableRuntimeBridge = source.enableRuntimeBridge;
            exchangeDirectory = source.exchangeDirectory;
            targetId = source.targetId;
            authToken = source.authToken;
            allowedActions = source.allowedActions == null ? null : (string[])source.allowedActions.Clone();
            enableRuntimeCodeExecution = source.enableRuntimeCodeExecution;
            maxRuntimeCodeExecutionSeconds = source.maxRuntimeCodeExecutionSeconds;
            heartbeatIntervalSeconds = source.heartbeatIntervalSeconds;
            logBufferSize = source.logBufferSize;
            maxResultBytes = source.maxResultBytes;
            orphanResultRetentionSeconds = source.orphanResultRetentionSeconds;
            keepRunningInBackground = source.keepRunningInBackground;
            enableHttpTransport = source.enableHttpTransport;
            httpBindAddress = source.httpBindAddress;
            httpPort = source.httpPort;
            enableLanDiscovery = source.enableLanDiscovery;
            discoveryUdpPort = source.discoveryUdpPort;
        }

        public bool IsActionExplicitlyAllowed(string action)
        {
            if (string.IsNullOrEmpty(action) || allowedActions == null)
            {
                return false;
            }

            for (var i = 0; i < allowedActions.Length; i++)
            {
                if (string.Equals(allowedActions[i], action, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
