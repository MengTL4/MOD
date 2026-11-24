#if HEADLESS
extern alias RealHarmony;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
#if HEADLESS
using RealHarmony::HarmonyLib;
#else
using HarmonyLib;
#endif
using LitJson;
using UnityEngine.Events;

namespace SolverLogger
{
    /// <summary>
    /// Battle recorder. Each battle attempt gets its own file containing:
    ///   battle_begin  – the game's own restart snapshot (BattleTempData) plus run context;
    ///   act_*         – every player decision, written before the game processes it,
    ///                   followed by act_result once it has been processed;
    ///   snap          – full state after each action and at every phase boundary.
    /// RNG records from Patches.cs are mirrored into the same file.
    /// </summary>
    internal static class Recorder
    {
        public static bool Active { get; private set; }

        private static string Static(string field) =>
            AccessTools.Field(typeof(BattleTempData), field).GetValue(null) as string;

        public static void Begin()
        {
            try
            {
                WantedProcess wp = Singleton<WantedManager>.Instance().wantedProcess;
                string label = wp?.wantedPhases == null
                    ? "battle"
                    : $"b{wp.wantedPhases.currentBigPhase}s{wp.wantedPhases.currentSmallPhase}";
                Log.BeginBattle(label);
                Active = true;
                Log.Write("battle_begin",
                    "seed", wp?.game_seed,
                    "world_seed", wp?.world_seed,
                    "seeded_run", wp?.seeded_run,
                    "small", wp?.wantedPhases?.currentSmallPhase,
                    "big", wp?.wantedPhases?.currentBigPhase,
                    "boss", wp?.onBossSuccessive,
                    "boss_idx", HistoryBossFightProcess.current,
                    "days", wp?.currentDays,
                    "is_cus_mod", wp?.isCusMod,
                    "process_advance", new RawJson(TryJson(wp?.processAdvance)),
                    "define_value", Dump.Fields(Define.defineValue),
                    "player_ship", new RawJson(Static("currentPlayerShipDataJson")),
                    "pilot", new RawJson(Static("currentPilotDataJson")),
                    "treasures", new RawJson(Static("itemTreasures")),
                    "wanted", new RawJson(Static("wanted")),
                    "bounty_events", new RawJson(Static("bountyEvents")),
                    "coin", AccessTools.Field(typeof(BattleTempData), "coin").GetValue(null),
                    "save_files", SaveFiles());
            }
            catch (Exception ex)
            {
                Log.Error("Recorder.Begin", ex);
            }
        }

        /// <summary>
        /// The save written by SaveManager.Save() just before TempSave (encrypted files, as on disk): permanent data (Save/Data) and
        /// the current run's slot (Save/{slot}/Save), keyed by path relative to persistentDataPath.
        /// The headless engine restores a battle by writing these back and running SaveManager.Load().
        /// </summary>
        internal static Dictionary<string, object> SaveFiles()
        {
            var files = new Dictionary<string, object>();
            string root = UnityEngine.Application.persistentDataPath;
            int slot = Singleton<SaveManager>.Instance().saveDataPack.currentSaveData;
            foreach (string dir in new[] { "Save/Data", $"Save/{slot}/Save" })
            {
                string full = System.IO.Path.Combine(root, dir);
                if (!System.IO.Directory.Exists(full))
                {
                    continue;
                }
                foreach (string file in System.IO.Directory.GetFiles(full, "*.json"))
                {
                    string name = System.IO.Path.GetFileName(file);
                    if (name.Contains("backup"))
                    {
                        continue;
                    }
                    // Saves are encrypted text; kept verbatim so the game's own JsonManager reads them back.
                    files[dir + "/" + name] = System.IO.File.ReadAllText(file);
                }
            }
            return files;
        }

        public static void End()
        {
            Active = false;
            Log.EndBattle();
        }

        /// <summary>Logs a player action before the game processes it.</summary>
        public static void Act(string type, params object[] kv)
        {
            if (Active)
            {
                Log.Write(type, kv);
            }
        }

        /// <summary>Logs the outcome of the preceding action and snapshots the resulting state.</summary>
        public static void Result(string type, bool ok, params object[] kv)
        {
            if (!Active)
            {
                return;
            }
            Log.Write("act_result", new object[] { "of", type, "ok", ok }.Concat(kv).ToArray());
            if (ok)
            {
                Snapshot.Write("after_" + type.Substring("act_".Length));
            }
        }

        private static string TryJson(object o)
        {
            if (o == null)
            {
                return null;
            }
            try
            {
                return JsonMapper.ToJson(o);
            }
            catch (Exception ex)
            {
                Log.Error("Recorder.TryJson(" + o.GetType().Name + ")", ex);
                return null;
            }
        }
    }

    // ---- Lifecycle ---------------------------------------------------------------------------

