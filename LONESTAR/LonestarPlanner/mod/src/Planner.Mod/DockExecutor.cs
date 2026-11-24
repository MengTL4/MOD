using System;
using System.Collections.Generic;
using LonestarPlanner.Core;
using UnityEngine;

namespace LonestarPlanner.Mod
{
    /// <summary>船坞快照：核心模型 + 对应的游戏对象，用来把"计划"落成"操作"。</summary>
    public sealed class DockSnapshot
    {
        public PlacementState State;
        public ShipData ShipData;
        public Dictionary<int, UnitData> EquippedByCardId = new Dictionary<int, UnitData>();
        public Dictionary<int, ItemUnit> ItemByCardId = new Dictionary<int, ItemUnit>();
    }

    /// <summary>
    /// 船坞一键摆放执行器。
    ///
    /// 完全复用游戏自己的装配链路（和拖拽落下时一致）：
    ///   船内移动/互换：EquipShipUnit（来源先清空，目标再装上）
    ///   从背包上船：   EquipShipUnit(item.unitData) + ItemManager.ToCell(item)
    ///   卸回背包：     EquipShipUnit(null) + ItemManager.ToItem(unitData)
    ///
    /// 视觉/控制器不需要我们管：ShipBaseVisual 给每个格子挂了 grid 变更监听，
    /// EquipShipUnit 触发 TriggerGridObjectChanged 之后会自己重建那一格。
    /// </summary>
    public static class DockExecutor
    {
        public static bool CanApply(DockSnapshot snapshot)
        {
            if (snapshot == null || snapshot.State == null) return false;
            if (Singleton<BattleManager>.Instance().inBattle) return false;
            return snapshot.ShipData != null && snapshot.ShipData.grid != null;
        }

        public static bool Apply(DockSnapshot snapshot, LayoutPlan plan, out string report)
        {
            report = string.Empty;

            if (!CanApply(snapshot))
            {
                report = L.T("当前不在船坞，无法摆放", "Not in the dock, cannot rearrange");
                return false;
            }

            if (plan == null || plan.Moves.Count == 0)
            {
                report = L.T("当前摆位已经是最优", "The current layout is already the best");
                return true;
            }

            var grid = snapshot.ShipData.grid;
            var done = new List<string>();

            try
            {
                for (int i = 0; i < plan.Moves.Count; i++)
                {
                    var move = plan.Moves[i];
                    if (!ApplyOne(grid, snapshot, move, done)) continue;
                }

                report = L.T("已执行 " + done.Count + " 步", done.Count + " steps done");
                Debug.Log("[Planner] dock layout applied: " + string.Join(" | ", done.ToArray()));
                return true;
            }
            catch (Exception ex)
            {
                report = L.T("摆放中断：" + ex.Message + "（已完成 " + done.Count + " 步）", "Stopped: " + ex.Message + " (" + done.Count + " steps done)");
                Debug.LogWarning("[Planner] dock apply failed: " + ex);
                return false;
            }
        }

        private static bool ApplyOne(GridMap<ShipCell> grid, DockSnapshot snapshot, LayoutMove move, List<string> done)
        {
            switch (move.Kind)
            {
                case LayoutMove.MoveKind.Bench:
                {
                    var cell = grid.GetContent(move.FromX, move.FromY);
                    UnitData unit;
                    if (cell == null || !snapshot.EquippedByCardId.TryGetValue(move.UnitId, out unit)) return false;

                    cell.EquipShipUnit(null, update: true, checkover: false);
                    Singleton<ItemManager>.Instance().ToItem(unit);
                    done.Add("卸下 " + move.UnitName);
                    return true;
                }

                case LayoutMove.MoveKind.Move:
                {
                    var from = grid.GetContent(move.FromX, move.FromY);
                    var to = grid.GetContent(move.ToX, move.ToY);
                    UnitData unit;
                    if (from == null || to == null) return false;
                    if (!snapshot.EquippedByCardId.TryGetValue(move.UnitId, out unit)) return false;
                    if (unit == null || !to.CheckEquipable(unit)) return false;

                    from.EquipShipUnit(null, update: true, checkover: false);
                    to.EquipShipUnit(unit);

                    // 防丢件：目标必须真的装上了，否则把来源恢复
                    if (!ReferenceEquals(to.unitData, unit))
                    {
                        from.EquipShipUnit(unit, update: true, checkover: false);
                        Debug.LogWarning("[Planner] move rejected by game, restored " + move.UnitName);
                        return false;
                    }

                    done.Add("移动 " + move.UnitName + " (" + move.FromX + "," + move.FromY + ")→(" + move.ToX + "," + move.ToY + ")");
                    return true;
                }

                case LayoutMove.MoveKind.Deploy:
                {
                    var to = grid.GetContent(move.ToX, move.ToY);
                    ItemUnit item;
                    if (to == null || !snapshot.ItemByCardId.TryGetValue(move.UnitId, out item) || item == null) return false;

                    if (item.unitData == null || !to.CheckEquipable(item.unitData)) return false;

                    var displaced = to.unitData;
                    to.EquipShipUnit(item.unitData);

                    // 防丢件：只有真的装上了才从背包里移除
                    if (!ReferenceEquals(to.unitData, item.unitData))
                    {
                        Debug.LogWarning("[Planner] deploy rejected by game: " + move.UnitName);
                        return false;
                    }

                    Singleton<ItemManager>.Instance().ToCell(item);
                    if (displaced != null) Singleton<ItemManager>.Instance().ToItem(displaced);
                    done.Add("上船 " + move.UnitName + " →(" + move.ToX + "," + move.ToY + ")");
                    return true;
                }

                case LayoutMove.MoveKind.Swap:
                {
                    var a = grid.GetContent(move.FromX, move.FromY);
                    var b = grid.GetContent(move.ToX, move.ToY);
                    if (a == null || b == null) return false;

                    var unitA = a.unitData;
                    var unitB = b.unitData;
                    if (unitA == null || unitB == null) return false;
                    if (!a.CheckEquipable(unitB) || !b.CheckEquipable(unitA)) return false;

                    a.EquipShipUnit(unitB, update: true, checkover: false);
                    b.EquipShipUnit(unitA);

                    if (!ReferenceEquals(a.unitData, unitB) || !ReferenceEquals(b.unitData, unitA))
                    {
                        // 回滚成原状
                        a.EquipShipUnit(unitA, update: true, checkover: false);
                        b.EquipShipUnit(unitB);
                        Debug.LogWarning("[Planner] swap rejected by game, rolled back");
                        return false;
                    }

                    done.Add("互换 (" + move.FromX + "," + move.FromY + ")↔(" + move.ToX + "," + move.ToY + ")");
                    return true;
                }

                default:
                    return false;
            }
        }
    }
}
