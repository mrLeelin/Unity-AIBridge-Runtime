using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIBridge.Runtime.Editor
{
    /// <summary>独立的真机 Runtime 管理窗口，通过桌面 CLI 连接 Player。</summary>
    public sealed class AIBridgeRuntimeWindow : EditorWindow
    {
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
        private AIBridgeRuntimeWindowStyles _styles;
        private RuntimeResultSummary _summary;
        private RuntimeLogEntry[] _logs = Array.Empty<RuntimeLogEntry>();
        private bool _logsLoaded;
        private Vector2 _codeScroll;
        private Vector2 _logScroll;
        [SerializeField] private int _connectionMode;
        [SerializeField] private bool _showAdvanced;
        [SerializeField] private bool _showManualDevice;
        [SerializeField] private bool _showTargetDetails;
        [SerializeField] private bool _showProfileDetails;
        [SerializeField] private bool _showRawOutput;
        [SerializeField] private bool _showDllEntry;
        [SerializeField] private bool _showEncryptedPatch;
        [SerializeField] private bool _showManualPatch;
        [SerializeField] private string _sourcePatchAssembly;
        [SerializeField] private bool _restoreConnection;
        private string[] _sourcePatchAssemblies = Array.Empty<string>();
        private string _checkedProfile;
        private string _checkedBuildId;
        private string _checkedPlatform;
        private bool _profileMatched;
        private string _resultOperation = string.Empty;
        private string _notice = string.Empty;

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
            window.minSize = new Vector2(720, 620);
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
            minSize = new Vector2(720, 620);
            if (string.IsNullOrEmpty(_adb))
                _adb = AIBridgeRuntimeWindowBackend.ResolveAdbPath();
            var previousUrl = _url;
            LoadConnectionConfiguration();
            if (_restoreConnection && !string.IsNullOrWhiteSpace(previousUrl)) _url = previousUrl;
            RefreshProfiles();
            if (string.IsNullOrEmpty(_sourcePatchAssembly))
                _sourcePatchAssembly = EditorPrefs.GetString(SourcePatchAssemblyPreferenceKey, string.Empty);
            RefreshSourcePatchAssemblies();
            EditorApplication.update -= PollOperation;
            EditorApplication.update += PollOperation;
            EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
            EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
            EditorApplication.delayCall -= RestoreConnection;
            if (_restoreConnection) EditorApplication.delayCall += RestoreConnection;
        }

        private void OnDisable()
        {
            SaveSourcePatchAssembly();
            EditorApplication.update -= PollOperation;
            EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
            EditorApplication.delayCall -= RestoreConnection;
            // 关闭窗口只停止显示结果，不能撤销已经发送到手机的执行或补丁。
            _pending = null;
            _completed = null;
            _token = string.Empty;
            _styles?.Dispose();
            _styles = null;
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

        /// <summary>绘制工作台：固定导航与目标状态，内容区域独立滚动。</summary>
        private void OnGUI()
        {
            if (_styles == null || _styles.Dark != EditorGUIUtility.isProSkin)
            {
                _styles?.Dispose();
                _styles = new AIBridgeRuntimeWindowStyles();
            }
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), _styles.Background);
            EditorGUI.DrawRect(new Rect(0, 0, position.width, 65), _styles.Panel);
            EditorGUI.DrawRect(new Rect(0, 65, 150, Mathf.Max(0, position.height - 65)), _styles.Sidebar);
            HandleExecutionShortcut();
            DrawHeader();
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            DrawSidebar();
            EditorGUILayout.BeginVertical(_styles.Main, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            DrawConnection();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawPageHeading();
            using (new EditorGUI.DisabledScope(Busy))
            {
                switch (Mathf.Clamp(_tab, 0, 3))
                {
                    case 0: DrawDevices(); break;
                    case 1: DrawExecution(); break;
                    case 2: DrawPatch(); break;
                    case 3: DrawLogs(); break;
                }
            }
            if (_tab != 1) DrawOutput();
            EditorGUILayout.EndScrollView();
            if (Busy) EditorGUILayout.LabelField(_operation + "…", _styles.Hint);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(_styles.Header, GUILayout.Height(65));
            EditorGUILayout.BeginVertical();
            GUILayout.Label("Runtime Bridge", _styles.Brand);
            GUILayout.Label("UNITY PLAYER CONSOLE", _styles.Tiny);
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.Label("Development Player", _styles.Tiny);
            if (GUILayout.Button("出包设置", GUILayout.Width(86), GUILayout.Height(28)))
                GetWindow<AIBridgeRuntimeSettingsWindow>("Runtime Settings").Show();
            EditorGUILayout.EndHorizontal();
            var rect = GUILayoutUtility.GetLastRect();
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), _styles.Border);
        }

        private void DrawSidebar()
        {
            EditorGUILayout.BeginVertical(_styles.Rail, GUILayout.Width(150), GUILayout.ExpandHeight(true));
            GUILayout.Label("WORKSPACE", _styles.Tiny);
            GUILayout.Space(14);
            var names = new[] { "连接设备", "执行代码", "方法热修复", "运行日志" };
            for (var index = 0; index < names.Length; index++)
            {
                var selected = _tab == index;
                var rect = GUILayoutUtility.GetRect(GUIContent.none, _styles.Navigation, GUILayout.Height(40), GUILayout.ExpandWidth(true));
                if (selected) EditorGUI.DrawRect(rect, _styles.Active);
                if (GUI.Button(rect, names[index], selected ? _styles.NavigationActive : _styles.Navigation))
                {
                    _tab = index;
                    _scroll = Vector2.zero;
                    GUI.FocusControl(null);
                }
                GUI.Label(new Rect(rect.x + 10, rect.y + 12, 24, 20), "0" + (index + 1), selected ? _styles.Caption : _styles.Tiny);
                GUILayout.Space(4);
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label(Connected ? "● Runtime 已连接" : "○ Runtime 离线", Connected ? _styles.SuccessHint : _styles.Hint);
            GUILayout.Space(5);
            GUILayout.Label(new DirectoryInfo(Path.Combine(Application.dataPath, "..")).Name, _styles.Tiny);
            GUILayout.Label("Unity " + Application.unityVersion, _styles.Tiny);
            EditorGUILayout.EndVertical();
            var bounds = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(bounds.xMax, bounds.y, 1, bounds.height), _styles.Border);
        }

        private void DrawConnection()
        {
            EditorGUILayout.BeginVertical(_styles.Card);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical();
            GUILayout.Label(Connected ? "● 已就绪  ·  " + _status.productName : Busy ? "○ 正在处理…" : "○ 尚未连接设备", Connected ? _styles.SuccessSubtitle : _styles.Subtitle);
            GUILayout.Space(4);
            GUILayout.Label(Connected ? _status.platform + "  ·  " + _status.applicationVersion + "  ·  " + _url : "先启动设备中的测试包，再建立调试连接。", _styles.Hint);
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(Busy))
            {
                if (Connected && GUILayout.Button("刷新", GUILayout.Width(50), GUILayout.Height(26))) QueryStatus();
                if (Connected && GUILayout.Button("断开", GUILayout.Width(50), GUILayout.Height(26)))
                {
                    _status = null;
                    _patch = null;
                    _restoreConnection = false;
                    _notice = "已断开本窗口连接，Player 中的 Runtime 继续运行。";
                }
            }
            EditorGUILayout.EndHorizontal();
            if (Connected)
            {
                _showTargetDetails = EditorGUILayout.Foldout(_showTargetDetails, "目标详情  /  Build " + ShortId(_status.buildId), true);
                if (_showTargetDetails)
                {
                    ReadOnlyField("目标", _status.targetId);
                    ReadOnlyField("Build ID", _status.buildId);
                    GUILayout.Label("代码执行：" + (CanExecute ? "可用" : "不可用"), CanExecute ? _styles.SuccessHint : _styles.Hint);
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawPageHeading()
        {
            var subtitles = new[] { "选择运行中的目标，建立调试连接。", "在当前 Player 中运行 C#，查看实际返回结果。", "保持游戏运行，替换指定方法的方法体。", "读取当前目标的日志，检查错误和运行状态。" };
            GUILayout.Label("0" + (_tab + 1) + " / " + new[] { "CONNECTION", "EXECUTION", "HOTFIX", "OBSERVABILITY" }[Mathf.Clamp(_tab, 0, 3)], _styles.Caption);
            GUILayout.Space(5);
            GUILayout.Label(new[] { "连接设备", "执行代码", "方法热修复", "运行日志" }[Mathf.Clamp(_tab, 0, 3)], _styles.Title);
            GUILayout.Space(4);
            GUILayout.Label(subtitles[Mathf.Clamp(_tab, 0, 3)], _styles.Hint);
            GUILayout.Space(20);
        }

        private void DrawDevices()
        {
            EditorGUILayout.BeginVertical(_styles.Card);
            GUILayout.Label("选择连接方式", _styles.Subtitle);
            GUILayout.Space(12);
            _connectionMode = GUILayout.Toolbar(Mathf.Clamp(_connectionMode, 0, 2), new[] { "USB / 模拟器", "局域网", "Editor" }, GUILayout.Height(30));
            GUILayout.Space(18);
            if (_connectionMode == 0) DrawUsbDevices();
            else if (_connectionMode == 1) DrawLanTargets();
            else DrawSceneDebugging();
            DrawAdvancedConnection();
            EditorGUILayout.EndVertical();
            if (Connected)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("下一步 · 在运行中的游戏里执行代码", _styles.Hint);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("开始执行代码 →", GUILayout.Height(28))) { _tab = 1; _scroll = Vector2.zero; }
                EditorGUILayout.EndHorizontal();
            }
            if (!string.IsNullOrEmpty(_notice)) GUILayout.Label(_notice, _styles.Hint);
        }

        private void DrawUsbDevices()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("可用设备", _styles.Hint);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("刷新列表", GUILayout.Width(88))) RefreshDevices();
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(8);
            if (_devices.Length == 0)
                GUILayout.Label("尚未读取到设备。连接手机或启动模拟器后，刷新列表。", _styles.Hint);
            else
            {
                var options = new[] { "选择一个设备…" }.Concat(_devices.Select(d => d.serial + "  ·  " + DeviceState(d.state))).ToArray();
                var current = Array.FindIndex(_devices, d => d.serial == _serial) + 1;
                var chosen = EditorGUILayout.Popup(Mathf.Max(0, current), options, GUILayout.Height(25));
                if (chosen != current) _serial = chosen == 0 ? string.Empty : _devices[chosen - 1].serial;
                var device = _devices.FirstOrDefault(d => d.serial == _serial);
                if (device != null && device.state != "device")
                    EditorGUILayout.HelpBox(device.state == "unauthorized" ? "请在设备上允许 USB 调试授权。" : "设备当前离线，请检查模拟器或 USB 连接。", MessageType.Warning);
            }
            GUILayout.Space(7);
            _showManualDevice = EditorGUILayout.Foldout(_showManualDevice, "设备不在列表中？手动指定序列号", true);
            if (_showManualDevice)
            {
                _serial = EditorGUILayout.TextField("设备序列号", _serial ?? string.Empty);
                GUILayout.Label("模拟器可能需要先通过 adb connect 接入 ADB。这里的序列号用于选择已接入的设备。", _styles.Hint);
            }
            GUILayout.Space(14);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("使用当前项目配置建立 USB 转发。", _styles.Hint);
            GUILayout.FlexibleSpace();
            if (PrimaryButton("连接设备", !string.IsNullOrWhiteSpace(_serial) || _devices.Count(d => d.state == "device") == 1)) ForwardDevice();
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(7);
            GUILayout.Label("连接不会安装或重启游戏，请先启动设备中的测试包。", _styles.Hint);
        }

        private void DrawLanTargets()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("局域网中的 Runtime", _styles.Subtitle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("扫描", GUILayout.Width(70))) DiscoverTargets();
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(10);
            if (_targets.Length == 0) GUILayout.Label("目标需要开启 LAN 发现。扫描仅在点击时进行。", _styles.Hint);
            foreach (var target in _targets)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                GUILayout.Label(target.projectName ?? target.targetId ?? "Runtime", _styles.Subtitle);
                GUILayout.Label(target.platform + " · " + target.deviceName, _styles.Hint);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(target.reachableUrl ?? string.Empty, _styles.Hint);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(target.reachableUrl)))
                    if (GUILayout.Button("连接", GUILayout.Width(64))) { _url = target.reachableUrl; QueryStatus(); }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
            GUILayout.Space(12);
            DrawHttpAddress();
            if (PrimaryButton("连接此地址")) QueryStatus();
        }

        private void DrawSceneDebugging()
        {
            GUILayout.Label("Editor Play Mode 协议调试", _styles.Subtitle);
            GUILayout.Space(8);
            GUILayout.Label("进入 Play Mode 前创建场景组件。Editor 中不支持原生方法热修复。", _styles.Hint);
            GUILayout.Space(12);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
                if (GUILayout.Button("在当前场景创建 / 应用 Runtime 配置", GUILayout.Height(28))) CreateOrApplySceneRuntime();
            GUILayout.Label("仅点击时修改场景，支持 Undo；不会自动保存。", _styles.Hint);
            GUILayout.Space(12);
            DrawHttpAddress();
            if (PrimaryButton("连接 Play Mode", EditorApplication.isPlaying)) QueryStatus();
        }

        private void DrawAdvancedConnection()
        {
            GUILayout.Space(14);
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "高级连接设置", true);
            if (!_showAdvanced) return;
            GUILayout.Space(8);
            DrawHttpAddress();
            EditorGUI.BeginChangeCheck();
            _token = EditorGUILayout.PasswordField("会话 Token", _token ?? string.Empty);
            if (EditorGUI.EndChangeCheck()) { _status = null; _patch = null; }
            _timeout = Mathf.Clamp(EditorGUILayout.IntField("超时（秒）", _timeout), 1, 300);
            _python = EditorGUILayout.TextField("Python", _python);
            _adb = EditorGUILayout.TextField("ADB", _adb);
            _forwardPort = Mathf.Clamp(EditorGUILayout.IntField("USB 转发端口", _forwardPort), 1, 65535);
            _discoveryPort = Mathf.Clamp(EditorGUILayout.IntField("LAN 发现端口", _discoveryPort), 1, 65535);
            GUILayout.Space(8);
            if (GUILayout.Button("使用项目 Runtime 配置")) LoadConnectionConfiguration();
        }

        private void DrawHttpAddress()
        {
            EditorGUI.BeginChangeCheck();
            _url = EditorGUILayout.TextField("HTTP 地址", _url);
            if (EditorGUI.EndChangeCheck()) { _status = null; _patch = null; }
        }

        private void DrawExecution()
        {
            var wide = position.width >= 1080;
            if (wide) EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(_styles.Card, GUILayout.ExpandWidth(true));
            DrawProfile();
            GUILayout.Space(12);
            _executionMode = GUILayout.Toolbar(_executionMode, new[] { "C# 片段", "已编译 DLL" }, GUILayout.Height(28));
            GUILayout.Space(16);
            if (_executionMode == 0) DrawCodeEditor();
            else
            {
                _dll = FileField("调试 DLL", _dll, new[] { "DLL / Bytes", "dll,bytes", "All files", "*" });
                _showDllEntry = EditorGUILayout.Foldout(_showDllEntry, "入口设置（可选）", true);
                if (_showDllEntry)
                {
                    _entryType = EditorGUILayout.TextField("入口类型", _entryType);
                    _methodName = EditorGUILayout.TextField("方法名称", _methodName);
                }
            }
            GUILayout.Space(14);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(ExecutionHint(), _styles.Hint);
            GUILayout.FlexibleSpace();
            if (PrimaryButton("运行代码", ExecutionReady())) ExecuteCode();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            if (wide)
            {
                GUILayout.Space(16);
                EditorGUILayout.BeginVertical(GUILayout.Width(280));
            }
            DrawOutput();
            if (wide) { EditorGUILayout.EndVertical(); EditorGUILayout.EndHorizontal(); }
        }

        private void DrawCodeEditor()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("C# · 临时代码", _styles.Tiny);
            GUILayout.FlexibleSpace();
            GUILayout.Label("Ctrl / ⌘ + Enter", _styles.Tiny);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(6);
            _codeScroll = EditorGUILayout.BeginScrollView(_codeScroll, GUILayout.Height(Mathf.Clamp(position.height * .3f, 180, 330)));
            _code = EditorGUILayout.TextArea(_code ?? string.Empty, _styles.Code, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            GUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("示例", _styles.Tiny, GUILayout.Width(28));
            if (GUILayout.Button(new GUIContent("计算结果", "只填入代码，不会自动执行"), EditorStyles.miniButton)) _code = "return 5 * 10;";
            if (GUILayout.Button(new GUIContent("读取场景", "只填入代码，不会自动执行"), EditorStyles.miniButton)) _code = "return new\n{\n    platform = UnityEngine.Application.platform.ToString(),\n    scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name\n};";
            if (GUILayout.Button(new GUIContent("异步等待", "只填入代码，不会自动执行"), EditorStyles.miniButton)) _code = "await System.Threading.Tasks.Task.Delay(100);\nreturn 43;";
            EditorGUILayout.EndHorizontal();
        }

        private void DrawProfile()
        {
            if (Connected && _status.isEditor)
            {
                GUILayout.Label("Editor 协议调试", _styles.Subtitle);
                GUILayout.Label("已编译 DLL 直接发送到 Editor，不附带 Player 构建引用。", _styles.Hint);
                return;
            }
            var matched = ProfileMatched();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical();
            GUILayout.Label(matched ? "● 构建引用已匹配" : "○ 尚未匹配构建引用", matched ? _styles.SuccessSubtitle : _styles.Subtitle);
            GUILayout.Label(matched ? "Build " + ShortId(_status.buildId) + "  /  " + _status.platform : "需要选择与当前 Player 对应的 profile。", _styles.Hint);
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(!Connected || _status.isEditor))
                if (GUILayout.Button(matched ? "重新匹配" : "匹配当前构建", GUILayout.Width(100))) { RefreshProfiles(); MatchCurrentProfile(true); }
            EditorGUILayout.EndHorizontal();
            _showProfileDetails = EditorGUILayout.Foldout(_showProfileDetails, "手动选择 profile", true);
            if (_showProfileDetails)
            {
                _profile = FileField("Player profile", _profile, new[] { "Reference profile", "json", "All files", "*" });
                var options = new[] { "选择已有构建…" }.Concat(_profiles.Select(p => new DirectoryInfo(Path.GetDirectoryName(p) ?? string.Empty).Name)).ToArray();
                var current = Array.FindIndex(_profiles, p => p == _profile) + 1;
                var selected = EditorGUILayout.Popup(current, options);
                if (selected != current) _profile = selected == 0 ? string.Empty : _profiles[selected - 1];
            }
            if (!string.IsNullOrEmpty(_notice)) GUILayout.Label(_notice, _styles.Hint);
        }

        private void DrawPatch()
        {
            EditorGUILayout.BeginVertical(_styles.Card);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("HybridCLR · 方法热修复", _styles.Subtitle);
            GUILayout.FlexibleSpace();
            GUILayout.Label(_patch == null ? "尚未检查" : _patch.available ? "● 可用" : "○ 不可用", _patch != null && _patch.available ? _styles.SuccessHint : _styles.Hint);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(12);
            using (new EditorGUI.DisabledScope(!Connected))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("检查能力")) QueryPatchCapabilities();
                if (GUILayout.Button("补丁历史")) BeginOperation("读取补丁历史", new[] { "patch-history" });
                EditorGUILayout.EndHorizontal();
            }
            if (_patch != null)
            {
                if (!_patch.available) EditorGUILayout.HelpBox(_patch.reason ?? "当前目标不支持方法热修复。", MessageType.Warning);
                GUILayout.Label("上次检查的原生应用次数  " + _patch.attempts + " / " + _patch.limit, _styles.Hint);
            }
            GUILayout.Space(16);
            DrawSourcePatchControls();
            GUILayout.Space(12);
            _showManualPatch = EditorGUILayout.Foldout(_showManualPatch, "手动应用已有 DLL（高级）", true);
            if (_showManualPatch) DrawManualPatchControls();
            EditorGUILayout.EndVertical();
        }

        private void DrawSourcePatchControls()
        {
            GUILayout.Label("从当前源码编译", _styles.Subtitle);
            GUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            var options = new[] { "选择目标程序集…" }.Concat(_sourcePatchAssemblies).ToArray();
            var current = Array.IndexOf(_sourcePatchAssemblies, _sourcePatchAssembly) + 1;
            var selected = EditorGUILayout.Popup(Mathf.Max(0, current), options);
            if (selected != current)
            {
                _sourcePatchAssembly = selected == 0 ? null : _sourcePatchAssemblies[selected - 1];
                SaveSourcePatchAssembly();
            }
            if (GUILayout.Button("刷新", GUILayout.Width(52))) RefreshSourcePatchAssemblies();
            EditorGUILayout.EndHorizontal();
            if (GUILayout.Button(new GUIContent("使用选中的 C# 脚本", "在 Project 窗口选中一个 C# 文件，从它识别目标程序集")))
                UseSelectedPatchScript();
            GUILayout.Space(8);
            GUILayout.Label("目标程序集只需选择一次。保存 C# 后点击“编译并应用”，最新 DLL 会直接更新到当前真机。", _styles.Hint);
            GUILayout.Label("DLL、基线、清单和补丁 ID 自动管理，后续修改无需重新选文件。", _styles.Hint);
            GUILayout.Label("目标需为已加载且支持方法热修复的程序集；普通 AOT 代码不能直接替换。", _styles.Hint);
            GUILayout.Space(12);
            var available = Connected && !_status.isEditor && _patch != null && _patch.available;
            var ready = available && CanExecute && ProfileMatched() && !string.IsNullOrWhiteSpace(_sourcePatchAssembly)
                && !EditorApplication.isCompiling && !EditorApplication.isUpdating;
            if (PrimaryButton("编译并应用", ready)) CompileAndApplySource();
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                GUILayout.Label("等待 Editor 完成源码编译后即可应用。", _styles.Hint);
            else if (Connected && !ProfileMatched())
                GUILayout.Label("请先在执行代码页匹配当前 Player 的构建引用。", _styles.Hint);
        }

        private void DrawManualPatchControls()
        {
            GUILayout.Space(10);
            _patchDll = FileField("补丁 DLL", _patchDll, new[] { "DLL / Bytes", "dll,bytes", "All files", "*" });
            _manifest = FileField("补丁清单", _manifest, new[] { "Patch manifest", "json", "All files", "*" });
            GUILayout.Space(8);
            _showEncryptedPatch = EditorGUILayout.Foldout(_showEncryptedPatch, "加固 DLL 设置（可选）", true);
            if (_showEncryptedPatch) _validationDll = FileField("明文候选 DLL", _validationDll, new[] { "DLL", "dll", "All files", "*" });
            GUILayout.Space(10);
            EditorGUILayout.HelpBox("仅支持方法体变化。基线、结构和哈希会先校验；补丁不会回滚对象状态。", MessageType.None);
            GUILayout.Space(12);
            var available = Connected && !_status.isEditor && _patch != null && _patch.available;
            if (PrimaryButton("应用方法补丁", available && File.Exists(_patchDll) && File.Exists(_manifest)))
            {
                var args = new List<string> { "patch", "--dll", _patchDll, "--manifest", _manifest };
                if (!string.IsNullOrWhiteSpace(_validationDll)) args.AddRange(new[] { "--validation-dll", _validationDll });
                BeginOperation("应用方法补丁", args.ToArray());
            }
        }

        private void RefreshSourcePatchAssemblies()
        {
            try
            {
                var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var configured = AIBridgeRuntimeProjectIntegration.ConfiguredPatchAssemblies();
                _sourcePatchAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player)
                    .Where(assembly => assembly.sourceFiles != null && assembly.sourceFiles.Length > 0
                        && (assembly.sourceFiles.Any(path => path.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                            || path.Replace('\\', '/').StartsWith(Application.dataPath.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase))
                            || configured.Contains(assembly.name)))
                    .Select(assembly => assembly.name).Distinct()
                    .OrderBy(name => configured.Contains(name) ? 0 : 1)
                    .ThenBy(name => File.Exists(Path.Combine(root, ".runtimebridge", "baseline", name + ".dll")) ? 0 : 1)
                    .ThenBy(name => name.IndexOf("Hotfix", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                    .ThenBy(name => name, StringComparer.Ordinal).ToArray();
                if (string.IsNullOrEmpty(_sourcePatchAssembly) && _sourcePatchAssemblies.Length > 0)
                    _sourcePatchAssembly = _sourcePatchAssemblies[0];
                if (!string.IsNullOrWhiteSpace(_sourcePatchAssembly) && !_sourcePatchAssemblies.Contains(_sourcePatchAssembly))
                    _sourcePatchAssemblies = new[] { _sourcePatchAssembly }.Concat(_sourcePatchAssemblies).ToArray();
            }
            catch (Exception exception) { _error = Redact(exception.Message); }
        }

        private void UseSelectedPatchScript()
        {
            var script = Selection.activeObject as MonoScript;
            if (script == null) { _notice = "请先在 Project 窗口选中一个 C# 脚本。"; return; }
            var name = CompilationPipeline.GetAssemblyNameFromScriptPath(AssetDatabase.GetAssetPath(script));
            if (string.IsNullOrEmpty(name)) { _notice = "无法识别这个脚本所属的 Player 程序集。"; return; }
            _sourcePatchAssembly = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
            SaveSourcePatchAssembly();
            _notice = string.Empty;
            RefreshSourcePatchAssemblies();
        }

        private void CompileAndApplySource()
        {
            if (Busy || !Connected || _status.isEditor || !ProfileMatched() || _patch == null || !_patch.available) return;
            SaveSourcePatchAssembly();
            BeginOperation("编译并应用源码", new[] { "patch-source", "--project", Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "--assembly", _sourcePatchAssembly, "--profile", _profile }, result =>
            {
                var sourcePatch = JsonUtility.FromJson<SourcePatchEnvelope>(result.Output)?.Data?.sourcePatch;
                if (sourcePatch == null || string.IsNullOrEmpty(sourcePatch.dll) || string.IsNullOrEmpty(sourcePatch.manifest)) return;
                _patchDll = sourcePatch.dll;
                _manifest = sourcePatch.manifest;
                _validationDll = string.Empty;
            });
        }

        private static string SourcePatchAssemblyPreferenceKey => "AIBridge.Runtime.SourcePatchAssembly."
            + Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');

        private void SaveSourcePatchAssembly()
        {
            EditorPrefs.SetString(SourcePatchAssemblyPreferenceKey, _sourcePatchAssembly ?? string.Empty);
        }

        private void RestoreConnection()
        {
            if (this != null && _restoreConnection && !Busy) QueryStatus();
        }

        private void DrawLogs()
        {
            EditorGUILayout.BeginVertical(_styles.Card);
            EditorGUILayout.BeginHorizontal();
            var selectedType = EditorGUILayout.Popup(Mathf.Clamp(_logType, 0, LogTypes.Length - 1), LogTypes, GUILayout.Width(100));
            if (selectedType != _logType)
            {
                _logType = selectedType;
                _logs = Array.Empty<RuntimeLogEntry>();
                _logsLoaded = false;
            }
            _logCount = Mathf.Clamp(EditorGUILayout.IntField(_logCount, GUILayout.Width(60)), 1, 1000);
            GUILayout.Label("条", _styles.Tiny);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("清空视图", GUILayout.Width(76))) { _logs = Array.Empty<RuntimeLogEntry>(); _logsLoaded = false; }
            using (new EditorGUI.DisabledScope(!Connected))
                if (GUILayout.Button("读取日志", GUILayout.Width(86))) BeginOperation("读取日志", new[] { "logs", "--log-type", LogTypes[_logType], "--count", _logCount.ToString() });
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(12);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(Mathf.Clamp(position.height * .4f, 190, 450)));
            if (_logs.Length == 0) GUILayout.Label(_logsLoaded ? "当前筛选下没有日志。" : "点击读取日志，查看当前目标的输出。", _styles.Hint);
            foreach (var log in _logs)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                GUILayout.Label(log.Type ?? "Log", _styles.Caption);
                GUILayout.Label(log.Message ?? string.Empty, EditorStyles.wordWrappedLabel);
                if (!string.IsNullOrEmpty(log.StackTrace)) GUILayout.Label(log.StackTrace, _styles.Hint);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawOutput()
        {
            EditorGUILayout.BeginVertical(_styles.Card);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(_tab == 1 ? "执行结果" : "最近操作", _styles.Subtitle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("清空", EditorStyles.miniButton, GUILayout.Width(48))) { _output = string.Empty; _error = string.Empty; _summary = null; _showRawOutput = false; }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(10);
            if (!string.IsNullOrEmpty(_error)) EditorGUILayout.HelpBox(_error, MessageType.Error);
            else if (_summary == null) GUILayout.Label("运行后，在这里查看返回结果。", _styles.Hint);
            else
            {
                GUILayout.Label(_summary.Title ?? _resultOperation, _styles.Hint);
                if (_summary.HasValue)
                {
                    GUILayout.Space(8);
                    var value = _summary.Value ?? "null";
                    var scalar = value.Length < 42 && !value.Contains("\n");
                    var style = scalar ? _styles.Value : _styles.ResultText;
                    var width = position.width >= 1080 && _tab == 1 ? 230 : Mathf.Max(220, position.width - 250);
                    var height = Mathf.Clamp(style.CalcHeight(new GUIContent(value), width), 40, 180);
                    EditorGUILayout.SelectableLabel(value, style, GUILayout.Height(height), GUILayout.ExpandWidth(true));
                }
                if (!string.IsNullOrEmpty(_summary.Detail)) GUILayout.Label(_summary.Detail, _styles.Hint);
            }
            if (!string.IsNullOrEmpty(_output))
            {
                GUILayout.Space(10);
                EditorGUILayout.BeginHorizontal();
                _showRawOutput = EditorGUILayout.Foldout(_showRawOutput, "原始 JSON / 完整输出", true);
                if (GUILayout.Button("复制", EditorStyles.miniButton, GUILayout.Width(48))) EditorGUIUtility.systemCopyBuffer = _output;
                EditorGUILayout.EndHorizontal();
                if (_showRawOutput)
                {
                    _outputScroll = EditorGUILayout.BeginScrollView(_outputScroll, GUILayout.Height(180));
                    EditorGUILayout.TextArea(_output, _styles.ResultText, GUILayout.ExpandHeight(true));
                    EditorGUILayout.EndScrollView();
                }
            }
            EditorGUILayout.EndVertical();
        }

        private bool PrimaryButton(string label, bool enabled = true)
        {
            var previous = GUI.backgroundColor;
            GUI.backgroundColor = _styles.Accent;
            bool clicked;
            using (new EditorGUI.DisabledScope(!enabled || Busy)) clicked = GUILayout.Button(label, _styles.Primary, GUILayout.Height(32));
            GUI.backgroundColor = previous;
            return clicked;
        }

        private bool ExecutionReady()
        {
            return CanExecute && (_executionMode == 1
                ? File.Exists(_dll) && (_status.isEditor || string.IsNullOrWhiteSpace(_profile) || ProfileMatched())
                : !_status.isEditor && ProfileMatched() && !string.IsNullOrWhiteSpace(_code));
        }

        private string ExecutionHint()
        {
            if (!Connected) return "先连接设备，再执行代码。";
            if (!CanExecute) return "当前目标未启用代码执行。";
            if (_executionMode == 0 && _status.isEditor) return "Editor 协议调试请使用已编译 DLL。";
            if (_executionMode == 0 && !ProfileMatched()) return "请先匹配当前 Player 的构建引用。";
            if (_executionMode == 1 && !_status.isEditor && !string.IsNullOrWhiteSpace(_profile) && !ProfileMatched())
                return "所选 profile 与目标不匹配，请重新匹配或清空。";
            return "代码将在当前目标中运行。";
        }

        private bool ProfileMatched()
        {
            if (!Connected || _status.isEditor) return false;
            if (_checkedProfile != _profile || _checkedBuildId != _status.buildId || _checkedPlatform != _status.platform)
            {
                _checkedProfile = _profile;
                _checkedBuildId = _status.buildId;
                _checkedPlatform = _status.platform;
                _profileMatched = AIBridgeRuntimePresentation.ProfileMatches(_profile, _status.buildId, _status.platform);
            }
            return _profileMatched;
        }

        private void MatchCurrentProfile(bool showNotice)
        {
            if (!Connected || _status.isEditor) return;
            var match = AIBridgeRuntimePresentation.FindMatchingProfile(_profiles, _status.buildId, _status.platform);
            if (match != null) { _profile = match; _notice = string.Empty; }
            else if (showNotice) _notice = "未找到当前 Build ID 的 profile，请先为这次 Player 构建导出引用。";
            _checkedProfile = null;
        }

        private void HandleExecutionShortcut()
        {
            var current = Event.current;
            if (_tab != 1 || Busy || current.type != EventType.KeyDown || !(current.control || current.command)
                || (current.keyCode != KeyCode.Return && current.keyCode != KeyCode.KeypadEnter) || !ExecutionReady()) return;
            ExecuteCode();
            current.Use();
        }

        private static string ShortId(string value)
        {
            return string.IsNullOrEmpty(value) ? "—" : value.Length <= 18 ? value : value.Substring(0, 8) + "…" + value.Substring(value.Length - 6);
        }

        private static string DeviceState(string state)
        {
            return state == "device" ? "在线" : state == "unauthorized" ? "未授权" : state == "offline" ? "离线" : state;
        }

        private static void ReadOnlyField(string label, string value)
        {
            EditorGUILayout.LabelField(label);
            EditorGUILayout.SelectableLabel(value ?? string.Empty, GUILayout.Height(EditorGUIUtility.singleLineHeight));
        }


        private void QueryStatus()
        {
            if (Busy) return;
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
                {
                    _restoreConnection = true;
                    _notice = string.Empty;
                    RefreshProfiles();
                    if (!ProfileMatched()) MatchCurrentProfile(false);
                    QueryPatchCapabilities();
                }
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
                var online = _devices.Where(device => device.state == "device").ToArray();
                if (online.Length == 1 && !_devices.Any(device => device.serial == _serial && device.state == "device"))
                    _serial = online[0].serial;
            });
        }

        private void ForwardDevice()
        {
            if (Busy) return;
            _status = null;
            _patch = null;
            _notice = string.Empty;
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
            if (!string.IsNullOrWhiteSpace(_profile) && (_status == null || !_status.isEditor))
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
                _resultOperation = _operation;
                _output = Redact(AIBridgeRuntimeWindowBackend.PrettyJson(result.Output ?? string.Empty));
                _summary = AIBridgeRuntimePresentation.Summarize(_output, result.Success, _resultOperation);
                if (!result.Success || !_summary.Success)
                    _error = Redact(string.IsNullOrEmpty(_summary.Detail)
                        ? string.IsNullOrEmpty(result.Error) ? "操作失败，请展开原始输出。" : result.Error
                        : _summary.Detail);
                else
                {
                    if (_resultOperation == "读取日志")
                    {
                        _logs = _summary.Logs ?? Array.Empty<RuntimeLogEntry>();
                        _logsLoaded = true;
                    }
                    completed?.Invoke(result);
                }
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
            _checkedProfile = null;
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
            _summary = AIBridgeRuntimePresentation.Summarize(_output, true, "场景配置");
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
        [Serializable] private sealed class SourcePatchEnvelope { public SourcePatchResult Data; }
        [Serializable] private sealed class SourcePatchResult { public SourcePatchFiles sourcePatch; }
        [Serializable] private sealed class SourcePatchFiles { public string dll; public string manifest; }
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
