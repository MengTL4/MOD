using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    public sealed class SwapSolverOptions
    {
        /// <summary>最多使用几次交换（游戏里由 maxExchange / currentExchange 决定）。</summary>
        public int MaxSwaps = 3;

        /// <summary>每层保留的候选数（束搜索宽度）。</summary>
        public int BeamWidth = 48;
    }

    /// <summary>
    /// 战斗内摆位求解器：不能上/下船，只能把部件挪到别的格子（游戏里一次移动消耗 1 点 exchange）。
    ///
    /// 因为交换次数通常只有 0~3，这里做的是"深度 ≤ 预算"的束搜索，
    /// 输出一串可以照着点/拖的操作。
    /// </summary>
    public sealed class SwapSolver
    {
        private readonly IPlacementEvaluator _evaluator;
        private readonly SwapSolverOptions _options;

        public SwapSolver(IPlacementEvaluator evaluator, SwapSolverOptions options = null)
        {
            _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
            _options = options ?? new SwapSolverOptions();
        }

        public LayoutPlan Solve(PlacementState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            string invalid = state.Validate();
            if (invalid != null) throw new ArgumentException("invalid PlacementState: " + invalid, nameof(state));

            var unitIndex = new Dictionary<int, int>();
            for (int i = 0; i < state.Units.Count; i++) unitIndex[state.Units[i].Id] = i;

            int[] start = new int[state.Units.Count];
            for (int i = 0; i < start.Length; i++) start[i] = -1;
            for (int i = 0; i < state.CurrentLayout.Count; i++)
            {
                var slot = state.CurrentLayout[i];
                int ui;
                if (!unitIndex.TryGetValue(slot.UnitId, out ui)) continue;
                start[ui] = state.Grid.IndexOf(slot.X, slot.Y);
            }

            var cells = state.Grid.UsableCells();
            var pairs = BuildPairs(cells);

            var budget = Math.Min(Math.Max(0, state.SwapBudget), Math.Max(0, _options.MaxSwaps));

            PlacementOutcome baseOutcome;
            double baseScore = _evaluator.Evaluate(state, ToLayout(state, start), out baseOutcome);

            LayoutPlan best = new LayoutPlan
            {
                Score = baseScore,
                Outcome = baseOutcome,
                SwapCount = 0,
                IsExact = true,
            };
            best.Layout.AddRange(ToLayout(state, start));

            if (budget == 0 || pairs.Count == 0)
            {
                best.Notes.Add("没有可用交换次数");
                return best;
            }

            var level = new List<Node>
            {
                new Node
                {
                    CellOfUnit = start,
                    Moves = new List<LayoutMove>(),
                    Score = baseScore,
                }
            };

            for (int depth = 0; depth < budget; depth++)
            {
                var next = new List<Node>();
                var keys = new HashSet<string>();

                for (int n = 0; n < level.Count; n++)
                {
                    var node = level[n];

                    for (int p = 0; p < pairs.Count; p++)
                    {
                        var pair = pairs[p];
                        int a = pair.A;
                        int b = pair.B;

                        int unitA = UnitAtCell(node.CellOfUnit, a);
                        int unitB = UnitAtCell(node.CellOfUnit, b);
                        if (unitA < 0 && unitB < 0) continue;

                        var swapped = (int[])node.CellOfUnit.Clone();
                        if (unitA >= 0) swapped[unitA] = b;
                        if (unitB >= 0) swapped[unitB] = a;

                        string key = Key(swapped);
                        if (!keys.Add(key)) continue;

                        var layout = ToLayout(state, swapped);
                        PlacementOutcome outcome;
                        double score = _evaluator.Evaluate(state, layout, out outcome);

                        // 交换时 a/b 对称；只有一边有部件时，from 必须是"有部件的那格"
                        int fromCell = unitA >= 0 ? a : b;
                        int toCell = unitA >= 0 ? b : a;

                        var move = new LayoutMove
                        {
                            Kind = (unitA >= 0 && unitB >= 0) ? LayoutMove.MoveKind.Swap : LayoutMove.MoveKind.Move,
                            UnitId = unitA >= 0 ? state.Units[unitA].Id : state.Units[unitB].Id,
                            UnitName = unitA >= 0 ? state.Units[unitA].Name : state.Units[unitB].Name,
                            OtherUnitId = (unitA >= 0 && unitB >= 0) ? state.Units[unitB].Id : -1,
                            FromX = state.Grid.XOf(fromCell),
                            FromY = state.Grid.YOf(fromCell),
                            ToX = state.Grid.XOf(toCell),
                            ToY = state.Grid.YOf(toCell),
                        };

                        var moves = new List<LayoutMove>(node.Moves) { move };

                        next.Add(new Node
                        {
                            CellOfUnit = swapped,
                            Moves = moves,
                            Score = score,
                            Outcome = outcome,
                        });

                        if (score > best.Score + 1e-9)
                        {
                            best = new LayoutPlan
                            {
                                Score = score,
                                Outcome = outcome,
                                SwapCount = depth + 1,
                                IsExact = true,
                            };
                            best.Layout.AddRange(layout);
                            best.Moves.AddRange(moves);
                        }
                    }
                }

                if (next.Count == 0) break;

                next.Sort((x, y) => y.Score.CompareTo(x.Score));
                if (next.Count > _options.BeamWidth) next.RemoveRange(_options.BeamWidth, next.Count - _options.BeamWidth);
                level = next;
            }

            if (best.Moves.Count == 0) best.Notes.Add("当前布局已经是最优，不需要交换");
            return best;
        }

        private static List<PlacementSlot> ToLayout(PlacementState state, int[] cellOfUnit)
        {
            var list = new List<PlacementSlot>();
            for (int u = 0; u < cellOfUnit.Length; u++)
            {
                if (cellOfUnit[u] < 0) continue;
                list.Add(new PlacementSlot(state.Units[u].Id, state.Grid.XOf(cellOfUnit[u]), state.Grid.YOf(cellOfUnit[u])));
            }
            return list;
        }

        private static int UnitAtCell(int[] cellOfUnit, int cell)
        {
            for (int u = 0; u < cellOfUnit.Length; u++)
            {
                if (cellOfUnit[u] == cell) return u;
            }
            return -1;
        }

        private static string Key(int[] cellOfUnit)
        {
            var chars = new char[cellOfUnit.Length];
            for (int i = 0; i < cellOfUnit.Length; i++) chars[i] = (char)(cellOfUnit[i] + 1);
            return new string(chars);
        }

        private static List<Pair> BuildPairs(List<int> cells)
        {
            var pairs = new List<Pair>();
            for (int i = 0; i < cells.Count; i++)
            {
                for (int j = i + 1; j < cells.Count; j++)
                {
                    pairs.Add(new Pair { A = cells[i], B = cells[j] });
                }
            }
            return pairs;
        }

        private struct Pair
        {
            public int A;
            public int B;
        }

        private sealed class Node
        {
            public int[] CellOfUnit;
            public List<LayoutMove> Moves;
            public double Score;
            public PlacementOutcome Outcome;
        }
    }
}
