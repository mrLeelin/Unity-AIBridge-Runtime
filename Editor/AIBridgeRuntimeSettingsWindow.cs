using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Runtime.Editor
{
    /// <summary>独立 Runtime 插件的出包配置，不提供 Editor 自动化命令。</summary>
    public sealed class AIBridgeRuntimeSettingsWindow : EditorWindow
    {
        public const string ConfigAssetPath = "Assets/Resources/AIBridgeRuntimeConfig.asset";
        private Vector2 _scrollPosition;

        [MenuItem("Window/AI Bridge Runtime/Settings")]
        private static void OpenWindow()
        {
            GetWindow<AIBridgeRuntimeSettingsWindow>("Runtime Settings").Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Player Runtime Bridge", EditorStyles.boldLabel);
            if (GUILayout.Button("打开 Runtime 管理窗口", GUILayout.Width(180)))
                AIBridgeRuntimeWindow.OpenWindow();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("Configure once before building a Development Player. The phone Runtime and desktop CLI do not require a running Unity Editor.", MessageType.Info);
            var config = Resources.Load<AIBridgeRuntimeConfig>(AIBridgeRuntimeConfig.ResourceName);
            if (config == null)
            {
                if (GUILayout.Button("Create Runtime Configuration"))
                    CreateConfiguration();
                return;
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            var serialized = new SerializedObject(config);
            serialized.Update();
            EditorGUILayout.PropertyField(serialized.FindProperty("_autoStart"), new GUIContent("Auto Start in Development Player"));
            EditorGUILayout.PropertyField(serialized.FindProperty("_settings"), new GUIContent("Runtime Settings"), true);
            if (serialized.ApplyModifiedProperties())
                EditorUtility.SetDirty(config);
            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("Select Configuration Asset"))
                Selection.activeObject = config;
            if (GUILayout.Button("Generate New Session Token"))
            {
                Undo.RecordObject(config, "Generate Runtime Bridge Token");
                config.Settings.authToken = Guid.NewGuid().ToString("N");
                EditorUtility.SetDirty(config);
            }
            if (GUILayout.Button("Import Legacy Runtime Settings (Keep Auto Start Off)"))
                ImportLegacySettings(config);
            if (GUILayout.Button("Save Configuration"))
                AssetDatabase.SaveAssetIfDirty(config);
        }

        /// <summary>显式创建配置，不在包导入或域重载时自动启用服务。</summary>
        public static AIBridgeRuntimeConfig CreateConfiguration()
        {
            var existing = AssetDatabase.LoadAssetAtPath<AIBridgeRuntimeConfig>(ConfigAssetPath);
            if (existing != null)
                return existing;
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                AssetDatabase.CreateFolder("Assets", "Resources");
            var config = CreateInstance<AIBridgeRuntimeConfig>();
            AssetDatabase.CreateAsset(config, ConfigAssetPath);
            AssetDatabase.SaveAssetIfDirty(config);
            Selection.activeObject = config;
            return config;
        }

        private static void ImportLegacySettings(AIBridgeRuntimeConfig config)
        {
            var path = Path.Combine(Application.dataPath, "..", "ProjectSettings", "AIBridgeSettings.asset");
            if (!File.Exists(path))
            {
                EditorUtility.DisplayDialog("AI Bridge Runtime", "No legacy ProjectSettings/AIBridgeSettings.asset was found.", "OK");
                return;
            }

            var values = ReadLegacyRuntimeSettings(File.ReadAllLines(path));
            if (values.Count == 0)
                throw new InvalidOperationException("The legacy asset does not contain a runtimeBridge section.");
            Undo.RecordObject(config, "Import Legacy Runtime Settings");
            ApplyLegacyValues(config.Settings, values);
            // 迁移旧配置不自动对外监听，也不继承旧版默认的 0.0.0.0/LAN 广播设置。
            config.AutoStart = false;
            config.Settings.httpBindAddress = "127.0.0.1";
            config.Settings.enableLanDiscovery = false;
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssetIfDirty(config);
            Debug.Log("[AI Bridge Runtime] Imported legacy settings. Auto Start remains disabled; HTTP binds to loopback.");
        }

        private static Dictionary<string, string> ReadLegacyRuntimeSettings(string[] lines)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var inSection = false;
            foreach (var line in lines)
            {
                if (!inSection)
                {
                    inSection = line == "  runtimeBridge:";
                    continue;
                }
                if (!line.StartsWith("    ", StringComparison.Ordinal))
                    break;
                var separator = line.IndexOf(':');
                if (separator <= 4)
                    continue;
                values[line.Substring(4, separator - 4)] = UnquoteYamlScalar(line.Substring(separator + 1).Trim());
            }
            return values;
        }

        private static string UnquoteYamlScalar(string value)
        {
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
                return value.Substring(1, value.Length - 2).Replace("''", "'");
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                return value.Substring(1, value.Length - 2).Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
            return value;
        }

        private static void ApplyLegacyValues(AIBridgeRuntimeSettings settings, Dictionary<string, string> values)
        {
            foreach (var pair in values)
            {
                var value = pair.Value;
                switch (pair.Key)
                {
                    case "EnableRuntimeBridge": settings.enableRuntimeBridge = value == "1"; break;
                    case "TargetId": settings.targetId = value; break;
                    case "AuthToken": if (!string.IsNullOrEmpty(value)) settings.authToken = value; break;
                    case "AllowedActions": settings.allowedActions = value.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).ToArray(); break;
                    case "EnableRuntimeCodeExecution": settings.enableRuntimeCodeExecution = value == "1"; break;
                    case "MaxRuntimeCodeExecutionSeconds": settings.maxRuntimeCodeExecutionSeconds = float.Parse(value, CultureInfo.InvariantCulture); break;
                    case "KeepRunningInBackground": settings.keepRunningInBackground = value == "1"; break;
                    case "EnableHttpTransport": settings.enableHttpTransport = value == "1"; break;
                    case "HttpPort": settings.httpPort = int.Parse(value, CultureInfo.InvariantCulture); break;
                }
            }
        }
    }
}