    [HarmonyPatch(typeof(BattleTempData), nameof(BattleTempData.TempLoad))]
    internal static class Patch_TempLoad
    {
        private static void Prefix() => Recorder.Act("battle_retry");
    }

    // ---- Player actions ----------------------------------------------------------------------

    [HarmonyPatch(typeof(PowerController), nameof(PowerController.PowerLoad))]
    internal static class Patch_PowerLoad
    {
        private static void Prefix(PowerController __instance, ShipUnitController shipUnitController)
        {
            EnergyData e = __instance.energyData;
            List<EnergyData> hand = Snap.HandPowers(Singleton<PowerManager>.Instance());
            Recorder.Act("act_load",
                "energy", e == null ? null : new List<object> { e.power, Snap.Colors(e.powerColors) },
                "hand_index", e == null ? -1 : hand.IndexOf(e),
                "target", Snapshot.UnitRef(shipUnitController),
                "by", Snap.Callers());
        }

        private static void Postfix(bool __result) => Recorder.Result("act_load", __result);
    }

    [HarmonyPatch]
    internal static class Patch_Move
    {
        private static readonly AccessTools.FieldRef<ShipMoveCell, ShipController> ShipOf =
            AccessTools.FieldRefAccess<ShipMoveCell, ShipController>("shipController");

        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ShipMoveCell), nameof(ShipMoveCell.OnMove));
            yield return AccessTools.Method(typeof(ShipMoveCellEnemy), nameof(ShipMoveCellEnemy.OnMove));
        }

        private static void Prefix(ShipMoveCell __instance, int y, out int[] __state)
        {
            ShipController ship = ShipOf(__instance);
            BattleManager bm = Singleton<BattleManager>.Instance();
            __state = new[] { ship.shipData.GetShipCenter().y, ship.shipData.currentMove, bm.battlePlayerGridOffest, bm.battleEnemyGridOffest };
            Recorder.Act("act_move",
                "y", y,
                "ship_is_player", ship.playerflag,
                "cell", __instance.GetType().Name,
                "before", __state);
        }

        private static void Postfix(ShipMoveCell __instance, int[] __state)
        {
            ShipController ship = ShipOf(__instance);
            BattleManager bm = Singleton<BattleManager>.Instance();
            int[] after = { ship.shipData.GetShipCenter().y, ship.shipData.currentMove, bm.battlePlayerGridOffest, bm.battleEnemyGridOffest };
            Recorder.Result("act_move", !after.SequenceEqual(__state), "after", after);
        }
    }

    [HarmonyPatch(typeof(ShipUnitSkill), nameof(ShipUnitSkill.Clicked))]
    internal static class Patch_Click
    {
        private static void Prefix(ShipUnitSkill __instance) =>
            Recorder.Act("act_click",
                "unit", Snapshot.UnitRef(__instance.shipUnitController),
                "skill", __instance.GetType().Name,
                "two_step", __instance is ITwoStepClick);

        private static void Postfix(bool __result) => Recorder.Result("act_click", __result);
    }

    /// <summary>Second step of targeted skills: wraps the panel's callback to see the chosen target.</summary>
    [HarmonyPatch(typeof(ShipDirPanel), nameof(ShipDirPanel.Init), typeof(List<ShipUnitController>), typeof(UnityAction<ShipUnitController>), typeof(UnityAction<ShipUnitController>))]
    internal static class Patch_TargetUnit
    {
        private static void Prefix(List<ShipUnitController> shipUnitControllers, ref UnityAction<ShipUnitController> action)
        {
            if (action == null)
            {
                return;
            }
            UnityAction<ShipUnitController> original = action;
            List<object> options = shipUnitControllers.Select(u => (object)Snapshot.UnitRef(u)).ToList();
            action = target =>
            {
                Recorder.Act("act_target", "kind", "unit", "target", Snapshot.UnitRef(target), "options", options);
                original(target);
                Recorder.Result("act_target", true);
            };
        }
    }

    [HarmonyPatch(typeof(ShipDirPanel), nameof(ShipDirPanel.Init), typeof(List<ShipCell>), typeof(UnityAction<ShipCell>), typeof(UnityAction<ShipCell>))]
    internal static class Patch_TargetCell
    {
        private static int[] CellCoor(ShipCell c) => c == null ? null : new[] { c.coor.x, c.coor.y };

        private static void Prefix(List<ShipCell> shipCells, ref UnityAction<ShipCell> action)
        {
            if (action == null)
            {
                return;
            }
            UnityAction<ShipCell> original = action;
            List<object> options = shipCells.Select(c => (object)CellCoor(c)).ToList();
            action = target =>
            {
                Recorder.Act("act_target", "kind", "cell", "target", CellCoor(target), "options", options);
                original(target);
                Recorder.Result("act_target", true);
            };
        }
    }

    [HarmonyPatch(typeof(PlayerExchangeShipController), nameof(PlayerExchangeShipController.DoTeleportAtEnd))]
    internal static class Patch_Exchange
    {
        private static void Prefix()
        {
            PreviewManager pm = Singleton<PreviewManager>.Instance();
            Recorder.Act("act_exchange",
                "start_coor", new[] { pm.startCoor.x, pm.startCoor.y },
                "start_cell", pm.startCell == null ? null : new[] { pm.startCell.coor.x, pm.startCell.coor.y });
        }

        private static void Postfix() => Recorder.Result("act_exchange", true);
    }

    /// <summary>多彩号: a hand card dropped on a paint pot (only the pot under the drop paints).</summary>
    [HarmonyPatch(typeof(ColorSlotCell), "PowerAction")]
    internal static class Patch_Paint
    {
        private static void Prefix(ColorSlotCell __instance, PowerController powerController, out bool __state)
        {
            EnergyData e = powerController?.energyData;
            __state = Paint.IsDropTarget(__instance) && Paint.CanPaint(e, __instance);
            if (!__state)
            {
                return;
            }
            List<EnergyData> hand = Snap.HandPowers(Singleton<PowerManager>.Instance());
            Recorder.Act("act_paint",
                "energy", new List<object> { e.power, Snap.Colors(e.powerColors) },
                "hand_index", hand.IndexOf(e),
                "rare", __instance.rare,
                "by", Snap.Callers());
        }

        private static void Postfix(bool __state)
        {
            if (__state)
            {
                Recorder.Result("act_paint", true);
            }
        }
    }

    /// <summary>多彩号 with clickPool: a paint pot clicked (creates a 1-power card of its colour).</summary>
    [HarmonyPatch(typeof(ColorSlotCell), "OnClickPool")]
    internal static class Patch_PaintPool
    {
        private static void Prefix(ColorSlotCell __instance, out bool __state)
        {
            __state = Paint.PointsLeft(__instance) != 0 && PowerController.draggingPowerController == null;
            if (__state)
            {
                Recorder.Act("act_paint_pool", "rare", __instance.rare, "by", Snap.Callers());
            }
        }

        private static void Postfix(bool __state)
        {
            if (__state)
            {
                Recorder.Result("act_paint_pool", true);
            }
        }
    }

    [HarmonyPatch(typeof(BattleChoiceButton), nameof(BattleChoiceButton.Click))]
    internal static class Patch_Choice
    {
        private static void Prefix(BattleChoiceButton __instance) =>
            Recorder.Act("act_choice",
                "index", ButtonIndex(__instance),
                "option", __instance.option?.GetType().Name,
                "fields", Dump.Fields(__instance.option),
                "check", __instance.option?.check);

        private static void Postfix(BattleChoiceButton __instance) => Recorder.Result("act_choice", __instance.option != null);

        private static int ButtonIndex(BattleChoiceButton button)
        {
            BattleChoicePanel panel = Singleton<UIManager>.Instance().GetPanel<BattleChoicePanel>(FilePath.battleChoicePanel);
            return panel?.buttons == null ? -1 : Array.IndexOf(panel.buttons, button);
        }
    }

    [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.EndTurn))]
    internal static class Patch_EndTurn
    {
        private static void Prefix(BattleManager __instance) =>
            Recorder.Act("act_end_turn", "status", __instance.turnStatus, "by", Snap.Callers());
    }

    // ---- Phase boundaries --------------------------------------------------------------------
    // Coroutine methods: a prefix runs when the coroutine is created, i.e. right before its body.

    [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.BattleBegin))]
    internal static class Patch_Snap_BattleBegin
    {
        private static void Prefix() => Snapshot.Write("pre_start", "define_value", Dump.Fields(Define.defineValue));
    }

    [HarmonyPatch(typeof(BattleManager), "PlayerTurn")]
    internal static class Patch_Snap_PlayerTurn
    {
        private static void Postfix() => Snapshot.Write("player_turn_start");
    }

    [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.PlayerTurnEnd))]
    internal static class Patch_Snap_PlayerTurnEnd
    {
        private static void Prefix() => Snapshot.Write("player_turn_end");
    }

    [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.DeadCheck))]
    internal static class Patch_Snap_DeadCheck
    {
        private static void Postfix() => Snapshot.Write("after_damage");
    }

    [HarmonyPatch(typeof(BattleManager), "TurnCaculateEnd")]
    internal static class Patch_Snap_TurnCaculateEnd
    {
        private static void Prefix() => Snapshot.Write("calc_end");
    }

    [HarmonyPatch(typeof(BattleManager), "EnemyTurnPre")]
    internal static class Patch_Snap_EnemyTurnPre
    {
        private static void Prefix() => Snapshot.Write("enemy_turn_start");
    }

    [HarmonyPatch(typeof(BattleManager), "EnemyTurnEnd")]
    internal static class Patch_Snap_EnemyTurnEnd
    {
        private static void Prefix() => Snapshot.Write("enemy_turn_end");
    }
}
