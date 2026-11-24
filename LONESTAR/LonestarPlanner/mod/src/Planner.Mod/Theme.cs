using UnityEngine;

namespace LonestarPlanner.Mod
{
    /// <summary>
    /// 战斗面板和船坞面板共用的外观：配色、字体、样式和几个画图小工具（进度条、分隔线、标题栏）。
    /// 所有样式在第一次 OnGUI 里创建（GUI.skin 只能在 OnGUI 里访问）。
    /// </summary>
    internal static class Ui
    {
        public static readonly Color Accent = new Color(0.36f, 0.78f, 1f);
        public static readonly Color Good = new Color(0.45f, 0.9f, 0.5f);
        public static readonly Color Bad = new Color(1f, 0.42f, 0.38f);
        public static readonly Color Gold = new Color(1f, 0.82f, 0.35f);
        public static readonly Color Dim = new Color(0.62f, 0.66f, 0.72f);
        public static readonly Color Text = new Color(0.9f, 0.92f, 0.95f);
        public static readonly Color Warn = new Color(1f, 0.65f, 0.3f);
        public static readonly Color Purple = new Color(0.85f, 0.5f, 1f);
        public static readonly Color Orange = new Color(1f, 0.6f, 0.2f);

        private static readonly Color Bg = new Color(0.06f, 0.07f, 0.10f, 0.94f);
        private static readonly Color Edge = new Color(0.22f, 0.27f, 0.35f, 1f);

        public static Texture2D White;
        public static Font Font;
        public static GUIStyle Window, Title, Big, Body, Small, Faint, Step, StepNow, StepDone, Section, WarnText, ErrorText;
        public static GUIStyle Button, Primary, ButtonOn, Icon, Badge, LineText, BarText, Hidden;

        private static bool ready;

        /// <summary>界面缩放：按 1080p 等比，再乘设置里的缩放。</summary>
        public static float Scale => Mathf.Clamp(Screen.height / 1080f * (PlannerMod.config?.uiScale ?? 1f), 0.6f, 2.5f);

        public static void Ensure()
        {
            if (ready) return;
            ready = true;
            try
            {
                Font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑", "PingFang SC", "Noto Sans CJK SC", "SimHei", "Segoe UI", "Segoe UI Symbol", "Arial Unicode MS" }, 16);
            }
            catch
            {
                Font = null;
            }

            White = Solid(Color.white);

            Window = new GUIStyle(GUI.skin.window)
            {
                padding = new RectOffset(12, 12, 8, 10),
                border = new RectOffset(1, 1, 1, 1),
                margin = new RectOffset(0, 0, 0, 0),
                overflow = new RectOffset(0, 0, 0, 0),
                contentOffset = Vector2.zero,
                // 面板背景是包住内容的竖排组：高度跟内容走，不能被拉满
                stretchHeight = false,
                fixedHeight = 0,
            };
            Window.normal.background = Framed(Bg, Edge);
            Window.onNormal.background = Window.normal.background;
            Window.focused.background = Window.normal.background;
            Window.onFocused.background = Window.normal.background;

            Title = Label(17, Accent, FontStyle.Bold);
            Big = Label(17, Color.white, FontStyle.Bold, wrap: true);
            Body = Label(15, Text, wrap: true);
            Small = Label(13, Dim, wrap: true);
            Faint = Label(12, new Color(0.5f, 0.54f, 0.6f), wrap: true);
            Step = Label(15, Text, wrap: true);
            StepNow = Label(15, Gold, FontStyle.Bold, wrap: true);
            StepDone = Label(15, new Color(0.5f, 0.55f, 0.6f), wrap: true);
            Section = Label(13, new Color(0.55f, 0.75f, 0.9f), FontStyle.Bold);
            Section.margin = new RectOffset(0, 0, 6, 2);
            WarnText = Label(13, Warn, wrap: true);
            ErrorText = Label(13, Bad, wrap: true);
            LineText = Label(14, Text);
            BarText = Label(12, Color.white);
            BarText.alignment = TextAnchor.MiddleLeft;
            BarText.padding = new RectOffset(6, 6, 0, 0);
            Badge = Label(15, Color.white, FontStyle.Bold);
            Badge.alignment = TextAnchor.MiddleCenter;
            Hidden = Label(13, Color.white, FontStyle.Bold);
            Hidden.alignment = TextAnchor.MiddleCenter;

            Button = MakeButton(new Color(0.15f, 0.18f, 0.24f), new Color(0.2f, 0.24f, 0.32f), new Color(0.11f, 0.13f, 0.18f), new Color(0.3f, 0.36f, 0.46f), Text);
            Primary = MakeButton(new Color(0.11f, 0.38f, 0.56f), new Color(0.14f, 0.46f, 0.67f), new Color(0.09f, 0.3f, 0.45f), new Color(0.3f, 0.65f, 0.9f), Color.white);
            Primary.fontStyle = FontStyle.Bold;
            ButtonOn = MakeButton(new Color(0.12f, 0.32f, 0.18f), new Color(0.15f, 0.4f, 0.22f), new Color(0.1f, 0.26f, 0.15f), new Color(0.35f, 0.75f, 0.45f), Good);
            ButtonOn.fontStyle = FontStyle.Bold;
            Icon = MakeButton(new Color(0, 0, 0, 0), new Color(1, 1, 1, 0.1f), new Color(1, 1, 1, 0.05f), new Color(0, 0, 0, 0), Dim);
            Icon.padding = new RectOffset(4, 4, 2, 2);
            Icon.fontSize = 15;
        }

