using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIBridge.Runtime
{
    /// <summary>
    /// 从 Runtime 自有配置启动调试服务，不依赖 Editor 自动化包或构建宏同步。
    /// </summary>
    public static class AIBridgeRuntimeBootstrap
    {
        private static AIBridgeRuntimeConfig _configuration;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void PrepareBeforeGameCode()
        {
            _configuration = null;
#if DEVELOPMENT_BUILD && !UNITY_EDITOR
            _configuration = AIBridgeRuntimeConfig.Load();
            if (_configuration != null && _configuration.AutoStart
                && _configuration.Settings.enableRuntimeBridge && _configuration.Settings.enableRuntimeCodeExecution)
                AIBridgeHotfixHandler.PrepareRuntime();
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void SubscribeToFirstScene()
        {
            SceneManager.sceneLoaded -= HandleFirstSceneLoaded;
            if (_configuration != null && _configuration.AutoStart && _configuration.Settings.enableRuntimeBridge)
                SceneManager.sceneLoaded += HandleFirstSceneLoaded;
        }

        private static void HandleFirstSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            SceneManager.sceneLoaded -= HandleFirstSceneLoaded;
            Start(_configuration.Settings);
        }

        /// <summary>
        /// 项目也可显式启动服务；发布版 Player 不开放调试服务。
        /// </summary>
        public static AIBridgeRuntime Start(AIBridgeRuntimeSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (AIBridgeRuntime.Instance != null)
                return AIBridgeRuntime.Instance;
            if (!settings.enableRuntimeBridge)
                return null;
            var gameObject = new GameObject("AI Bridge Runtime");
            gameObject.SetActive(false);
            var runtime = gameObject.AddComponent<AIBridgeRuntime>();
            runtime.SettingsProvidedExplicitly = true;
            runtime.runtimeSettings = settings.Clone();
            gameObject.SetActive(true);
            return runtime;
#else
            return null;
#endif
        }
    }
}
