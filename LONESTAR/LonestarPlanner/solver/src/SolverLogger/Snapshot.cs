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

namespace SolverLogger
{
    /// <summary>
    /// Full battle state at one instant: both ships, every unit (slots, loaded powers, buffs,
    /// skill counters), the clash lines, the hand, the energy decks and the enemy AI cursors.
    /// This is the ground truth the headless engine must reproduce step by step.
    /// </summary>
    internal static class Snapshot
    {
        private static readonly AccessTools.FieldRef<ShipUnitSlot, int> SlotRare =
            AccessTools.FieldRefAccess<ShipUnitSlot, int>("rare_");

        public static void Write(string tag, params object[] extra)
        {
            if (!Recorder.Active)
            {
                return;
            }
            try
            {
                List<object> kv = Build(tag);
                kv.AddRange(extra);
                Log.Write("snap", kv.ToArray());
            }
            catch (Exception ex)
            {
                Log.Error("Snapshot." + tag, ex);
            }
        }

        /// <summary>The snapshot as alternating key/value pairs (also used headlessly as a state fingerprint).</summary>
        public static List<object> Build(string tag)
        {
            BattleManager bm = Singleton<BattleManager>.Instance();
            var kv = new List<object>
            {
                "tag", tag,
                "round", bm.round,
                "status", bm.turnStatus,
                "previewing", Singleton<PreviewManager>.Instance().isPreviewing,
                "grid_off", new[] { bm.battlePlayerGridOffest, bm.battleEnemyGridOffest },
                "player", Ship(bm.playerShip),
                "enemy", Ship(bm.enemyShip),
                "ai", Ai(bm.enemyShip),
                "lines", Lines(bm),
                "hand", Snap.Hand(),
            };
            kv.AddRange(Snap.Deck());
            return kv;
        }

        public static Dictionary<string, object> UnitRef(ShipUnitController u)
        {
            if (u == null)
            {
                return null;
            }
            return new Dictionary<string, object>
            {
                ["player"] = u.shipController != null && u.shipController.playerflag,
                ["id"] = u.unitData?.id,
                ["coor"] = Coor(u),
            };
        }

        public static int[] Coor(ShipUnitController u) => u.shipCell == null ? null : new[] { u.coor.x, u.coor.y };

        private static Dictionary<string, object> Ship(ShipBaseController ship)
        {
            if (ship == null)
            {
                return null;
            }
            ShipData d = ship.shipData;
            var result = new Dictionary<string, object>
            {
                ["data_id"] = d.dataID,
                ["hp"] = d.currentHP,
                ["max_hp"] = d.maxHP,
                ["shield"] = d.currentShield,
                ["max_shield"] = d.maxShield,
                ["move"] = d.currentMove,
                ["max_move"] = d.maxMove,
                ["exchange"] = d.currentExchange,
                ["max_exchange"] = d.maxExchange,
                ["center"] = new[] { d.GetShipCenter().x, d.GetShipCenter().y },
                ["size"] = new[] { d.shipSize.x, d.shipSize.y },
                ["color_slots"] = d.colorSlotListBattle,
                ["dead"] = ship.isDead,
                ["units"] = ship.shipUnitControllers.Where(u => u != null).Select(Unit).ToList(),
            };
            if (ship is EnemyShipController enemy && enemy.breakSystem != null)
            {
                result["break"] = Dump.Fields(enemy.breakSystem);
            }
            return result;
        }

        private static Dictionary<string, object> Unit(ShipUnitController u)
        {
            return new Dictionary<string, object>
            {
                ["id"] = u.unitData?.id,
                ["lv"] = u.unitData?.lv,
                ["coor"] = Coor(u),
                ["active"] = u.shipUnitActive,
                ["destroyed"] = u.destroyed,
                ["slots"] = u.unitSlots.Select(s => new Dictionary<string, object>
                {
                    ["rare"] = SlotRare(s),
                    ["power"] = s.powerData == null ? null : Power(s.powerData),
                    ["empty"] = s.emptyDatas.Select(Power).ToList(),
                    ["disturbed"] = s.isDisturbed,
                    ["empty_pre"] = s.emptyPreCount,
                }).ToList(),
                ["powers"] = u.powerDataContainer?.powerDatas.Select(Power).ToList(),
                ["container"] = Dump.Fields(u.powerDataContainer),
                ["buffs"] = u.buffs.Select(b => new Dictionary<string, object>
                {
                    ["type"] = b.GetType().Name,
                    ["f"] = Dump.Fields(b),
                }).ToList(),
                ["skill"] = u.skill == null ? null : new Dictionary<string, object>
                {
                    ["type"] = u.skill.GetType().Name,
                    ["f"] = Dump.Fields(u.skill),
                },
            };
        }

        private static List<object> Power(PowerData p) =>
            new List<object> { p.power, (int)p.powerDataType, p.isExtra, p.endTurn };

        private static List<object> Lines(BattleManager bm)
        {
            return bm.battleResultInfos.OrderBy(kv => kv.Key).Select(kv => (object)new Dictionary<string, object>
            {
                ["y"] = kv.Key,
                ["player_line"] = kv.Value.playerLine,
                ["enemy_line"] = kv.Value.enemyLine,
                ["player_total"] = kv.Value.playerTotal,
                ["enemy_total"] = kv.Value.enemyTotal,
                ["result"] = kv.Value.result,
                ["has_player"] = kv.Value.playerShipController != null,
                ["has_enemy"] = kv.Value.enemyShipController != null,
                ["player_container"] = Dump.Fields(kv.Value.playerContainer),
                ["enemy_container"] = Dump.Fields(kv.Value.enemyContainer),
                ["damage"] = Dump.Fields(kv.Value.damageMaker),
            }).ToList();
        }

        private static Dictionary<string, object> Ai(EnemyShipController enemy)
        {
            AISequence seq = enemy?.AISequence;
            if (seq == null)
            {
                return null;
            }
            return new Dictionary<string, object>
            {
                ["break_turn"] = seq.breakTurn,
                ["move_process"] = seq.sequnceMoves?.process,
                ["notice_process"] = seq.sequnceNotices?.process,
                ["units"] = seq.sequnce.Where(kv => kv.Key != null).Select(kv => (object)new Dictionary<string, object>
                {
                    ["coor"] = Coor(kv.Key),
                    ["id"] = kv.Key.unitData?.id,
                    ["order"] = kv.Value.AIOrder,
                    ["process"] = kv.Value.process,
                    ["load_preview"] = kv.Value.loadPreview,
                }).ToList(),
            };
        }
    }
}
