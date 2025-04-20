using System;
using BepInEx;
using BepInEx.Unity.Mono;
using UnityEngine;
using HarmonyLib;
using Combat;
using World;
using Card;
using BepInEx.Configuration;
using Collection;
using Relic;
using UnityEngine.Localization;

namespace DICEOMANCERCheatMenu
{
    [BepInPlugin("me.mengtl.plugin.cheatmenu", "DICEOMANCERCheatMenu", "2.0.0")]
    public class DICEOMANCERCheatMenu : BaseUnityPlugin
    {
        ConfigEntry<KeyCode> hotkey;
        private bool windowShow = false;
        void Start()
        {
            hotkey = Config.Bind<KeyCode>("config", "hotkey", KeyCode.F9, "快捷键");
            Harmony.CreateAndPatchAll(typeof(DICEOMANCERCheatMenu));
            Logger.LogInfo("插件启动成功!");
        }
        void Update()
        {
            if (Input.GetKeyDown(hotkey.Value))
            {
                windowShow = !windowShow;
            }
        }

        private Rect windowRect = new Rect(50, 50, 500, 400);
        private Vector2 scrollPosition;
        private Vector2 scrollPositionCardCollection;
        private Vector2 scrollPositionRelicCollection;
        private static bool bool_1 = false;
        private static bool bool_2 = false;
        private static bool bool_3 = false;
        private static bool bool_4 = false;
        private static bool bool_5 = false;
        private static int multiple_int = 1;
        private static bool bool_6 = false;
        private static bool bool_7 = false;
        private static bool bool_8 = false;
        private static string card_copy_id = "";
        private static bool bool_9 = false;
        private static string card_remove_id = "";
        private static bool bool_10 = true;
        private static bool bool_11 = true;
        private static CardBase[] cardBase = Array.Empty<CardBase>();//存储卡组变量
        private static CardSo[] cardSo = Array.Empty<CardSo>();//存储卡牌图鉴变量
        private static bool bool_12 = false;
        private static string card_collection_id = "";
        private static RelicSo[] relicSo = Array.Empty<RelicSo>();//存储遗物图鉴变量
        private static bool bool_13 = false;
        private static string relic_collection_id = "";
        private static bool bool_14 = false;
        private static RelicSo[] relicSoAll = Array.Empty<RelicSo>();//存储所有遗物图鉴变量
        void OnGUI()
        {
            if (windowShow)
            {
                windowRect = GUILayout.Window(123, windowRect, WindowFunc, "骰子浪游者内置修改器");
            }
        }
        void WindowFunc(int id)
        {
            GUILayout.BeginHorizontal(); // 主水平布局
            GUILayout.BeginVertical(GUILayout.Width(250)); // 左侧功能区域

            GUILayout.BeginHorizontal();
            GUILayout.Label("功能列表:");
            GUILayout.EndHorizontal();

            bool_1 = GUILayout.Toggle(bool_1, "魔力不减");
            bool_2 = GUILayout.Toggle(bool_2, "金币不减");
            bool_3 = GUILayout.Toggle(bool_3, "金币不减反增");
            bool_4 = GUILayout.Toggle(bool_4, "生命不减");
            bool_5 = GUILayout.Toggle(bool_5, "倍功");
            if (bool_5)
            {
                multiple_int = (int)GUILayout.HorizontalSlider(multiple_int, 1, 5);
                GUILayout.Label("倍数:" + multiple_int);
            }
            bool_6 = GUILayout.Toggle(bool_6, "卡牌能量种类无要求");
            bool_7 = GUILayout.Toggle(bool_7, "卡牌自动强化");
            bool_8 = GUILayout.Toggle(bool_8, "卡牌复制");
            if (bool_8)
            {
                GUILayout.Label("需要复制的卡牌编号:");
                card_copy_id = GUILayout.TextField(card_copy_id);
                if (GUILayout.Button("复制")) CopyCard();
            }
            bool_9 = GUILayout.Toggle(bool_9, "卡牌删除");
            if (bool_9)
            {
                GUILayout.Label("需要删除的卡牌编号:");
                card_remove_id = GUILayout.TextField(card_remove_id);
                if (GUILayout.Button("删除")) DelCard();
            }
            bool_10 = GUILayout.Toggle(bool_10, "卡牌图鉴全解锁");
            bool_11 = GUILayout.Toggle(bool_11, "遗物图鉴全解锁");
            bool_12 = GUILayout.Toggle(bool_12, "卡牌添加(图鉴)");
            if (bool_12)
            {
                GUILayout.Label("需要添加的卡牌编号:");
                card_collection_id = GUILayout.TextField(card_collection_id);
                if (GUILayout.Button("添加")) AddCard();
            }

            bool_13 = GUILayout.Toggle(bool_13, "遗物添加(图鉴)");
            if (bool_13)
            {
                GUILayout.Label("需要添加的遗物编号:");
                relic_collection_id = GUILayout.TextField(relic_collection_id);
                if (GUILayout.Button("添加")) AddRelic();
            }

            bool_14 = GUILayout.Toggle(bool_14, "获取全遗物(图鉴)");
            if (bool_14)
            {
                if (GUILayout.Button("获取")) AddAllRelics();
            }


            GUILayout.EndVertical(); // 结束左侧垂直布局

            // 右侧卡组显示面板
            GUILayout.BeginVertical(GUILayout.Width(250));
            if (bool_12)
            {
                DisplayCardCollectionPanel();
            }
            else if (bool_13)
            {
                DisplayRelicCollectionPanel();
            }
            else
            {
                DisplayCardDeckPanel();
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal(); // 结束主水平布局
            GUI.DragWindow();
        }

        // 新增卡组显示方法
        private void DisplayCardDeckPanel()
        {
            //更新卡组索引
            cardBase = CardManager.Instance.GetAllPlayerCards();
            GUILayout.Label("当前卡组列表（共 " + cardBase.Length + " 张）");
            scrollPosition = GUILayout.BeginScrollView(scrollPosition,
                GUILayout.Height(300)); // 限制高度

            if (cardBase != null && cardBase.Length > 0)
            {
                for (int i = 0; i < cardBase.Length; i++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"No.{i + 1}", GUILayout.Width(50));
                    if (cardBase[i] != null && cardBase[i].cardNameTextMeshPro.text != null)
                    {
                        GUILayout.Label(cardBase[i].cardNameTextMeshPro.text);
                    }
                    else
                    {
                        GUILayout.Label("-- 无效卡牌 --");
                    }
                    GUILayout.EndHorizontal();
                }
            }
            else
            {
                GUILayout.Label("当前没有卡牌");
            }

            GUILayout.EndScrollView();
        }
        // 新增卡牌图鉴显示方法
        private void DisplayCardCollectionPanel()
        {
            GUILayout.Label($"卡牌图鉴列表（共 {cardSo.Length} 张）");
            scrollPositionCardCollection = GUILayout.BeginScrollView(scrollPositionCardCollection,
                GUILayout.Height(300));

            if (cardSo != null && cardSo.Length > 0)
            {
                for (int i = 0; i < cardSo.Length; i++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"No.{i + 1}", GUILayout.Width(50));
                    if (cardSo[i] != null)
                    {
                        var cardNameCN = new LocalizedString("CardName", cardSo[i].CardInfo.cardName).GetLocalizedString();
                        GUILayout.Label(cardNameCN);
                    }
                    else
                    {
                        GUILayout.Label("-- 无效卡牌 --");
                    }
                    GUILayout.EndHorizontal();
                }
            }
            else
            {
                GUILayout.Label("请先在游戏中打开卡牌图鉴页面加载数据");
            }

