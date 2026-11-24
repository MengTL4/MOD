using System;
using UnityEngine;

namespace LonestarPlanner.Mod
{
    /// <summary>
    /// 求解时间：每回合（战斗）和摆位上限（船坞）各一个。面板上的 -/+ 按档位调，
    /// 和官方设置面板存在同一个 config.json 里。每回合的 0 = 不限时（完整深度搜索跑完，
    /// 同一局面结果固定），是最高一档。
    /// </summary>
    internal static class SolveBudget
    {
        public const float Unlimited = 0f;

        public static readonly float[] BattleSteps = { 10f, 15f, 20f, 30f, 45f, 60f, 90f, 120f, 180f, 300f, Unlimited };
        public static readonly float[] LoadoutSteps = { 60f, 120f, 180f, 300f, 450f, 600f, 900f, 1200f, 1800f };

        // 1.0 版存在 PlayerPrefs 里，读一次搬进 config.json
        private const string OldBattleKey = "LonestarPlanner.battleSeconds.v2";
        private const string OldLoadoutKey = "LonestarPlanner.loadoutSeconds.v2";

        public static void Load(PlannerMod.PlannerConfig config)
        {
            try
            {
                if (PlayerPrefs.HasKey(OldBattleKey) || PlayerPrefs.HasKey(OldLoadoutKey))
                {
                    if (PlayerPrefs.HasKey(OldBattleKey)) config.battleSeconds = PlayerPrefs.GetFloat(OldBattleKey);
                    if (PlayerPrefs.HasKey(OldLoadoutKey)) config.loadoutSeconds = PlayerPrefs.GetFloat(OldLoadoutKey);
                    PlayerPrefs.DeleteKey(OldBattleKey);
                    PlayerPrefs.DeleteKey(OldLoadoutKey);
                    Clamp(config);
                    PlannerMod.SaveConfig();
                    return;
                }
            }
            catch
            {
                // 读不到就用设置里的
            }
            Clamp(config);
        }

        private static void Clamp(PlannerMod.PlannerConfig config)
        {
            config.battleSeconds = config.battleSeconds > 0f ? Mathf.Clamp(config.battleSeconds, BattleSteps[0], 300f) : Unlimited;
            config.loadoutSeconds = Mathf.Clamp(config.loadoutSeconds, LoadoutSteps[0], LoadoutSteps[LoadoutSteps.Length - 1]);
        }

        /// <summary>从 <paramref name="current"/> 往上/下走一档。</summary>
        public static float Step(float[] steps, float current, int direction)
        {
            int nearest = -1;
            for (int i = 0; i < steps.Length; i++)
            {
                // 不限时（0）只和自己对应，它排在最长一档后面
                if (current <= 0f ? steps[i] <= 0f : steps[i] > 0f && (nearest < 0 || Math.Abs(steps[i] - current) < Math.Abs(steps[nearest] - current))) nearest = i;
            }
            if (nearest < 0) nearest = 0;
            return steps[Mathf.Clamp(nearest + direction, 0, steps.Length - 1)];
        }

        /// <summary>一行"每回合求解  不限时  [-][+]"，返回调整后的值（变了就已经保存）。</summary>
        public static float Row(string label, float[] steps, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, Ui.Small, GUILayout.ExpandWidth(false));
            GUILayout.Space(6);
            GUILayout.Label(L.Duration(value), Ui.Body, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            float next = value;
            bool lowest = steps[0] == value, highest = steps[steps.Length - 1] == value;
            GUI.enabled = !lowest;
            if (GUILayout.Button("−", Ui.Button, GUILayout.Width(30f))) next = Step(steps, value, -1);
            GUI.enabled = !highest;
            if (GUILayout.Button("+", Ui.Button, GUILayout.Width(30f))) next = Step(steps, value, +1);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            return next;
        }
    }
}
