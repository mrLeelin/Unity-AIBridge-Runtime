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
#if UNITY_EDITOR
        public const string ConfigAssetPath = "Assets/AIBridgeRuntime/Configuration/AIBridgeRuntimeConfig.asset";
#endif

        /// <summary>Editor 读取项目配置；开发 Player 从构建注入的预加载资源读取配置。</summary>
        public static AIBridgeRuntimeConfig Load()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<AIBridgeRuntimeConfig>(ConfigAssetPath)
                ?? Resources.Load<AIBridgeRuntimeConfig>(ResourceName);
#else
            var configurations = Resources.FindObjectsOfTypeAll<AIBridgeRuntimeConfig>();
            if (configurations.Length > 1)
                throw new InvalidOperationException("Multiple AI Bridge Runtime configurations were preloaded.");
            return configurations.Length == 1 ? configurations[0] : null;
#endif
        }

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