        private static GUIStyle Label(int size, Color color, FontStyle style = FontStyle.Normal, bool wrap = false)
        {
            var s = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = style, wordWrap = wrap, richText = false };
            s.normal.textColor = color;
            s.margin = new RectOffset(0, 0, 2, 2);
            return s;
        }

        private static GUIStyle MakeButton(Color normal, Color hover, Color active, Color edge, Color text)
        {
            var s = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                padding = new RectOffset(10, 10, 6, 6),
                margin = new RectOffset(2, 2, 2, 2),
                border = new RectOffset(1, 1, 1, 1),
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
                richText = false,
            };
            s.normal.background = Framed(normal, edge);
            s.hover.background = Framed(hover, edge);
            s.active.background = Framed(active, edge);
            s.focused.background = s.normal.background;
            s.onNormal.background = s.normal.background;
            s.onHover.background = s.hover.background;
            s.onActive.background = s.active.background;
            s.normal.textColor = text;
            s.hover.textColor = Color.white;
            s.active.textColor = Color.white;
            s.focused.textColor = text;
            s.onNormal.textColor = text;
            return s;
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        /// <summary>3×3 贴图：中间填充色，一圈 1px 边框（配合 style.border = 1 九宫格拉伸）。</summary>
        private static Texture2D Framed(Color fill, Color edge)
        {
            var t = new Texture2D(3, 3) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            for (int x = 0; x < 3; x++)
            {
                for (int y = 0; y < 3; y++)
                {
                    t.SetPixel(x, y, x == 1 && y == 1 ? fill : edge);
                }
            }
            t.Apply();
            return t;
        }

        // ------------------------------------------------------------------ primitives

