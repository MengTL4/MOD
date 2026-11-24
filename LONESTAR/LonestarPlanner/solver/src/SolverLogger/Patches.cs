#if HEADLESS
extern alias RealHarmony;
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
#if HEADLESS
using RealHarmony::HarmonyLib;
#else
using HarmonyLib;
#endif

namespace SolverLogger
{
    internal static class Snap
    {
        public static List<int> Powers(List<PowerWeight> list) => list?.Select(w => w.power).ToList();

        public static List<List<int>> Colors(List<PowerColorWeight> list) =>
            list?.Select(w => Colors(w.powerColors)).ToList();

        public static List<int> Colors(PowerColors colors) =>
            colors?.colorList.Select(c => (int)c).ToList();

        public static readonly AccessTools.FieldRef<PowerManager, List<EnergyData>> HandPowers =
            AccessTools.FieldRefAccess<PowerManager, List<EnergyData>>("handPowers");

        public static List<object> Hand()
        {
            var hand = HandPowers(Singleton<PowerManager>.Instance());
            return hand.Select(e => (object)new List<object> { e.power, Colors(e.powerColors) }).ToList();
        }

        public static object[] Deck()
        {
            PowerManager pm = Singleton<PowerManager>.Instance();
            return new object[]
            {
                "deck", Powers(pm.powerCore.powerWeights),
                "deck_cd", Powers(pm.powerCore.powerWeightsCD),
                "cdeck", Colors(pm.powerColorCore.powerColorWeights),
                "cdeck_cd", Colors(pm.powerColorCore.powerColorWeightsCD),
            };
        }

        public static object[] Concat(object[] a, params object[] b) => a.Concat(b).ToArray();

        /// <summary>Names the game methods that requested this random number (skipping RNG plumbing).</summary>
        public static List<string> Callers()
        {
            var result = new List<string>();
            foreach (StackFrame frame in new StackTrace(2, false).GetFrames() ?? new StackFrame[0])
            {
                var m = frame.GetMethod();
                if (m == null)
                {
                    continue;
                }
                string type = m.DeclaringType?.Name ?? "?";
                if (type.StartsWith("Patch_") || type == "WantedProcess")
                {
                    continue;
                }
                result.Add(type + "." + m.Name);
                if (result.Count >= 4)
                {
                    break;
                }
            }
            return result;
        }
    }

    // ---- RNG streams -------------------------------------------------------------------------

    [HarmonyPatch(typeof(WantedProcess), nameof(WantedProcess.GetStateByKey))]
    internal static class Patch_GetStateByKey
    {
        private static void Postfix(WantedProcess __instance, string key, int __result)
        {
            try
            {
                WantedPhases phases = __instance.wantedPhases;
                Log.Write("state",
                    "key", key,
                    "ret", __result,
                    "in_battle", Singleton<BattleManager>.Instance().inBattle,
                    "battle_states", BattleTempData.battle_states != null,
                    "seed", __instance.game_seed,
                    "small", phases?.currentSmallPhase,
                    "big", phases?.currentBigPhase,
                    "boss", __instance.onBossSuccessive,
                    "boss_idx", HistoryBossFightProcess.current,
                    "by", Snap.Callers());
            }
            catch (Exception ex)
            {
                Log.Error("GetStateByKey", ex);
            }
        }
    }

    [HarmonyPatch(typeof(Utils.UtilsClass), nameof(Utils.UtilsClass.RandomRange), typeof(int), typeof(int), typeof(string))]
    internal static class Patch_RandomRangeInt
    {
        private static void Postfix(int minInclusive, int maxExclusive, string key, int __result) =>
            Log.Write("rand_int", "min", minInclusive, "max", maxExclusive, "key", key, "ret", __result);
    }

    [HarmonyPatch(typeof(Utils.UtilsClass), nameof(Utils.UtilsClass.RandomRange), typeof(float), typeof(float), typeof(string))]
    internal static class Patch_RandomRangeFloat
    {
        private static void Postfix(float minInclusive, float maxInclusive, string key, float __result) =>
            Log.Write("rand_float", "min", minInclusive, "max", maxInclusive, "key", key,
                "ret_bits", BitConverter.ToInt32(BitConverter.GetBytes(__result), 0));
    }

    [HarmonyPatch(typeof(BattleTempData), nameof(BattleTempData.TempSave))]
    internal static class Patch_TempSave
    {
        private static void Postfix()
        {
            // Opens the battle file first so the reset marker is part of it.
            Recorder.Begin();
            Log.Write("battle_states_reset");
        }
    }

    // ---- Energy deck -------------------------------------------------------------------------

    [HarmonyPatch(typeof(PowerCore), nameof(PowerCore.RanmdomOrder))]
    internal static class Patch_PowerCoreShuffle
    {
        private static void Prefix(List<PowerWeight> target) => Log.Write("deck_shuffle_pre", "which", "power", "list", Snap.Powers(target));

        private static void Postfix(List<PowerWeight> target) => Log.Write("deck_shuffle_post", "which", "power", "list", Snap.Powers(target));
    }

