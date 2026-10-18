using System;
using BepInEx.Logging;
using HarmonyLib;
using Combat;
using World;
using Card;
using Collection;
using Relic;

namespace DICEOMANCERCheatMenu
{
    // 逐个应用补丁：游戏更新导致某个方法失效时，只会禁用对应功能，不影响其他功能
    internal static class CheatPatches
    {
        private static readonly Type[] PatchTypes =
        {
            typeof(InfiniteManaPatch),
            typeof(MoneyPatch),
            typeof(LoseLifePatch),
            typeof(AnyManaColorPatch),
            typeof(AutoUpgradePatch),
            typeof(CardUnlockPatch),
            typeof(RelicUnlockPatch),
            typeof(CardCollectionCapturePatch),
            typeof(RelicPageCapturePatch),
            typeof(RelicRarityCapturePatch)
        };

        private static readonly System.Collections.Generic.HashSet<Type> failedPatches =
            new System.Collections.Generic.HashSet<Type>();

        public static void ApplyAll(Harmony harmony, ManualLogSource log)
        {
            foreach (Type type in PatchTypes)
            {
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    failedPatches.Add(type);
                    log.LogError($"补丁 {type.Name} 应用失败，对应功能已禁用：{e}");
                }
            }

            CheatSettings.AutoUpgradeCards.SettingChanged += (sender, args) => AutoUpgradePatch.Reset();
        }

        public static bool IsAvailable(Type patchType)
        {
            return !failedPatches.Contains(patchType);
        }
    }

    //魔力不减
    [HarmonyPatch(typeof(ManaManager), "PayManaToManaCosts")]
    internal static class InfiniteManaPatch
    {
        private static bool Prefix()
        {
            return !CheatSettings.InfiniteMana.Value;
        }
    }

    //金币不减 / 金币不减反增（合并为一个补丁，避免两个开关同时打开时互相抵消）
    [HarmonyPatch(typeof(MoneyManager), "LoseMoney")]
    internal static class MoneyPatch
    {
        private static void Prefix(ref int moneyLost)
        {
            switch (CheatSettings.Money.Value)
            {
                case MoneyMode.NoLoss:
                    moneyLost = 0;
                    break;
                case MoneyMode.Gain:
                    moneyLost = -moneyLost;
                    break;
            }
        }
    }

    //生命不减与伤害倍率
    [HarmonyPatch(typeof(Character), "LoseLife")]
    internal static class LoseLifePatch
    {
        private static void Prefix(ref int amount, Damage damageSource, Character __instance)
        {
            if (IsPlayer(__instance, damageSource))
            {
                if (CheatSettings.Invincible.Value) amount = 0;
            }
            else if (CheatSettings.DamageMultiplierEnabled.Value)
            {
                // 倍率只作用于敌人，玩家受到的伤害不会被放大
                amount *= CheatSettings.DamageMultiplier.Value;
            }
        }

        private static bool IsPlayer(Character character, Damage damageSource)
        {
            if (character is CharacterPlayer) return true;
            return !ReferenceEquals(damageSource, null) && damageSource.TargetCharacter is CharacterPlayer;
        }
    }

    //卡牌能量种类无要求（对之后生成的卡牌生效）
    [HarmonyPatch(typeof(CardManager), "GenerateCardObjFromCardInfo", new Type[] { typeof(CardInfo), typeof(OwnerShip) })]
    internal static class AnyManaColorPatch
    {
        private static void Prefix(ref CardInfo cardInfo)
        {
            if (!CheatSettings.AnyManaColor.Value || ReferenceEquals(cardInfo, null)) return;

            var costs = cardInfo.cardManaCostColorEArrays;
            if (costs == null) return;
            for (int i = 0; i < costs.Length; i++)
            {
                if (ReferenceEquals(costs[i], null)) continue;
                costs[i].colorEs = new ColorE[] { ColorE.Void };
            }
        }
    }

    //卡牌自动强化
    [HarmonyPatch(typeof(CardManager), "GetAllPlayerCards")]
    internal static class AutoUpgradePatch
    {
        // 记录已经强化过的卡牌，避免每次读取卡组都对所有卡牌重复调用强化
        private static readonly System.Collections.Generic.HashSet<CardBase> upgraded =
            new System.Collections.Generic.HashSet<CardBase>();
        private static bool upgrading;

        public static void Reset()
        {
            upgraded.Clear();
        }

        private static void Postfix(CardBase[] __result, CardManager __instance)
        {
            if (!CheatSettings.AutoUpgradeCards.Value || __result == null || upgrading) return;

            if (upgraded.Count > 1024) upgraded.RemoveWhere(IsDestroyed);
            upgrading = true;
            try
            {
                foreach (CardBase card in __result)
                {
                    if (card == null || !upgraded.Add(card)) continue;
                    try
                    {
                        __instance.UpgradeCard(card);
                    }
                    catch (Exception e)
                    {
                        DICEOMANCERCheatMenu.Log.LogWarning($"卡牌强化失败：{e.Message}");
                    }
                }
            }
            finally
            {
                upgrading = false;
            }
        }

        private static bool IsDestroyed(CardBase card)
        {
            return card == null;
        }
    }

    //卡牌图鉴全解锁
    [HarmonyPatch(typeof(CardUtils), "IsCardDiscovered")]
    internal static class CardUnlockPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!CheatSettings.UnlockCardCollection.Value) return true;
            // 将返回值设置为true，并跳过原方法执行
            __result = true;
            return false;
        }
    }

    //遗物图鉴全解锁
    [HarmonyPatch(typeof(SubCollectionRelics), "IsRelicDiscovered")]
    internal static class RelicUnlockPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!CheatSettings.UnlockRelicCollection.Value) return true;
            __result = true;
            return false;
        }
    }

    //打开卡牌图鉴时记录当前页的卡牌
    [HarmonyPatch(typeof(SubCollectionCards), "ShowCardsInThisPage")]
    internal static class CardCollectionCapturePatch
    {
        private static void Postfix(SubCollectionCards __instance)
        {
            CheatActions.SetCardCollection(
                Traverse.Create(__instance).Field("_currentBookmarkIndexCards").GetValue<CardSo[]>());
        }
    }

    //打开遗物图鉴时记录当前分类的遗物
    [HarmonyPatch(typeof(SubCollectionRelics), "ShowRelicsInThisPage")]
    internal static class RelicPageCapturePatch
    {
        private static void Postfix(SubCollectionRelics __instance)
        {
            CheatActions.SetRelicCollection(
                Traverse.Create(__instance).Field("_currentRarityRelics").GetValue<RelicSo[]>());
        }
    }

    [HarmonyPatch(typeof(SubCollectionRelics), "GetCurrentPageRelics")]
    internal static class RelicRarityCapturePatch
    {
        private static void Postfix(SubCollectionRelics __instance)
        {
            CheatActions.SetRelicCollection(
                Traverse.Create(__instance).Field("_currentRarityRelics").GetValue<RelicSo[]>());
        }
    }
}
