using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Cheat
{
    [BepInPlugin("me.mengtl.GaiaTrekCheatMenu", "盖娅迷航内置修改器", "1.1.0")]
    public class GaiaTrekCheatMenu : BaseUnityPlugin
    {
        ConfigEntry<KeyCode> hotkey;
        ConfigEntry<int> targetFrameRate;
        private bool windowShow = false;
        private Rect windowRect = new Rect(50, 50, 300, 200);
        private static bool RangeBool = false;
        private static bool SpeedBool = false;
        private static bool ResourceBool = false;
        private static bool RecliBool = false;
        private static bool MoveSpeedBool = false;
        private static bool AddLevelBool = false;
        private static bool ResourceFreshBool = false;
        private static int hSliderValue;
        private static bool RelicFreshBool = false;
        private static int hSliderValue2;
        private static bool SkillFreshBool = false;
        private static int hSliderValue3;
        void Start()
        {
            hotkey = Config.Bind<KeyCode>("config", "hotkey", KeyCode.F9, "启动快捷键");
            targetFrameRate = Config.Bind<int>("config", "targetFrameRate", 120, "帧数");
            Application.targetFrameRate = targetFrameRate.Value;
            Harmony.CreateAndPatchAll(typeof(GaiaTrekCheatMenu));
            Debug.Log("插件加载完成！");
        }

        void Update()
        {
            var key = new KeyboardShortcut(KeyCode.F9);

            if (key.IsDown())
            {
                windowShow = !windowShow;
            }
        }
        void OnGUI()
        {
            if (windowShow)
            {
                windowRect = GUILayout.Window(123, windowRect, WindowFunc, "盖娅迷航内置修改器");
            }
        }
        public void WindowFunc(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("x", GUILayout.Width(25)))
            {
                windowShow = false;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("功能皆可随开随关，拆分功能即编辑资源");
            GUILayout.Label("当前解锁:"+targetFrameRate.Value.ToString()+"帧");
            RangeBool = GUILayout.Toggle(RangeBool, "无限距离");
            SpeedBool = GUILayout.Toggle(SpeedBool, "超级攻速");
            ResourceBool = GUILayout.Toggle(ResourceBool, "10倍获取资源卡");
            RecliBool = GUILayout.Toggle(RecliBool, "10倍获取装备卡");
            MoveSpeedBool = GUILayout.Toggle(MoveSpeedBool, "敌人龟速");
            AddLevelBool = GUILayout.Toggle(AddLevelBool, "怪物卡合成即为最高级");
            ResourceFreshBool = GUILayout.Toggle(ResourceFreshBool, "资源卡拆分");
            if (ResourceFreshBool)
            {
                GUILayout.Label("编辑资源卡数量:");
                hSliderValue = (int)GUILayout.HorizontalSlider(hSliderValue, 1, 500);
            }
            RelicFreshBool = GUILayout.Toggle(RelicFreshBool, "装备卡拆分");
            if (RelicFreshBool)
            {
                GUILayout.Label("编辑装备卡数量:");
                hSliderValue2 = (int)GUILayout.HorizontalSlider(hSliderValue2, 1, 50);
            }
            SkillFreshBool = GUILayout.Toggle(SkillFreshBool, "技能卡拆分");
            if (SkillFreshBool)
            {
                GUILayout.Label("编辑技能卡数量:");
                hSliderValue3 = (int)GUILayout.HorizontalSlider(hSliderValue3, 1, 50);
            }
            GUI.DragWindow();
        }
        //无限攻击距离
        [HarmonyPostfix, HarmonyPatch(typeof(MonsterCard), "GetAttackRange")]
        public static void MonsterCard_GetAttackRange_Postfix(ref float __result)
        {
            if (RangeBool)
            {
                __result = __result * 100f;
            }
        }
        //超级攻速
        [HarmonyPostfix, HarmonyPatch(typeof(MonsterCard), "GetAttackSpeed")]
        public static void MonsterCard_GetAttackSpeed_Postfix(ref float __result)
        {
            if (SpeedBool)
            {
                __result = __result * 0.1f;
            }
        }
        //敌人龟速
        [HarmonyPostfix, HarmonyPatch(typeof(MonsterCard), "GetSpeed")]
        public static void MonsterCard_GetSpeed_Postfix(ref float __result)
        {
            if (MoveSpeedBool)
            {
                __result = __result * 0.1f;
            }
        }
        //10倍获取资源卡
        [HarmonyPrefix, HarmonyPatch(typeof(ResourceCard), "AddCard")]
        public static bool ResourceCard_AddCard_Prefix(ResourceCard __instance, ResourceCard card)
        {
            if (ResourceBool)
            {
                __instance.count = card.count * 10 + __instance.count;
                return true;
            }
            else
            {
                return true;
            }
        }
        //10倍获取装备卡
        [HarmonyPrefix, HarmonyPatch(typeof(RelicCard), "AddCard")]
        public static bool RelicCard_AddCard_Prefix(RelicCard __instance, RelicCard card)
        {
            if (ResourceBool)
            {
                __instance.count = card.count * 10 + __instance.count;
                return true;
            }
            else
            {
                return true;
            }
        }
        //任意怪物卡合成即为最高级
        [HarmonyPrefix, HarmonyPatch(typeof(MonsterCard), "AddLevel")]
        public static bool MonsterCard_AddLevel_Prefix(ref int delta)
        {
            if (AddLevelBool)
            {
                delta = 9;
                return true;
            }
            else
            {
                return true;
            }
        }
        //资源卡拆分(编辑资源)
        [HarmonyPrefix, HarmonyPatch(typeof(ResourceCard), "Fresh")]
        public static void ResourceCard_Fresh_Prefix(ResourceCard __instance)
        {
            if (ResourceFreshBool)
            {
                __instance.count = hSliderValue;
            }
        }
        //装备卡拆分(编辑装备)
        [HarmonyPrefix, HarmonyPatch(typeof(RelicCard), "Fresh")]
        public static void RelicCard_Fresh_Prefix(RelicCard __instance)
        {
            if (RelicFreshBool)
            {
                __instance.count = hSliderValue2;
            }
        }
        //技能卡拆分
        [HarmonyPrefix, HarmonyPatch(typeof(SkillCard), "Fresh")]
        public static void SkillCard_Fresh_Prefix(SkillCard __instance)
        {
            if (SkillFreshBool)
            {
                __instance.count = hSliderValue3;
            }
        }
    }
}
