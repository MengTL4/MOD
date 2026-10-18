using BepInEx;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Talented
{
    [BepInPlugin("me.mengtl.TalentedMenu", "TalentedModMenu", "1.0.0")]
    public class TalentedMenu : BaseUnityPlugin
    {
        // GUI控制
        private bool showGUI = true;
        private Rect windowRect = new Rect(20, 20, 180, 150);

        // 设置变量
        public static bool ForceAutoAimEnabled = true;
        public static int TalentMultiplier = 1;
        public static bool DlcUnlockEnabled = true;
        private static bool autoAimOverrideApplied;

        void Start()
        {
            Harmony.CreateAndPatchAll(typeof(TalentedMenu));
            Logger.LogInfo("TalentedMenu: Patch 已加载");
        }

        void Update()
        {
            // 可以加快捷键切换GUI显示
            if (Input.GetKeyDown(KeyCode.F1))
            {
                showGUI = !showGUI;
            }
        }

        void OnGUI()
        {
            if (showGUI)
            {
                windowRect = GUI.Window(0, windowRect, DrawWindow, "TalentedModMenu");
            }
        }

        void DrawWindow(int windowID)
        {
            GUILayout.BeginVertical();

            // Force AutoAim 开关
            ForceAutoAimEnabled = GUILayout.Toggle(ForceAutoAimEnabled, "自动瞄准");

            // TalentMultiplier 滑条
            GUILayout.Label("TalentPointsSpent倍率: " + TalentMultiplier);
            GUILayout.Label("数值越高，天赋点可用越多");
            TalentMultiplier = (int)GUILayout.HorizontalSlider(TalentMultiplier, 1, 10);

            DlcUnlockEnabled = GUILayout.Toggle(DlcUnlockEnabled, "DLC解锁");

            GUILayout.EndVertical();

            // 允许拖拽窗口
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        [HarmonyPrefix, HarmonyPatch(typeof(InputManager), "Update")]
        public static void ForceAutoAimBeforeUpdate(InputManager __instance)
        {
            ApplyAutoAimOverride(__instance);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(InputManager), "Update")]
        public static void ForceAutoAimAfterUpdate(InputManager __instance)
        {
            ApplyAutoAimOverride(__instance);
        }

        private static void ApplyAutoAimOverride(InputManager inputManager)
        {
            if (inputManager == null) return;

            if (ForceAutoAimEnabled)
            {
                inputManager.SpecialAutoAimEnabled = true;
                inputManager.DirectionalInputDisabled = false;
                autoAimOverrideApplied = true;
            }
            else if (autoAimOverrideApplied)
            {
                inputManager.SpecialAutoAimEnabled = false;
                autoAimOverrideApplied = false;
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(RunManager), "get_TalentPointsRemaining")]
        public static bool TalentPointsRemain(RunManager __instance, ref int __result)
        {
            if (!__instance.CharacterSheet.CanGainTalentPoints)
            {
                __result = 0;
                return false; // 阻止原方法执行
            }

            // 使用可调倍率
            int b = __instance.Level - __instance.TalentPointsSpent / TalentMultiplier - 1;
            __result = Mathf.Max(0, b);
            return false; // 阻止原方法执行
        }

        [HarmonyPostfix, HarmonyPatch(typeof(DLCManager), "TryInitialiseDLC")]
        public static void Patch_TryInitialiseDLC(DLCManager __instance)
        {
            // 仅当启用时解锁
            if (!DlcUnlockEnabled) return;

            // 使用 AccessTools 获取私有字段 activeDLCTypes
            var activeListField = AccessTools.Field(typeof(DLCManager), "activeDLCTypes");
            if (activeListField == null) return;

            var activeList = (List<DLCType>)activeListField.GetValue(__instance);
            if (activeList == null) return;

            // 使用 AccessTools 获取静态私有字段 DLCAppIDs
            var dlcDictField = AccessTools.Field(typeof(DLCManager), "DLCAppIDs");
            if (dlcDictField == null) return;

            var dlcDict = (Dictionary<DLCType, uint>)dlcDictField.GetValue(null); // null 因为是静态字段
            if (dlcDict == null) return;

            // 遍历字典的键（DLCType），动态添加所有DLC到active列表，避免重复
            foreach (var kvp in dlcDict)
            {
                var dlcType = kvp.Key;
                if (!activeList.Contains(dlcType))
                {
                    activeList.Add(dlcType);
                }
            }
        }

    }
}