        public static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, White);
            GUI.color = old;
        }

        public static void Outline(Rect r, Color c, float t)
        {
            Fill(new Rect(r.x, r.y, r.width, t), c);
            Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
            Fill(new Rect(r.x, r.y, t, r.height), c);
            Fill(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        public static void Line(Vector2 a, Vector2 b, Color c, float width)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 1) return;
            float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, a);
            Fill(new Rect(a.x, a.y - width / 2, len, width), c);
            GUI.matrix = old;
        }

        /// <summary>一条细分隔线。</summary>
        public static void Separator()
        {
            Rect r = GUILayoutUtility.GetRect(10, 9, GUILayout.ExpandWidth(true));
            Fill(new Rect(r.x, r.y + 4, r.width, 1), new Color(1, 1, 1, 0.08f));
        }

        public static void Colored(string text, GUIStyle style, Color c)
        {
            Color old = GUI.contentColor;
            GUI.contentColor = c;
            GUILayout.Label(text, style);
            GUI.contentColor = old;
        }

        /// <summary>
        /// 进度条，文字叠在条上。<paramref name="frac"/> &lt; 0 = 不知道进度（来回滑动的一段）。
        /// </summary>
        public static void Progress(float frac, string text, Color color)
        {
            Rect r = GUILayoutUtility.GetRect(10, 20, GUILayout.ExpandWidth(true));
            Fill(r, new Color(1, 1, 1, 0.07f));
            if (frac < 0f)
            {
                float t = Mathf.PingPong(Time.realtimeSinceStartup * 0.6f, 1f);
                float w = r.width * 0.25f;
                Fill(new Rect(r.x + (r.width - w) * t, r.y, w, r.height), new Color(color.r, color.g, color.b, 0.45f));
            }
            else
            {
                Fill(new Rect(r.x, r.y, r.width * Mathf.Clamp01(frac), r.height), new Color(color.r, color.g, color.b, 0.55f));
            }
            GUI.Label(r, text, BarText);
        }

        /// <summary>转圈的小字符，表示"正在算"。</summary>
        public static string Spinner => Frames[(int)(Time.realtimeSinceStartup * 4f) % Frames.Length];

        // 只用微软雅黑里有的字符（◐、✔、⚠、▶ 之类它没有，会显示成方框）
        private static readonly string[] Frames = { "●○○", "○●○", "○○●", "○●○" };

        /// <summary>
        /// 标题栏：圆点 + 标题，右侧副标题、折叠、关闭。返回 true = 点了关闭。
        /// </summary>
        public static bool TitleBar(string title, string right, ref bool collapsed)
        {
            GUILayout.BeginHorizontal();
            Colored("●", Title, Accent);
            GUILayout.Label(title, Title);
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(right)) GUILayout.Label(right, Small);
            if (GUILayout.Button(collapsed ? "+" : "—", Icon, GUILayout.Width(26))) collapsed = !collapsed;
            bool close = GUILayout.Button("×", Icon, GUILayout.Width(26));
            GUILayout.EndHorizontal();
            return close;
        }

        /// <summary>可折叠的小节标题："▸ 标题" / "▾ 标题"。</summary>
        public static bool Foldout(bool open, string title)
        {
            return GUILayout.Toggle(open, (open ? "－ " : "＋ ") + title, Section);
        }

        /// <summary>面板隐藏时屏幕边上的小标签，点一下（或按 F8）重新打开。</summary>
        public static bool HiddenTab(string text)
        {
            float s = Scale;
            var r = new Rect(0, Screen.height * 0.32f, 30 * s, 120 * s);
            Fill(r, new Color(0.06f, 0.07f, 0.1f, 0.85f));
            Fill(new Rect(r.xMax - 2 * s, r.y, 2 * s, r.height), Accent);
            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(90, r.center);
            var label = new Rect(r.center.x - r.height / 2, r.center.y - r.width / 2, r.height, r.width);
            int size = Hidden.fontSize;
            Hidden.fontSize = Mathf.RoundToInt(13 * s);
            GUI.Label(label, text, Hidden);
            Hidden.fontSize = size;
            GUI.matrix = old;
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }
    }

    /// <summary>
    /// 高度跟着内容走的滚动区：内容不超过 <c>max</c> 时和普通布局一样，超过才出滚动条。
    /// （GUILayout.BeginScrollView 配 MaxHeight 放在自动高度的窗口里会被压成几十像素高。）
    /// 内容高度在 Repaint 时量出来，下一帧生效。
    /// </summary>
    internal sealed class FitScroll
    {
        private Vector2 position;
        private float content = -1f;

        public void Begin(float max)
        {
            max = Mathf.Max(80f, max);
            float height = content < 0f ? Mathf.Min(320f, max) : Mathf.Min(content, max);
            position = GUILayout.BeginScrollView(position, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none, GUILayout.Height(height));
            GUILayout.BeginVertical();
        }

        public void End()
        {
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint)
            {
                content = GUILayoutUtility.GetLastRect().height + 2f;
            }
            GUILayout.EndScrollView();
        }
    }
}
