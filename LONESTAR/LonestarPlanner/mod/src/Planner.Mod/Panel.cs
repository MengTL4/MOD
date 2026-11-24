using System;
using UnityEngine;

namespace LonestarPlanner.Mod
{
    /// <summary>
    /// 战斗和船坞共用的那一个面板：同一个位置、宽度、显示/折叠状态（两处不会同时出现，
    /// 1.0 是两个窗口，各记各的位置）。
    ///
    /// 不用 GUILayout.Window：自动高度要靠每个事件把高度清零、等 Unity 的原生窗口重新量，
    /// 拖动时会一闪一闪。这里自己画：背景就是包住内容的那个竖排组，排版和绘制在同一帧，
    /// 高度永远贴合内容；拖动自己处理（抓住标题栏，按住期间独占鼠标）。
    /// </summary>
    internal static class Panel
    {
        public const float Width = 440f;
        private const float TitleHeight = 30f;
        private const string PrefX = "LonestarPlanner.panel.x", PrefY = "LonestarPlanner.panel.y", PrefCollapsed = "LonestarPlanner.panel.collapsed";
        // 1.0 战斗面板的位置：第一次用新面板时沿用
        private const string OldX = "LonestarPlanner.hud.x", OldY = "LonestarPlanner.hud.y";

        public static bool Visible
        {
            get
            {
                Load();
                return visible;
            }
            set
            {
                Load();
                visible = value;
            }
        }

        /// <summary>折叠：只留标题和最要紧的几行。</summary>
        public static bool Collapsed
        {
            get
            {
                Load();
                return collapsed;
            }
        }

        /// <summary>面板顶边到屏幕底边还剩多高（界面坐标）：滚动区的上限，免得内容掉到屏幕外。</summary>
        public static float Room => Screen.height / Ui.Scale - position.y;

        private static bool visible = true, collapsed;
        private static Vector2 position = new Vector2(20, 80);
        private static string lastError;
        private static Rect drawn;
        private static Vector2 grab;
        private static bool loaded;

        private static void Load()
        {
            if (loaded) return;
            loaded = true;
            visible = PlannerMod.config?.visibleByDefault ?? true;
            try
            {
                string kx = PlayerPrefs.HasKey(PrefX) ? PrefX : OldX, ky = PlayerPrefs.HasKey(PrefY) ? PrefY : OldY;
                position = new Vector2(PlayerPrefs.GetFloat(kx, position.x), PlayerPrefs.GetFloat(ky, position.y));
                collapsed = PlayerPrefs.GetInt(PrefCollapsed, 0) == 1;
            }
            catch
            {
                // 默认位置
            }
        }

        /// <summary>
        /// 在 OnGUI 里调用：标题栏（拖动、折叠、关闭）+ <paramref name="body"/>。
        /// 面板隐藏时只画屏幕边上的小标签。
        /// </summary>
        public static void Draw(string title, string right, Action body)
        {
            Load();
            if (!visible)
            {
                if (Ui.HiddenTab(L.T("求解器 F8", "Solver F8"))) visible = true;
                return;
            }
            float scale = Ui.Scale;
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            try
            {
                // 每个事件都要取，控件 id 才对得上
                int dragId = GUIUtility.GetControlID(FocusType.Passive);
                float screenW = Screen.width / scale, screenH = Screen.height / scale;
                position.x = Mathf.Clamp(position.x, 0, Mathf.Max(0, screenW - 80));
                position.y = Mathf.Clamp(position.y, 0, Mathf.Max(0, screenH - 40));

                GUILayout.BeginArea(new Rect(position.x, position.y, Width, screenH - position.y));
                GUILayout.BeginVertical(Ui.Window, GUILayout.Width(Width));
                bool wasCollapsed = collapsed;
                if (Ui.TitleBar(title, right, ref collapsed)) visible = false;
                if (collapsed != wasCollapsed) PlayerPrefs.SetInt(PrefCollapsed, collapsed ? 1 : 0);
                try
                {
                    body();
                }
                catch (Exception ex) when (!(ex is ExitGUIException))
                {
                    // 内容画到一半出错：面板本身照常收尾，错误只记一次
                    if (ex.Message != lastError) Debug.LogWarning("[Planner] panel: " + ex);
                    lastError = ex.Message;
                }
                GUILayout.EndVertical();
                if (Event.current.type == EventType.Repaint)
                {
                    Rect r = GUILayoutUtility.GetLastRect();
                    drawn = new Rect(position.x + r.x, position.y + r.y, r.width, r.height);
                }
                GUILayout.EndArea();

                Drag(dragId);
            }
            finally
            {
                GUI.matrix = old;
            }
        }

        /// <summary>标题栏按下开始拖；按钮先处理过的点击（折叠、关闭）已经被用掉，不会走到这里。</summary>
        private static void Drag(int id)
        {
            Event e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && new Rect(drawn.x, drawn.y, drawn.width, TitleHeight).Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        grab = e.mousePosition - position;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        position = e.mousePosition - grab;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                        PlayerPrefs.SetFloat(PrefX, position.x);
                        PlayerPrefs.SetFloat(PrefY, position.y);
                    }
                    break;
            }
        }
    }
}