            GUILayout.EndScrollView();
        }

        // 新增遗物图鉴显示方法
        private void DisplayRelicCollectionPanel()
        {
            GUILayout.Label($"遗物图鉴列表（共 {relicSo.Length} 件）");
            scrollPositionRelicCollection = GUILayout.BeginScrollView(scrollPositionRelicCollection,
                GUILayout.Height(300));

            if (relicSo != null && relicSo.Length > 0)
            {
                for (int i = 0; i < relicSo.Length; i++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"No.{i + 1}", GUILayout.Width(50));
                    if (relicSo[i] != null)
                    {
                        var relicNameCN = new LocalizedString("Relic", relicSo[i].relicInfo.relicName).GetLocalizedString();
                        GUILayout.Label(relicNameCN);
                    }
                    else
                    {
                        GUILayout.Label("-- 无效遗物 --");
                    }
                    GUILayout.EndHorizontal();
                }
            }
            else
            {
                GUILayout.Label("请先在游戏中打开遗物图鉴页面加载数据");
            }

            GUILayout.EndScrollView();
        }

        //魔力不减
        [HarmonyPrefix, HarmonyPatch(typeof(ManaManager), "PayManaToManaCosts")]
        public static bool PayManaToManaCosts()
        {
            if (bool_1)
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        //金币不减
        [HarmonyPrefix, HarmonyPatch(typeof(MoneyManager), "LoseMoney")]
        public static void LoseMoney(ref int moneyLost)
        {
            if (bool_2)
            {
                moneyLost = 0;
            }
        }

        //金币不减反增
        [HarmonyPrefix, HarmonyPatch(typeof(MoneyManager), "LoseMoney")]
        public static void LoseMoney2(ref int moneyLost, MoneyManager __instance)
        {
            if (bool_3)
            {
                moneyLost = -moneyLost;
            }
        }

        //生命不减与倍功
        [HarmonyPrefix, HarmonyPatch(typeof(Character), "LoseLife")]
        public static void LoseLife(ref int amount, Damage damageSource, Character __instance)
        {
            if (bool_4)
            {
                if (damageSource.TargetCharacter is CharacterPlayer)
                {
                    amount = 0;
                }
            }
            if (bool_5)
            {
                amount = amount * multiple_int;
            }
        }

        //卡牌能量种类无要求
        [HarmonyPrefix, HarmonyPatch(typeof(CardManager), "GenerateCardObjFromCardInfo", new Type[] { typeof(CardInfo), typeof(OwnerShip) })]
        public static bool GenerateCardObjFromCardInfo(ref CardInfo cardInfo)
        {
            if (bool_6)
            {
                for (int i = 0; i < cardInfo.cardManaCostColorEArrays.Length; i++)
                {
                    cardInfo.cardManaCostColorEArrays[i].colorEs = new ColorE[] { ColorE.Void };
                }
            }
            return true;
        }

        //卡牌自动强化
        [HarmonyPostfix, HarmonyPatch(typeof(CardManager), "GetAllPlayerCards")]
        public static void GetAllPlayerCards(CardBase[] __result, CardManager __instance)
        {
            if (bool_7)
            {
                foreach (CardBase card in __result)
                {
                    __instance.UpgradeCard(card);
                }
            }
        }

        //卡牌一键复制(调用)
        public static void CopyCard()
        {
            CardManager.Instance.AddCardToPlayerCards(cardBase[int.Parse(card_copy_id) - 1]);
        }

        //卡牌一键删除(调用)
        public static void DelCard()
        {
            CardManager.Instance.RemoveCardFromGame(cardBase[int.Parse(card_remove_id) - 1]);
        }

        //卡牌图鉴全解锁
        [HarmonyPrefix, HarmonyPatch(typeof(CardUtils), "IsCardDiscovered")]
        public static bool IsCardDiscovered(ref bool __result)
        {
            if (bool_10)
            {
                // 将返回值设置为true，并跳过原方法执行
                __result = true;
                return false;
            }
            return true;
        }

        //遗物全解锁
        [HarmonyPrefix, HarmonyPatch(typeof(SubCollectionRelics), "IsRelicDiscovered")]
        public static bool IsRelicDiscovered(ref bool __result)
        {
            if (bool_11)
            {
                // 将返回值设置为true，并跳过原方法执行
                __result = true;
                return false;
            }
            return true;
        }

        //根据图鉴添加卡牌变量
        [HarmonyPostfix, HarmonyPatch(typeof(SubCollectionCards), "ShowCardsInThisPage")]
        public static void GeneratePlayerCard(SubCollectionCards __instance)
        {
            cardSo = Traverse.Create(__instance).Field("_currentBookmarkIndexCards").GetValue<CardSo[]>();
        }
        //图鉴添加卡牌
        public static void AddCard()
        {
            CardManager.Instance.GenerateCardObjFromCardInfo(cardSo[int.Parse(card_collection_id) - 1].CardInfo, OwnerShip.Player);
        }

        //根据图鉴添加遗物变量
        [HarmonyPostfix, HarmonyPatch(typeof(SubCollectionRelics), "ShowRelicsInThisPage")]
        public static void GeneratePlayerRelic(SubCollectionRelics __instance)
        {
            relicSo = Traverse.Create(__instance).Field("_currentRarityRelics").GetValue<RelicSo[]>();
        }
        //图鉴添加遗物
        public static void AddRelic()
        {
            RelicManager.Instance.GenerateAndGainNewRelic(relicSo[int.Parse(relic_collection_id) - 1].relicInfo);
        }

        //添加全遗物到变量
        [HarmonyPostfix, HarmonyPatch(typeof(SubCollectionRelics), "GetCurrentPageRelics")]
        public static void GenerateAllPlayerRelic(SubCollectionRelics __instance)
        {
            relicSoAll = Traverse.Create(__instance).Field("_currentRarityRelics").GetValue<RelicSo[]>();
        }
        //添加全遗物
        public static void AddAllRelics()
        {
            if (bool_14)
            {
                foreach(var relic in relicSoAll)
                {
                    RelicManager.Instance.GenerateAndGainNewRelic(relic.relicInfo);
                }
            }
        }
    }
}
