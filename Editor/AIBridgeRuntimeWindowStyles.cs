using System;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Runtime.Editor
{
    /// <summary>工作台的缓存样式；只使用当前 Editor 主题和可回收的等宽字体。</summary>
    internal sealed class AIBridgeRuntimeWindowStyles : IDisposable
    {
        private readonly Font _codeFont;
        internal readonly bool Dark = EditorGUIUtility.isProSkin;
        internal readonly Color Background, Sidebar, Panel, Border, Text, Muted, Accent, Active, Success;
        internal readonly GUIStyle Header, Rail, Main, Card, Title, Brand, Subtitle, Hint, Tiny, Navigation,
            NavigationActive, Code, Value, ResultText, Primary, Caption, SuccessSubtitle, SuccessHint;

        internal AIBridgeRuntimeWindowStyles()
        {
            Background = Dark ? new Color(.065f, .078f, .086f) : new Color(.89f, .90f, .91f);
            Sidebar = Dark ? new Color(.085f, .10f, .11f) : new Color(.84f, .86f, .87f);
            Panel = Dark ? new Color(.105f, .125f, .137f) : new Color(.96f, .97f, .97f);
            Border = Dark ? new Color(.20f, .24f, .26f) : new Color(.70f, .74f, .76f);
            Text = Dark ? new Color(.90f, .93f, .93f) : new Color(.12f, .16f, .18f);
            Muted = Dark ? new Color(.57f, .64f, .68f) : new Color(.34f, .41f, .45f);
            Accent = new Color(.75f, .87f, .60f);
            Active = Dark ? new Color(.16f, .20f, .14f) : new Color(.73f, .82f, .67f);
            Success = Dark ? new Color(.54f, .80f, .66f) : new Color(.16f, .40f, .24f);
            Header = new GUIStyle { padding = new RectOffset(20, 20, 13, 13) };
            Rail = new GUIStyle { padding = new RectOffset(10, 10, 22, 14) };
            Main = new GUIStyle { padding = new RectOffset(22, 22, 20, 14) };
            Card = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(16, 16, 16, 16), margin = new RectOffset(0, 0, 0, 14) };
            Brand = Label(19, Text, FontStyle.Bold);
            Title = Label(18, Text, FontStyle.Bold);
            Subtitle = Label(13, Text, FontStyle.Bold);
            Hint = Label(11, Muted);
            Hint.wordWrap = true;
            SuccessSubtitle = Label(13, Success, FontStyle.Bold);
            SuccessHint = Label(11, Success);
            SuccessHint.wordWrap = true;
            Tiny = Label(10, Muted);
            Caption = Label(10, Dark ? Accent : new Color(.25f, .39f, .19f));
            Navigation = Label(12, Muted);
            Navigation.alignment = TextAnchor.MiddleLeft;
            Navigation.padding = new RectOffset(36, 6, 0, 0);
            NavigationActive = new GUIStyle(Navigation);
            NavigationActive.normal.textColor = Dark ? Accent : Text;
            NavigationActive.fontStyle = FontStyle.Bold;
            var fontName = Application.platform == RuntimePlatform.WindowsEditor ? "Consolas" : "Menlo";
            _codeFont = Font.CreateDynamicFontFromOSFont(fontName, 13);
            Code = new GUIStyle(EditorStyles.textArea) { font = _codeFont ?? EditorStyles.textArea.font,
                fontSize = 13, padding = new RectOffset(12, 12, 12, 12), wordWrap = false };
            ResultText = new GUIStyle(EditorStyles.textArea) { font = _codeFont ?? EditorStyles.textArea.font,
                fontSize = 11, wordWrap = true, padding = new RectOffset(10, 10, 10, 10) };
            Value = Label(26, Dark ? Accent : new Color(.22f, .36f, .16f));
            Value.wordWrap = true;
            Primary = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fontSize = 12,
                padding = new RectOffset(18, 18, 8, 8) };
        }

        private static GUIStyle Label(int size, Color color, FontStyle weight = FontStyle.Normal)
        {
            var style = new GUIStyle(EditorStyles.label) { fontSize = size, fontStyle = weight, richText = false };
            style.normal.textColor = color;
            return style;
        }

        /// <summary>窗口关闭或重新加载时释放为代码编辑区创建的字体。</summary>
        public void Dispose()
        {
            if (_codeFont != null) UnityEngine.Object.DestroyImmediate(_codeFont);
        }
    }
}
