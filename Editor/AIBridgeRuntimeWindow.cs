using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIBridge.Runtime.Editor
{
    /// <summary>独立的真机 Runtime 管理窗口，通过桌面 CLI 连接 Player。</summary>
    public sealed class AIBridgeRuntimeWindow : EditorWindow
    {
        private static readonly string[] Tabs = { "设备连接", "执行代码", "方法热修复", "日志" };
        private static readonly string[] LogTypes = { "Error", "Warning", "Log", "All" };
        [SerializeField] private int _tab;
        [SerializeField] private string _url = "http://127.0.0.1:27182";
        [SerializeField] private string _python = "python";
        [SerializeField] private string _adb;
        [SerializeField] private string _serial = string.Empty;
        [SerializeField] private int _forwardPort = 27182;
        [SerializeField] private int _discoveryPort = 27183;
        [SerializeField] private int _timeout = 30;
        [SerializeField] private int _executionMode;
        [SerializeField] private string _profile;
        [SerializeField] private string _dll;
        [SerializeField] private string _entryType;
        [SerializeField] private string _methodName;
        [SerializeField] private string _patchDll;
        [SerializeField] private string _manifest;
        [SerializeField] private string _validationDll;
        [SerializeField] private int _logType;
        [SerializeField] private int _logCount = 100;
        [NonSerialized] private string _token = string.Empty;
        [NonSerialized] private string _code = "return 42;";
        [NonSerialized] private string _output = string.Empty;
        [NonSerialized] private string _error = string.Empty;
        [NonSerialized] private string _operation = string.Empty;
        private string[] _profiles = Array.Empty<string>();
        private DeviceInfo[] _devices = Array.Empty<DeviceInfo>();
        private TargetInfo[] _targets = Array.Empty<TargetInfo>();
        private StatusData _status;
        private PatchData _patch;
        private Task<RuntimeWindowResult> _pending;
        private Action<RuntimeWindowResult> _completed;
        private Vector2 _scroll;
        private Vector2 _outputScroll;
        private GUIStyle _outputStyle;

        private bool Busy => _pending != null;
        private bool Connected => _status != null && _status.ready;
        private bool CanExecute => Connected && _status.capabilities != null && _status.capabilities.runtimeCodeExecute;

        /// <summary>打开真机 Runtime 管理窗口，兼容拆分前的菜单入口。</summary>
        [MenuItem("Window/AI Bridge Runtime/Runtime", false, 0)]
        [MenuItem("Window/AIBridge/Runtime", false, 0)]
        public static void OpenWindow()
        {
            var window = GetWindow<AIBridgeRuntimeWindow>();
            window.titleContent = new GUIContent("Runtime Console");
            window.minSize = new Vector2(680, 620);
            window.Show();
        }

        [MenuItem("Window/AIBridge/Players", false, 1)]
        private static void OpenDevices()
        {
            OpenWindow();
            GetWindow<AIBridgeRuntimeWindow>()._tab = 0;
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Runtime Console");
            minSize = new Vector2(680, 620);
            if (string.IsNullOrEmpty(_adb))
                _adb = AIBridgeRuntimeWindowBackend.ResolveAdbPath();
            LoadConnectionConfiguration();
            RefreshProfiles();
            EditorApplication.update -= PollOperation;
            EditorApplication.update += PollOperation;
            EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
            EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
        }

        private void OnDisable()
        {
            EditorApplication.update -= PollOperation;
            EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
            // 关闭窗口只停止显示结果，不能撤销已经发送到手机的执行或补丁。
            _pending = null;
            _completed = null;
            _token = string.Empty;
        }

        private void HandlePlayModeStateChanged(PlayModeStateChange state)
        {
            if ((state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
                && _status != null && _status.isEditor)
            {
                _status = null;
                _patch = null;
                Repaint();
            }
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawConnection();
            _tab = GUILayout.Toolbar(Mathf.Clamp(_tab, 0, Tabs.Length - 1), Tabs);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            using (new EditorGUI.DisabledScope(Busy))
            {
                switch (_tab)
                {
                    case 0: DrawDevices(); break;
                    case 1: DrawExecution(); break;
                    case 2: DrawPatch(); break;
                    case 3: DrawLogs(); break;
                }
            }
            DrawOutput();
            EditorGUILayout.EndScrollView();
            if (Busy)
                EditorGUILayout.HelpBox(_operation + "… 关闭窗口不会撤销已发送的操作。", MessageType.Info);
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("AI Bridge Runtime", EditorStyles.boldLabel);
            if (GUILayout.Button("出包设置", GUILayout.Width(100)))
                GetWindow<AIBridgeRuntimeSettingsWindow>("Runtime Settings").Show();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("连接手机或独立 Player，执行代码并管理方法补丁。", EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawConnection()
        {
            using (new EditorGUI.DisabledScope(Busy))
            {
                EditorGUI.BeginChangeCheck();
                _url = EditorGUILayout.TextField("HTTP 地址", _url);
                _token = EditorGUILayout.PasswordField("会话 Token", _token ?? string.Empty);
                if (EditorGUI.EndChangeCheck())
                {
                    _status = null;
                    _patch = null;
                }
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("使用项目 Runtime 配置"))
                    LoadConnectionConfiguration();
                if (GUILayout.Button("连接 / 刷新状态"))
                    QueryStatus();
                EditorGUILayout.EndHorizontal();
            }
            if (_status == null)
                EditorGUILayout.LabelField("尚未连接；手机需先运行已启用 Runtime Bridge 的测试包。", EditorStyles.wordWrappedMiniLabel);
            else
            {
                EditorGUILayout.HelpBox((_status.ready ? "已就绪" : "未就绪") + " · " + _status.productName + " · " + _status.platform + " · " + _status.applicationVersion, _status.ready ? MessageType.Info : MessageType.Warning);
                EditorGUILayout.LabelField("目标", _status.targetId ?? string.Empty);
                EditorGUILayout.LabelField("Build ID", _status.buildId ?? string.Empty);
                EditorGUILayout.LabelField("代码执行", CanExecute ? "可用" : "不可用");
            }
            EditorGUILayout.Space(5);
        }

        private void DrawDevices()
        {
            EditorGUILayout.LabelField("Android USB", EditorStyles.boldLabel);
            _python = EditorGUILayout.TextField("Python", _python);
            _adb = EditorGUILayout.TextField("ADB", _adb);
            _forwardPort = Mathf.Clamp(EditorGUILayout.IntField("转发端口", _forwardPort), 1, 65535);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("刷新 ADB 设备"))
                RefreshDevices();
            if (GUILayout.Button("建立 USB 转发并连接"))
                ForwardDevice();
            EditorGUILayout.EndHorizontal();
            if (_devices.Length > 0)
            {
                var options = new[] { "默认设备" }.Concat(_devices.Select(d => d.serial + " [" + d.state + "] " + d.details)).ToArray();
                var index = Array.FindIndex(_devices, d => d.serial == _serial) + 1;
                var selected = EditorGUILayout.Popup("设备", Mathf.Max(0, index), options);
                if (selected != index)
                    _serial = selected == 0 ? string.Empty : _devices[selected - 1].serial;
            }
            _serial = EditorGUILayout.TextField("序列号（可选）", _serial);
            EditorGUILayout.HelpBox("USB 转发只建立电脑到手机的连接，不安装或重启游戏。局域网连接可直接填写手机 HTTP 地址。", MessageType.None);
            DrawLanTargets();
            _timeout = Mathf.Clamp(EditorGUILayout.IntField("超时（秒）", _timeout), 1, 300);
            DrawSceneDebugging();
        }

        private void DrawLanTargets()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("局域网 Runtime", EditorStyles.boldLabel);
            _discoveryPort = Mathf.Clamp(EditorGUILayout.IntField("UDP 发现端口", _discoveryPort), 1, 65535);
            if (GUILayout.Button("扫描局域网 Runtime"))
                DiscoverTargets();
            foreach (var target in _targets)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField((target.projectName ?? target.targetId ?? "Runtime") + " · " + target.platform + " · " + target.deviceName, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(target.reachableUrl ?? string.Empty);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(target.reachableUrl)))
                {
                    if (GUILayout.Button("连接", GUILayout.Width(60)))
                    {
                        _url = target.reachableUrl;
                        QueryStatus();
                    }
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.LabelField("扫描仅在点击时进行；手机需显式启用 LAN 发现。", EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawSceneDebugging()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Editor Play Mode 协议调试", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
            {
                if (GUILayout.Button("在当前场景创建 / 应用 Runtime 配置"))
                    CreateOrApplySceneRuntime();
            }
            EditorGUILayout.LabelField("此按钮仅在点击时修改当前场景，并支持 Undo；进入 Play Mode 前使用。", EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawExecution()
        {
            EditorGUILayout.LabelField("手机执行代码", EditorStyles.boldLabel);
            _executionMode = GUILayout.Toolbar(_executionMode, new[] { "C# 片段", "已编译 DLL" });
            DrawProfile();
            if (_executionMode == 0)
            {
                EditorGUILayout.HelpBox("片段在电脑上编译，必须选择对应 Player 构建导出的 profile。Play Mode 协议测试可使用 DLL 模式。", MessageType.None);
                _code = EditorGUILayout.TextArea(_code ?? string.Empty, GUILayout.MinHeight(150));
            }
            else
            {
                _dll = FileField("调试 DLL", _dll, new[] { "DLL / Bytes", "dll,bytes", "All files", "*" });
                _entryType = EditorGUILayout.TextField("入口类型（可选）", _entryType);
                _methodName = EditorGUILayout.TextField("方法（可选）", _methodName);
            }
            var validInput = _executionMode == 0
                ? File.Exists(_profile) && !string.IsNullOrWhiteSpace(_code) && Connected && !_status.isEditor
                : File.Exists(_dll);
            using (new EditorGUI.DisabledScope(!CanExecute || !validInput))
            {
                if (GUILayout.Button("在当前目标执行", GUILayout.Height(28)))
                    ExecuteCode();
            }
            if (!CanExecute)
                EditorGUILayout.LabelField("先连接就绪且已启用代码执行的 Runtime 目标。", EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawProfile()
        {
            _profile = FileField("Player profile", _profile, new[] { "Reference profile", "json", "All files", "*" });
            EditorGUILayout.BeginHorizontal();
            var options = new[] { "选择已有构建配置…" }.Concat(_profiles.Select(p => new DirectoryInfo(Path.GetDirectoryName(p) ?? string.Empty).Name)).ToArray();
            var selected = EditorGUILayout.Popup(0, options);
            if (selected > 0)
                _profile = _profiles[selected - 1];
            if (GUILayout.Button("刷新", GUILayout.Width(60)))
                RefreshProfiles();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawPatch()
        {
            EditorGUILayout.LabelField("HybridCLR 方法热修复", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!Connected))
            {
                if (GUILayout.Button("检查热修复能力"))
                    QueryPatchCapabilities();
                if (GUILayout.Button("查看补丁历史"))
                    BeginOperation("读取补丁历史", new[] { "patch-history" });
            }
            EditorGUILayout.EndHorizontal();
            if (_patch != null)
            {
                EditorGUILayout.HelpBox(_patch.available ? "方法热修复可用" : (_patch.reason ?? "方法热修复不可用"), _patch.available ? MessageType.Info : MessageType.Warning);
                EditorGUILayout.LabelField("上次检查：原生调用", _patch.attempts + " / " + _patch.limit);
            }
            _patchDll = FileField("补丁 DLL", _patchDll, new[] { "DLL / Bytes", "dll,bytes", "All files", "*" });
            _manifest = FileField("补丁 manifest", _manifest, new[] { "Patch manifest", "json", "All files", "*" });
            _validationDll = FileField("明文候选（加固时）", _validationDll, new[] { "DLL", "dll", "All files", "*" });
            EditorGUILayout.HelpBox("只支持方法体变化。CLI 会先校验基线、结构和元数据 Token，再由手机核对 MVID、哈希与方法。补丁不会回滚对象状态。", MessageType.None);
            var available = Connected && !_status.isEditor && _patch != null && _patch.available;
            using (new EditorGUI.DisabledScope(!available || !File.Exists(_patchDll) || !File.Exists(_manifest)))
            {
                if (GUILayout.Button("应用方法补丁", GUILayout.Height(28)))
                {
                    var args = new List<string> { "patch", "--dll", _patchDll, "--manifest", _manifest };
                    if (!string.IsNullOrWhiteSpace(_validationDll))
                        args.AddRange(new[] { "--validation-dll", _validationDll });
                    BeginOperation("应用方法补丁", args.ToArray());
                }
            }
            if (Connected && _status.isEditor)
                EditorGUILayout.LabelField("Editor 下 HybridCLR 的 Hotfix 是空实现，因此应用按钮不可用；请连接 IL2CPP Development Player。", EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawLogs()
        {
            EditorGUILayout.LabelField("手机日志", EditorStyles.boldLabel);
            _logType = EditorGUILayout.Popup("日志类型", Mathf.Clamp(_logType, 0, LogTypes.Length - 1), LogTypes);
            _logCount = Mathf.Clamp(EditorGUILayout.IntField("条数", _logCount), 1, 1000);
            using (new EditorGUI.DisabledScope(!Connected))
            {
                if (GUILayout.Button("读取日志"))
                    BeginOperation("读取日志", new[] { "logs", "--log-type", LogTypes[_logType], "--count", _logCount.ToString() });
            }
        }

        private void DrawOutput()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("操作结果", EditorStyles.boldLabel);
            if (GUILayout.Button("清空", GUILayout.Width(60)))
            {
                _output = string.Empty;
                _error = string.Empty;
            }
            EditorGUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_error))
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            if (_outputStyle == null)
                _outputStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            _outputScroll = EditorGUILayout.BeginScrollView(_outputScroll, GUILayout.MinHeight(140), GUILayout.MaxHeight(320));
            EditorGUILayout.TextArea(_output ?? string.Empty, _outputStyle, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private void QueryStatus()
        {
            _status = null;
            _patch = null;
            BeginOperation("读取目标状态", new[] { "status" }, result =>
            {
                var envelope = JsonUtility.FromJson<StatusEnvelope>(result.Output);
                if (envelope == null || envelope.Data == null)
                    throw new InvalidOperationException("Runtime 返回的状态格式无效。");
                // 停止 Play Mode 后到达的旧响应不能重新把 Editor 目标标记为就绪。
                if (envelope.Data.isEditor && !EditorApplication.isPlaying)
                {
                    _status = null;
                    _patch = null;
                    return;
                }
                _status = envelope.Data;
                if (_status.ready)
                    QueryPatchCapabilities();
            });
        }

        private void QueryPatchCapabilities()
        {
            _patch = null;
            BeginOperation("读取热修复能力", new[] { "patch-capabilities" }, result =>
            {
                var envelope = JsonUtility.FromJson<PatchEnvelope>(result.Output);
                if (envelope == null || envelope.Data == null)
                    throw new InvalidOperationException("Runtime 返回的热修复能力格式无效。");
                if (_status == null || (_status.isEditor && !EditorApplication.isPlaying))
                {
                    _patch = null;
                    return;
                }
                _patch = envelope.Data;
            });
        }

        private void RefreshDevices()
        {
            BeginOperation("读取 ADB 设备", new[] { "devices", "--adb", _adb }, result =>
            {
                var envelope = JsonUtility.FromJson<DevicesEnvelope>(result.Output);
                _devices = envelope == null || envelope.devices == null ? Array.Empty<DeviceInfo>() : envelope.devices;
            });
        }

        private void ForwardDevice()
        {
            var args = new List<string> { "device", "--adb", _adb, "--port", _forwardPort.ToString() };
            if (!string.IsNullOrWhiteSpace(_serial))
                args.AddRange(new[] { "--serial", _serial });
            BeginOperation("建立 USB 转发", args.ToArray(), _ =>
            {
                _url = "http://127.0.0.1:" + _forwardPort;
                QueryStatus();
            });
        }

        private void DiscoverTargets()
        {
            BeginOperation("扫描局域网 Runtime", new[] { "discover", "--udp-port", _discoveryPort.ToString(), "--timeout", "3" }, result =>
            {
                var envelope = JsonUtility.FromJson<TargetsEnvelope>(result.Output);
                _targets = envelope == null || envelope.targets == null ? Array.Empty<TargetInfo>() : envelope.targets;
            });
        }

        private void ExecuteCode()
        {
            var args = new List<string> { "exec" };
            if (!string.IsNullOrWhiteSpace(_profile))
                args.AddRange(new[] { "--profile", _profile });
            if (_executionMode == 1)
            {
                args.AddRange(new[] { "--dll", _dll });
                if (!string.IsNullOrWhiteSpace(_entryType))
                    args.AddRange(new[] { "--entry-type", _entryType });
                if (!string.IsNullOrWhiteSpace(_methodName))
                    args.AddRange(new[] { "--method-name", _methodName });
            }
            BeginOperation("执行代码", args.ToArray(), null, _executionMode == 0 ? _code : null);
        }

        private void BeginOperation(string label, string[] arguments, Action<RuntimeWindowResult> completed = null, string temporaryCode = null)
        {
            if (Busy)
                return;
            _error = string.Empty;
            _operation = label;
            try
            {
                _pending = AIBridgeRuntimeWindowBackend.RunAsync(_python, arguments, _url, _token ?? string.Empty, _timeout, temporaryCode);
                _completed = completed;
            }
            catch (Exception exception)
            {
                _error = Redact(exception.Message);
                _pending = null;
                _completed = null;
            }
            Repaint();
        }

        private void PollOperation()
        {
            if (_pending == null || !_pending.IsCompleted)
                return;
            var pending = _pending;
            var completed = _completed;
            _pending = null;
            _completed = null;
            try
            {
                var result = pending.GetAwaiter().GetResult();
                _output = Redact(AIBridgeRuntimeWindowBackend.PrettyJson(result.Output ?? string.Empty));
                if (!result.Success)
                    _error = Redact(string.IsNullOrEmpty(result.Error) ? "操作失败，请查看结果。" : result.Error);
                else
                    completed?.Invoke(result);
            }
            catch (Exception exception)
            {
                _error = Redact(exception.Message);
            }
            Repaint();
        }

        private void LoadConnectionConfiguration()
        {
            var config = Resources.Load<AIBridgeRuntimeConfig>(AIBridgeRuntimeConfig.ResourceName);
            if (config == null)
                return;
            var settings = config.Settings;
            var host = string.IsNullOrWhiteSpace(settings.httpBindAddress) || settings.httpBindAddress == "0.0.0.0" || settings.httpBindAddress == "::"
                ? "127.0.0.1" : settings.httpBindAddress;
            _forwardPort = settings.httpPort >= 1 && settings.httpPort <= 65535 ? settings.httpPort : 27182;
            _token = settings.authToken ?? string.Empty;
            _status = null;
            _patch = null;
            try { _url = new UriBuilder("http", host, _forwardPort).Uri.GetLeftPart(UriPartial.Authority); }
            catch (UriFormatException) { _error = "项目 Runtime HTTP 配置无效，请手动填写连接地址。"; }
        }

        private void RefreshProfiles()
        {
            try { _profiles = AIBridgeRuntimeWindowBackend.FindProfiles() ?? Array.Empty<string>(); }
            catch (Exception exception) { _error = Redact(exception.Message); }
        }

        private void CreateOrApplySceneRuntime()
        {
            var config = Resources.Load<AIBridgeRuntimeConfig>(AIBridgeRuntimeConfig.ResourceName);
            if (config == null)
            {
                _error = "请先在出包设置中创建 Runtime 配置。";
                return;
            }
            var scene = SceneManager.GetActiveScene();
            var runtime = Resources.FindObjectsOfTypeAll<AIBridgeRuntime>().FirstOrDefault(r => !EditorUtility.IsPersistent(r) && r.gameObject.scene == scene);
            if (runtime == null)
            {
                var gameObject = new GameObject("AI Bridge Runtime");
                Undo.RegisterCreatedObjectUndo(gameObject, "Create AI Bridge Runtime");
                runtime = Undo.AddComponent<AIBridgeRuntime>(gameObject);
            }
            Undo.RecordObject(runtime, "Apply AI Bridge Runtime Configuration");
            runtime.runtimeSettings = config.Settings.Clone();
            if (PrefabUtility.IsPartOfPrefabInstance(runtime))
                PrefabUtility.RecordPrefabInstancePropertyModifications(runtime);
            EditorUtility.SetDirty(runtime);
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeObject = runtime.gameObject;
            _output = "已应用当前配置。场景尚未保存，可通过 Undo 撤销。";
        }

        private string Redact(string value)
        {
            value = value ?? string.Empty;
            return string.IsNullOrEmpty(_token) ? value : value.Replace(_token, "<REDACTED>");
        }

        private static string FileField(string label, string value, string[] filters)
        {
            EditorGUILayout.BeginHorizontal();
            value = EditorGUILayout.TextField(label, value ?? string.Empty);
            if (GUILayout.Button("选择", GUILayout.Width(54)))
            {
                var chosen = EditorUtility.OpenFilePanelWithFilters(label, string.Empty, filters);
                if (!string.IsNullOrEmpty(chosen))
                    value = chosen;
            }
            EditorGUILayout.EndHorizontal();
            return value;
        }

#pragma warning disable 0649
        [Serializable] private sealed class StatusEnvelope { public StatusData Data; }
        [Serializable] private sealed class PatchEnvelope { public PatchData Data; }
        [Serializable] private sealed class DevicesEnvelope { public DeviceInfo[] devices; }
        [Serializable] private sealed class TargetsEnvelope { public TargetInfo[] targets; }
        [Serializable] private sealed class DeviceInfo { public string serial; public string state; public string details; }
        [Serializable] private sealed class TargetInfo
        {
            public string targetId; public string platform; public string projectName; public string deviceName; public string reachableUrl;
        }
        [Serializable] private sealed class StatusData
        {
            public string targetId; public string productName; public string platform; public string applicationVersion;
            public string buildId; public bool ready; public bool isEditor; public RuntimeCapabilities capabilities;
        }
        [Serializable] private sealed class RuntimeCapabilities { public bool runtimeCodeExecute; }
        [Serializable] private sealed class PatchData { public bool available; public string reason; public int attempts; public int limit; }
#pragma warning restore 0649
    }
}
