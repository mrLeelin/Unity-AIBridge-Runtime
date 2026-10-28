using System;
using UnityEngine;

namespace AIBridge.Runtime
{
    /// <summary>
    /// 独立 Runtime Bridge 配置。仅在显式启用后自动启动，不依赖 Editor 自动化包。
    /// </summary>
    [CreateAssetMenu(fileName = ResourceName, menuName = "AI Bridge Runtime/Configuration")]
    public sealed class AIBridgeRuntimeConfig : ScriptableObject
    {
        public const string ResourceName = "AIBridgeRuntimeConfig";

        [SerializeField] private bool _autoStart;
        [SerializeField] private AIBridgeRuntimeSettings _settings = CreateDefaultSettings();

        /// <summary>是否在测试 Player 启动时自动启动 Runtime Bridge。</summary>
        public bool AutoStart
        {
            get { return _autoStart; }
            set { _autoStart = value; }
        }

        /// <summary>网络、鉴权及代码执行设置。</summary>
        public AIBridgeRuntimeSettings Settings
        {
            get { return _settings ?? (_settings = CreateDefaultSettings()); }
            set { _settings = value ?? CreateDefaultSettings(); }
        }

        private static AIBridgeRuntimeSettings CreateDefaultSettings()
        {
            return new AIBridgeRuntimeSettings
            {
                httpBindAddress = "127.0.0.1",
                httpPort = 27182,
                enableLanDiscovery = false,
                authToken = Guid.NewGuid().ToString("N")
            };
        }
    }
}
