using System;
using Combat;
using World;
using Card;
using Collection;
using Relic;
using UnityEngine.Localization;

namespace DICEOMANCERCheatMenu
{
    // 与游戏数据交互：读取卡组、缓存图鉴数据、执行复制/删除/添加等操作。
    // 对外统一使用 object，界面代码无需引用游戏类型。
    // 操作失败时抛出 InvalidOperationException，消息会直接显示在界面状态栏上。
    internal static class CheatActions
    {
        // 图鉴名称的本地化查询开销较大，按对象缓存
        private static readonly System.Collections.Generic.Dictionary<object, string> nameCache =
            new System.Collections.Generic.Dictionary<object, string>();

        public static object[] CardCollection { get; private set; } = Array.Empty<object>();
        public static int CardCollectionVersion { get; private set; }
        public static object[] RelicCollection { get; private set; } = Array.Empty<object>();
        public static int RelicCollectionVersion { get; private set; }

        public static void SetCardCollection(CardSo[] cards)
        {
            CardCollection = cards ?? Array.Empty<CardSo>();
            CardCollectionVersion++;
        }

        public static void SetRelicCollection(RelicSo[] relics)
        {
            RelicCollection = relics ?? Array.Empty<RelicSo>();
            RelicCollectionVersion++;
        }

        public static void ClearNameCache()
        {
            nameCache.Clear();
            CardCollectionVersion++;
            RelicCollectionVersion++;
        }

        public static object[] GetDeck(out bool inRun)
        {
            try
            {
                CardManager manager = CardManager.Instance;
                inRun = manager != null;
                if (!inRun) return Array.Empty<object>();
                return manager.GetAllPlayerCards() ?? Array.Empty<CardBase>();
            }
            catch (Exception)
            {
                inRun = false;
                return Array.Empty<object>();
            }
        }

        public static string GetDeckCardName(object item)
        {
            CardBase card = item as CardBase;
            if (card == null || card.cardNameTextMeshPro == null) return null;
            return card.cardNameTextMeshPro.text;
        }

        public static string GetCollectionCardName(object item)
        {
            CardSo card = item as CardSo;
            if (card == null) return null;

            string name;
            if (nameCache.TryGetValue(card, out name)) return name;
            try
            {
                name = new LocalizedString("CardName", card.CardInfo.cardName).GetLocalizedString();
            }
            catch (Exception)
            {
                name = null;
            }
            if (!string.IsNullOrEmpty(name))
            {
                nameCache[card] = name;
                return name;
            }

            // 本地化表尚未加载时先显示原始名称，且不缓存，下次刷新再重试
            try
            {
                return Convert.ToString(card.CardInfo.cardName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string GetRelicName(object item)
        {
            RelicSo relic = item as RelicSo;
            if (relic == null) return null;

            string name;
            if (nameCache.TryGetValue(relic, out name)) return name;
            try
            {
                name = new LocalizedString("Relic", relic.relicInfo.relicName).GetLocalizedString();
            }
            catch (Exception)
            {
                name = null;
            }
            if (!string.IsNullOrEmpty(name))
            {
                nameCache[relic] = name;
                return name;
            }

            // 本地化表尚未加载时先显示原始名称，且不缓存，下次刷新再重试
            try
            {
                return Convert.ToString(relic.relicInfo.relicName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        //卡牌复制
        public static string CopyCard(object item)
        {
            CardBase card = item as CardBase;
            if (card == null) throw new InvalidOperationException("卡牌已失效，请刷新卡组");
            RequireCardManager().AddCardToPlayerCards(card);
            return "已复制卡牌：" + (GetDeckCardName(card) ?? "未知卡牌");
        }

        //卡牌删除
        public static string RemoveCard(object item)
        {
            CardBase card = item as CardBase;
            if (card == null) throw new InvalidOperationException("卡牌已失效，请刷新卡组");
            string name = GetDeckCardName(card) ?? "未知卡牌";
            RequireCardManager().RemoveCardFromGame(card);
            return "已删除卡牌：" + name;
        }

        //图鉴添加卡牌
        public static string AddCollectionCard(object item)
        {
            CardSo card = item as CardSo;
            if (card == null) throw new InvalidOperationException("卡牌数据无效，请重新打开卡牌图鉴");
            RequireCardManager().GenerateCardObjFromCardInfo(card.CardInfo, OwnerShip.Player);
            return "已添加卡牌：" + (GetCollectionCardName(card) ?? "未知卡牌");
        }

        //图鉴添加遗物
        public static string AddRelic(object item)
        {
            RelicSo relic = item as RelicSo;
            if (relic == null) throw new InvalidOperationException("遗物数据无效，请重新打开遗物图鉴");
            RequireRelicManager().GenerateAndGainNewRelic(relic.relicInfo);
            return "已获得遗物：" + (GetRelicName(relic) ?? "未知遗物");
        }

        //获得当前图鉴分类下的全部遗物
        public static string AddAllRelics()
        {
            RelicManager manager = RequireRelicManager();
            int added = 0;
            int failed = 0;
            foreach (object item in RelicCollection)
            {
                RelicSo relic = item as RelicSo;
                if (relic == null) continue;
                try
                {
                    manager.GenerateAndGainNewRelic(relic.relicInfo);
                    added++;
                }
                catch (Exception e)
                {
                    failed++;
                    DICEOMANCERCheatMenu.Log.LogWarning($"获得遗物失败：{e.Message}");
                }
            }

            if (added == 0 && failed == 0) throw new InvalidOperationException("没有可获得的遗物，请先打开遗物图鉴");
            return failed == 0 ? $"已获得 {added} 件遗物" : $"已获得 {added} 件遗物，{failed} 件失败（详见日志）";
        }

        private static CardManager RequireCardManager()
        {
            CardManager manager = CardManager.Instance;
            if (manager == null) throw new InvalidOperationException("请先进入游戏对局");
            return manager;
        }

        private static RelicManager RequireRelicManager()
        {
            RelicManager manager = RelicManager.Instance;
            if (manager == null) throw new InvalidOperationException("请先进入游戏对局");
            return manager;
        }
    }
}
