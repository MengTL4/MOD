using System;
using Lean.Localization;

namespace LonestarPlanner.Mod
{
    /// <summary>
    /// 界面语言：跟随游戏（简体/繁体中文 → 中文，其余 → 英文），设置里可以强制指定。
    /// 主线程每帧 <see cref="Refresh"/> 一次，结果缓存起来，所以后台线程里调用 <see cref="T"/> 也安全。
    /// </summary>
    internal static class L
    {
        private static volatile bool zh = true;

        public static bool Zh => zh;

        public static string T(string zh, string en) => L.zh ? zh : en;

        /// <summary>只在主线程调用（读游戏的当前语言）。</summary>
        public static void Refresh()
        {
            string forced = PlannerMod.config?.language;
            if (forced == PlannerMod.LanguageZh)
            {
                zh = true;
                return;
            }
            if (forced == PlannerMod.LanguageEn)
            {
                zh = false;
                return;
            }
            try
            {
                string lang = LeanLocalization.GetFirstCurrentLanguage();
                zh = string.IsNullOrEmpty(lang) || lang.StartsWith("Chinese", StringComparison.Ordinal);
            }
            catch
            {
                // 读不到就保持上一次
            }
        }

        /// <summary>"30 秒" / "30 s"，"2.5 分钟" / "2.5 min"，0 = 不限时。</summary>
        public static string Duration(float seconds)
        {
            if (seconds <= 0f) return T("不限时", "No limit");
            if (seconds >= 60f) return (seconds / 60f).ToString("0.#") + T(" 分钟", " min");
            return seconds.ToString("0") + T(" 秒", " s");
        }

        /// <summary>计时显示：12.3s / 2:05。</summary>
        public static string Clock(double seconds)
        {
            if (seconds < 60) return seconds.ToString("0.0") + "s";
            int s = (int)seconds;
            return (s / 60) + ":" + (s % 60).ToString("00");
        }
    }
}