    [HarmonyPatch(typeof(PowerColorCore), nameof(PowerColorCore.RanmdomOrder))]
    internal static class Patch_PowerColorCoreShuffle
    {
        private static void Prefix(List<PowerColorWeight> target) => Log.Write("deck_shuffle_pre", "which", "color", "list", Snap.Colors(target));

        private static void Postfix(List<PowerColorWeight> target) => Log.Write("deck_shuffle_post", "which", "color", "list", Snap.Colors(target));
    }

    [HarmonyPatch(typeof(PowerManager), nameof(PowerManager.GetPowerNumByPowerCore))]
    internal static class Patch_DrawPower
    {
        private static void Prefix(PowerManager __instance)
        {
            if (__instance.powerCore.powerWeights.Count == 0)
            {
                Log.Write("deck_reshuffle_pre", "which", "power", "cd", Snap.Powers(__instance.powerCore.powerWeightsCD));
            }
        }

        private static void Postfix(PowerManager __instance, int __result)
        {
            Log.Write("draw", "which", "power", "val", __result,
                "deck", Snap.Powers(__instance.powerCore.powerWeights),
                "cd", Snap.Powers(__instance.powerCore.powerWeightsCD));
        }
    }

    [HarmonyPatch(typeof(PowerManager), nameof(PowerManager.GetPowerColorByPowerCore))]
    internal static class Patch_DrawColor
    {
        private static void Prefix(PowerManager __instance)
        {
            if (__instance.powerColorCore.powerColorWeights.Count == 0)
            {
                Log.Write("deck_reshuffle_pre", "which", "color", "cd", Snap.Colors(__instance.powerColorCore.powerColorWeightsCD));
            }
        }

        private static void Postfix(PowerManager __instance, PowerColors __result)
        {
            Log.Write("draw", "which", "color", "val", Snap.Colors(__result),
                "deck", Snap.Colors(__instance.powerColorCore.powerColorWeights),
                "cd", Snap.Colors(__instance.powerColorCore.powerColorWeightsCD));
        }
    }

    [HarmonyPatch(typeof(PowerManager), nameof(PowerManager.Create))]
    internal static class Patch_CreateEnergy
    {
        private static void Postfix(int power, PowerColors powerColors, bool fromCore, EnergyData __result)
        {
            Log.Write("energy_created", "power", power, "colors", Snap.Colors(powerColors),
                "from_core", fromCore, "ok", __result != null);
        }
    }

    // ---- Battle flow -------------------------------------------------------------------------

    [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.InitBattleGrid))]
    internal static class Patch_InitBattleGrid
    {
        private static void Postfix(EnemyShipData enemyShipData, int battleEventID)
        {
            try
            {
                WantedProcess wp = Singleton<WantedManager>.Instance().wantedProcess;
                Log.Write("battle_init",
                    "enemy", enemyShipData?.dataID,
                    "enemy_type", enemyShipData?.enemyType,
                    "battle_event", battleEventID,
                    "seed", wp?.game_seed,
                    "world_seed", wp?.world_seed,
                    "seeded_run", wp?.seeded_run,
                    "small", wp?.wantedPhases?.currentSmallPhase,
                    "big", wp?.wantedPhases?.currentBigPhase,
                    "boss", wp?.onBossSuccessive,
                    "boss_idx", HistoryBossFightProcess.current);
            }
            catch (Exception ex)
            {
                Log.Error("InitBattleGrid", ex);
            }
        }
    }

    [HarmonyPatch(typeof(BattleManager), "PlayerTurn")]
    internal static class Patch_PlayerTurn
    {
        private static void Postfix(BattleManager __instance)
        {
            try
            {
                Log.Write("player_turn", Snap.Concat(Snap.Deck(),
                    "round", __instance.round,
                    "move", __instance.turnStartMoveCount,
                    "hand", Snap.Hand()));
            }
            catch (Exception ex)
            {
                Log.Error("PlayerTurn", ex);
            }
        }
    }

    [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.PlayerTurnEnd))]
    internal static class Patch_PlayerTurnEnd
    {
        private static void Prefix(BattleManager __instance)
        {
            try
            {
                Log.Write("player_turn_end", "round", __instance.round, "hand", Snap.Hand());
            }
            catch (Exception ex)
            {
                Log.Error("PlayerTurnEnd", ex);
            }
        }
    }

    [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.DeadCheck))]
    internal static class Patch_DeadCheck
    {
        private static void Postfix(BattleManager __instance, bool __result)
        {
            Log.Write("dead_check", "round", __instance.round, "over", __result,
                "player_dead", __instance.playerShip?.isDead, "enemy_dead", __instance.enemyShip?.isDead);
        }
    }

    [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.BattleEnd))]
    internal static class Patch_BattleEnd
    {
        private static void Prefix(BattleManager __instance)
        {
            Snapshot.Write("battle_end");
            Log.Write("battle_end", "round", __instance.round,
                "player_dead", __instance.playerShip?.isDead, "enemy_dead", __instance.enemyShip?.isDead);
        }

        private static void Postfix() => Recorder.End();
    }
}
