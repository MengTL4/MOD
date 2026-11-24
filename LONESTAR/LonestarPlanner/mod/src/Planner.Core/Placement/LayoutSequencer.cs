using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 把"当前布局 → 目标布局"翻译成一串可以照着做的操作。
    /// 规则：每一步要么放进空格、要么和占着目标格的部件交换，保证按顺序执行一定合法。
    /// </summary>
    public static class LayoutSequencer
    {
        public static List<LayoutMove> Build(PlacementState state, IReadOnlyList<PlacementSlot> target)
        {
            var moves = new List<LayoutMove>();
            if (state == null || target == null) return moves;

            var currentCell = new Dictionary<int, int>();
            for (int i = 0; i < state.CurrentLayout.Count; i++)
            {
                var slot = state.CurrentLayout[i];
                currentCell[slot.UnitId] = state.Grid.IndexOf(slot.X, slot.Y);
            }

            var targetCell = new Dictionary<int, int>();
            for (int i = 0; i < target.Count; i++)
            {
                var slot = target[i];
                targetCell[slot.UnitId] = state.Grid.IndexOf(slot.X, slot.Y);
            }

            // 1) 先卸下不要的部件，腾出格子
            var remaining = new List<int>();
            for (int i = 0; i < state.Units.Count; i++)
            {
                var card = state.Units[i];
                int t;
                bool hasTarget = targetCell.TryGetValue(card.Id, out t);
                int cur;
                bool hasCur = currentCell.TryGetValue(card.Id, out cur);

                if (!hasTarget)
                {
                    if (hasCur)
                    {
                        moves.Add(new LayoutMove
                        {
                            Kind = LayoutMove.MoveKind.Bench,
                            UnitId = card.Id,
                            UnitName = card.Name,
                            FromX = state.Grid.XOf(cur),
                            FromY = state.Grid.YOf(cur),
                        });
                        currentCell.Remove(card.Id);
                    }
                    continue;
                }

                if (hasCur && cur == t) continue;

                remaining.Add(card.Id);
            }

            int guard = 0;
            while (remaining.Count > 0 && guard++ < 20000)
            {
                bool progressed = false;

                // 2) 目标格是空的，直接搬过去
                for (int i = 0; i < remaining.Count; i++)
                {
                    int unitId = remaining[i];
                    int dst = targetCell[unitId];
                    if (OccupantOf(currentCell, dst) >= 0) continue;

                    int cur;
                    bool hasCur = currentCell.TryGetValue(unitId, out cur);

                    moves.Add(new LayoutMove
                    {
                        Kind = hasCur ? LayoutMove.MoveKind.Move : LayoutMove.MoveKind.Deploy,
                        UnitId = unitId,
                        UnitName = NameOf(state, unitId),
                        FromX = hasCur ? state.Grid.XOf(cur) : -1,
                        FromY = hasCur ? state.Grid.YOf(cur) : -1,
                        ToX = state.Grid.XOf(dst),
                        ToY = state.Grid.YOf(dst),
                    });

                    if (hasCur) currentCell.Remove(unitId);
                    currentCell[unitId] = dst;
                    remaining.RemoveAt(i);
                    i--;
                    progressed = true;
                }

                if (progressed || remaining.Count == 0) continue;

                // 3) 卡在循环里：找一个"目标格被另一个待移动部件占着"的部件，做一次交换
                int victim = -1;
                int blocker = -1;
                for (int i = 0; i < remaining.Count && victim < 0; i++)
                {
                    int unitId = remaining[i];
                    if (!currentCell.ContainsKey(unitId)) continue;

                    int occupant = OccupantOf(currentCell, targetCell[unitId]);
                    if (occupant < 0 || !remaining.Contains(occupant)) continue;
                    if (!currentCell.ContainsKey(occupant)) continue;

                    victim = unitId;
                    blocker = occupant;
                }

                if (victim < 0) break;

                int from = currentCell[victim];
                int to = targetCell[victim];

                moves.Add(new LayoutMove
                {
                    Kind = LayoutMove.MoveKind.Swap,
                    UnitId = victim,
                    UnitName = NameOf(state, victim),
                    OtherUnitId = blocker,
                    FromX = state.Grid.XOf(from),
                    FromY = state.Grid.YOf(from),
                    ToX = state.Grid.XOf(to),
                    ToY = state.Grid.YOf(to),
                });

                currentCell[victim] = to;
                currentCell[blocker] = from;
                remaining.Remove(victim);
                // 两两对调时被换走的那个往往正好落到自己的目标格，不能再留在待移动里（否则会出现"自己和自己互换"）
                if (from == targetCell[blocker]) remaining.Remove(blocker);
            }

            return moves;
        }

        private static int OccupantOf(Dictionary<int, int> currentCell, int cell)
        {
            foreach (var kv in currentCell)
            {
                if (kv.Value == cell) return kv.Key;
            }
            return -1;
        }

        private static string NameOf(PlacementState state, int unitId)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                if (state.Units[i].Id == unitId) return state.Units[i].Name;
            }
            return "#" + unitId;
        }
    }
}
