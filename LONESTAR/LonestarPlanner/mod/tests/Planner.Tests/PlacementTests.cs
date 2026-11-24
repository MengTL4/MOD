using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LonestarPlanner.Core;

namespace LonestarPlanner.Tests
{
    public static class PlacementTests
    {
        public static void Register()
        {
            TestRunner.Test("摆位: 布局合法（格子可用/不重复/不超载），且动作序列可执行", () =>
            {
                var rng = new Random(555);
                var solver = new PlacementSolver(new ApproxPlacementEvaluator());

                for (int i = 0; i < 15; i++)
                {
                    var state = PlacementFactory.RandomPlacement(rng, new PlacementGenOptions
                    {
                        Width = 5,
                        Height = 4,
                        MinUnits = 5,
                        MaxUnits = 8,
                        LoadLimit = 6,
                    });

                    var plan = solver.Solve(state);
                    TestRunner.True(plan.Outcome.LoadUsed <= state.LoadLimit, "不应超载: " + plan.Outcome.LoadUsed);

                    var cells = new HashSet<int>();
                    foreach (var slot in plan.Layout)
                    {
                        TestRunner.True(state.Grid.IsUsable(slot.X, slot.Y), "布局用了不可用格");
                        TestRunner.True(cells.Add(state.Grid.IndexOf(slot.X, slot.Y)), "布局格子重复");
                    }

                    ApplyMovesAndCompare(state, plan);
                }
            });

            TestRunner.Test("摆位: 小规模上与穷举最优一致", () =>
            {
                var rng = new Random(20260818);
                var evaluator = new ApproxPlacementEvaluator();
                var solver = new PlacementSolver(evaluator, new PlacementSolverOptions { Restarts = 2, Iterations = 4000 });

                for (int i = 0; i < 12; i++)
                {
                    var state = PlacementFactory.Blueprint(3, 2, 3, 7);
                    state.LoadLimit = 3;
                    state.TokenPool.AddRange(PlacementFactory.ExpectedPool(rng, 2, 3));

                    int unitCount = rng.Next(2, 4);
                    for (int u = 0; u < unitCount; u++)
                    {
                        var card = new UnitCard
                        {
                            Id = u,
                            Name = "P" + u,
                            LoadCost = 1,
                            Powerable = true,
                        };
                        int slots = rng.Next(1, 3);
                        for (int s = 0; s < slots; s++) card.SlotRarities.Add(rng.Next(1, 4));
                        state.Units.Add(card);

                        // 随机初始摆放
                        if (rng.NextDouble() < 0.6 && state.CurrentLayout.Count < 3)
                        {
                            var free = state.Grid.UsableCells().Where(c => state.CurrentLayout.All(p => state.Grid.IndexOf(p.X, p.Y) != c)).ToList();
                            if (free.Count > 0)
                            {
                                int cell = free[rng.Next(free.Count)];
                                state.CurrentLayout.Add(new PlacementSlot(u, state.Grid.XOf(cell), state.Grid.YOf(cell)));
                            }
                        }
                    }

                    var plan = solver.Solve(state);
                    double brute = BruteForcePlacement(state, evaluator);
                    TestRunner.Near(brute, plan.Score, 1e-6, "case " + i + " 退火应找到穷举最优（或至少同分）");
                }
            });

            TestRunner.Test("摆位: 会把高槽位部件挪到吃紧的行", () =>
            {
                var state = PlacementFactory.Blueprint(2, 2, 0, 8);
                state.LoadLimit = 4;

                // A：两个紫色槽，初始在 row0（对面 0 战力）
                state.Place(0, 0, 0, 1, 3, 3);
                // B：一个白色槽，初始在 row1（对面 8 战力）
                state.Place(1, 0, 1, 1, 1);

                state.WithToken(0, 4, PowerColor.Purple)
                     .WithToken(1, 4, PowerColor.Purple)
                     .WithToken(2, 2, PowerColor.White);

                var solver = new PlacementSolver(new ApproxPlacementEvaluator(),
                    new PlacementSolverOptions { Restarts = 3, Iterations = 6000 });
                var plan = solver.Solve(state);

                var moved = plan.Layout.First(p => p.UnitId == 0);
                TestRunner.Equal(1, moved.Y, "A 应被挪到第 2 行，把战力顶到对手最强的那行");
                TestRunner.Greater(plan.Score, 0.8, "分数应高于原始摆放");
            });

            TestRunner.Test("摆位: 会把背包里的部件换上场", () =>
            {
                var state = PlacementFactory.Blueprint(2, 2, 0, 6);
                state.LoadLimit = 2;

                state.Place(0, 0, 0, 1, 1);       // 船上：单白槽
                state.Bench(1, 1, 1, 1);          // 背包：双白槽（更能吃能量）

                state.WithToken(0, 3, PowerColor.White)
                     .WithToken(1, 3, PowerColor.White);

                var solver = new PlacementSolver(new ApproxPlacementEvaluator(),
                    new PlacementSolverOptions { Restarts = 3, Iterations = 6000 });
                var plan = solver.Solve(state);

                TestRunner.True(plan.Layout.Any(p => p.UnitId == 1), "双槽部件应该被装上船");
                TestRunner.True(!plan.BenchedUnitIds.Contains(1), "它不应该还在背包里");
            });

            TestRunner.Test("交换: 预算为 0 时不动，且给出说明", () =>
            {
                var state = PlacementFactory.Blueprint(2, 2, 0, 6);
                state.SwapBudget = 0;
                state.Place(0, 0, 0, 1, 1);
                state.Place(1, 0, 1, 1, 1);
                state.WithToken(0, 3, PowerColor.White);

                var plan = new SwapSolver(new ApproxPlacementEvaluator()).Solve(state);
                TestRunner.Equal(0, plan.Moves.Count, "没有交换次数就不应给出操作");
            });

            TestRunner.Test("交换: 一次交换能翻盘时就换", () =>
            {
                var state = PlacementFactory.Blueprint(2, 2, 0, 6);
                state.SwapBudget = 1;

                // row0：双白槽；row1：单白槽
                state.Place(0, 0, 0, 1, 1, 1);
                state.Place(1, 0, 1, 1, 1);
                state.WithToken(0, 3, PowerColor.White)
                     .WithToken(1, 3, PowerColor.White)
                     .WithToken(2, 3, PowerColor.White);

                var baseOutcome = new ApproxPlacementEvaluator().Evaluate(state, state.CurrentLayout, out _);
                var plan = new SwapSolver(new ApproxPlacementEvaluator()).Solve(state);

                TestRunner.Equal(1, plan.SwapCount, "应只花一次交换");
                TestRunner.Equal(1, plan.Moves.Count, "只给一步操作");
                TestRunner.Greater(plan.Score, baseOutcome, "交换后应该更好");
                TestRunner.Equal(1, plan.Layout.First(p => p.UnitId == 0).Y, "双槽部件应去第 2 行");
            });

            TestRunner.Test("交换: 动作序列回放后与目标布局一致", () =>
            {
                var rng = new Random(777);
                var solver = new SwapSolver(new ApproxPlacementEvaluator(),
                    new SwapSolverOptions { MaxSwaps = 2, BeamWidth = 16 });

                for (int i = 0; i < 8; i++)
                {
                    var state = PlacementFactory.RandomPlacement(rng, new PlacementGenOptions
                    {
                        Width = 4,
                        Height = 3,
                        MinUnits = 3,
                        MaxUnits = 5,
                        LoadLimit = 4,
                    });
                    state.SwapBudget = 2;

                    var plan = solver.Solve(state);
                    ApplyMovesAndCompare(state, plan);
                    TestRunner.True(plan.SwapCount <= 2, "不应超过交换预算");
                }
            });

            TestRunner.Test("动作序列: 两两对调后不会再出现自己和自己互换", () =>
            {
                var state = new PlacementState
                {
                    Grid = new ShipGridModel(3, 3),
                    EnemyRowPower = new int[3],
                    PlayerRowFlatBonus = new int[3],
                    LoadLimit = 6,
                };
                for (int id = 0; id < 3; id++)
                {
                    state.Units.Add(new UnitCard { Id = id, Name = "U" + id, LoadCost = 1, SlotRarities = new List<int> { 1 } });
                }
                state.CurrentLayout.Add(new PlacementSlot(0, 1, 0));
                state.CurrentLayout.Add(new PlacementSlot(1, 0, 2));
                state.CurrentLayout.Add(new PlacementSlot(2, 2, 2));
                // 0 和 1 对调，2 不动
                var target = new List<PlacementSlot> { new PlacementSlot(0, 0, 2), new PlacementSlot(1, 1, 0), new PlacementSlot(2, 2, 2) };

                var moves = LayoutSequencer.Build(state, target);
                TestRunner.Equal(1, moves.Count, "一次互换就够了");
                foreach (var m in moves)
                {
                    TestRunner.True(m.Kind != LayoutMove.MoveKind.Swap || m.FromX != m.ToX || m.FromY != m.ToY, "不应有原地互换");
                }
            });

            TestRunner.Test("摆位/交换: 同种子结果可复现", () =>
            {
                var state = PlacementFactory.RandomPlacement(new Random(9090), new PlacementGenOptions { MinUnits = 6, MaxUnits = 6 });

                var a = new PlacementSolver(new ApproxPlacementEvaluator()).Solve(state.Clone());
                var b = new PlacementSolver(new ApproxPlacementEvaluator()).Solve(state.Clone());
                TestRunner.Equal(a.Score, b.Score, "摆位分数应一致");
                TestRunner.Equal(
                    string.Join(",", a.Layout.Select(p => p.UnitId + "@" + p.X + "," + p.Y)),
                    string.Join(",", b.Layout.Select(p => p.UnitId + "@" + p.X + "," + p.Y)),
                    "摆位结果应一致");

                var c = new SwapSolver(new ApproxPlacementEvaluator()).Solve(state.Clone());
                var d = new SwapSolver(new ApproxPlacementEvaluator()).Solve(state.Clone());
                TestRunner.Equal(c.Score, d.Score, "交换分数应一致");
            });

            TestRunner.Test("摆位: 空船/无可用格等退化输入不崩", () =>
            {
                var solver = new PlacementSolver(new ApproxPlacementEvaluator());

                var empty = PlacementFactory.Blueprint(2, 2, 0, 0);
                var planA = solver.Solve(empty);
                TestRunner.Equal(0, planA.Layout.Count, "空局面应该给出空布局");

                var blocked = new PlacementState
                {
                    Grid = new ShipGridModel(3, 3, (x, y) => false),
                    EnemyRowPower = new int[3],
                    PlayerRowFlatBonus = new int[3],
                    LoadLimit = 6,
                };
                blocked.Units.Add(new UnitCard { Id = 0, Name = "A", LoadCost = 1, SlotRarities = new List<int> { 1 } });
                var planB = solver.Solve(blocked);
                TestRunner.Equal(0, planB.Layout.Count, "没有可用格时不应该放任何部件");
            });

            TestRunner.Test("穷举摆位: 结果与朴素穷举一致，且动作可回放", () =>
            {
                var rng = new Random(24680);
                var evaluator = new ApproxPlacementEvaluator();

                for (int i = 0; i < 10; i++)
                {
                    var state = PlacementFactory.Blueprint(3, 2, 3, 7);
                    state.LoadLimit = 3;
                    state.TokenPool.AddRange(PlacementFactory.ExpectedPool(rng, 2, 3));

                    int unitCount = rng.Next(2, 4);
                    for (int u = 0; u < unitCount; u++)
                    {
                        var card = new UnitCard { Id = u, Name = "P" + u, LoadCost = 1, Powerable = true };
                        int slots = rng.Next(1, 3);
                        for (int sl = 0; sl < slots; sl++) card.SlotRarities.Add(rng.Next(1, 4));
                        state.Units.Add(card);

                        if (rng.NextDouble() < 0.6 && state.CurrentLayout.Count < 3)
                        {
                            var free = state.Grid.UsableCells()
                                .Where(c => state.CurrentLayout.All(pp => state.Grid.IndexOf(pp.X, pp.Y) != c)).ToList();
                            if (free.Count > 0)
                            {
                                int cell = free[rng.Next(free.Count)];
                                state.CurrentLayout.Add(new PlacementSlot(u, state.Grid.XOf(cell), state.Grid.YOf(cell)));
                            }
                        }
                    }

                    var solver = new ExactPlacementSolver(evaluator, new ExactPlacementSolverOptions { TimeBudgetSeconds = 5 });
                    var plan = solver.Solve(state);

                    double brute = BruteForcePlacement(state, evaluator);
                    TestRunner.Near(brute, plan.Score, 1e-6, "case " + i + " 穷举应等于朴素穷举最优");
                    TestRunner.True(plan.IsExact, "case " + i + " 应该枚举完成");
                    ApplyMovesAndCompare(state, plan);
                }
            });

            TestRunner.Test("穷举摆位: 锁定部件保持原位", () =>
            {
                var state = PlacementFactory.Blueprint(3, 2, 0, 0);
                state.LoadLimit = 3;

                var support = state.Place(0, 1, 0, 1);
                support.Pinned = true;
                support.Powerable = false;

                var cannon = state.Bench(1, 1, 1, 1);

                var plan = new ExactPlacementSolver(new ApproxPlacementEvaluator()).Solve(state);

                var supportSlot = plan.Layout.First(x => x.UnitId == 0);
                TestRunner.Equal(1, supportSlot.X, "辅助部件 X 不应改变");
                TestRunner.Equal(0, supportSlot.Y, "辅助部件 Y 不应改变");
            });

            TestRunner.Test("性能: 6 部件 / 20 格摆位求解", () =>
            {
                var state = PlacementFactory.RandomPlacement(new Random(1234), new PlacementGenOptions
                {
                    Width = 5,
                    Height = 4,
                    MinUnits = 6,
                    MaxUnits = 6,
                    LoadLimit = 6,
                });

                var sw = Stopwatch.StartNew();
                var plan = new PlacementSolver(new ApproxPlacementEvaluator()).Solve(state);
                sw.Stop();

                Console.WriteLine("        摆位 6 部件/30k iter 耗时 " + sw.ElapsedMilliseconds + "ms, score=" + plan.Score.ToString("0.##"));
                TestRunner.True(sw.ElapsedMilliseconds < 3000, "摆位求解应在 3 秒内");
            });
        }

        /// <summary>
        /// 把 LayoutMove 序列真的执行一遍，验证：每一步合法、且最终结果等于 plan.Layout。
        /// 这保证我们给玩家的操作说明是能照着做的。
        /// </summary>
        private static void ApplyMovesAndCompare(PlacementState state, LayoutPlan plan)
        {
            var cellOfUnit = new Dictionary<int, int>();
            foreach (var slot in state.CurrentLayout)
            {
                cellOfUnit[slot.UnitId] = state.Grid.IndexOf(slot.X, slot.Y);
            }

            foreach (var move in plan.Moves)
            {
                switch (move.Kind)
                {
                    case LayoutMove.MoveKind.Deploy:
                    {
                        int target = state.Grid.IndexOf(move.ToX, move.ToY);
                        TestRunner.True(!cellOfUnit.ContainsKey(move.UnitId), "Deploy 的部件不应已在船上");
                        TestRunner.True(!cellOfUnit.ContainsValue(target), "Deploy 目标格必须是空的");
                        cellOfUnit[move.UnitId] = target;
                        break;
                    }
                    case LayoutMove.MoveKind.Bench:
                    {
                        TestRunner.True(cellOfUnit.ContainsKey(move.UnitId), "Bench 的部件应已在船上");
                        cellOfUnit.Remove(move.UnitId);
                        break;
                    }
                    case LayoutMove.MoveKind.Move:
                    {
                        int target = state.Grid.IndexOf(move.ToX, move.ToY);
                        TestRunner.True(cellOfUnit.ContainsKey(move.UnitId), "Move 的部件应已在船上");
                        TestRunner.True(!cellOfUnit.ContainsValue(target), "Move 目标格必须是空的");
                        cellOfUnit[move.UnitId] = target;
                        break;
                    }
                    case LayoutMove.MoveKind.Swap:
                    {
                        TestRunner.True(cellOfUnit.ContainsKey(move.UnitId), "Swap 的两个部件都应在船上");
                        TestRunner.True(cellOfUnit.ContainsKey(move.OtherUnitId), "Swap 的两个部件都应在船上");
                        int a = cellOfUnit[move.UnitId];
                        int b = cellOfUnit[move.OtherUnitId];
                        cellOfUnit[move.UnitId] = b;
                        cellOfUnit[move.OtherUnitId] = a;
                        break;
                    }
                }
            }

            var expected = plan.Layout
                .Select(p => p.UnitId + "@" + state.Grid.IndexOf(p.X, p.Y))
                .OrderBy(s => s, StringComparer.Ordinal);

            var actual = cellOfUnit
                .Select(kv => kv.Key + "@" + kv.Value)
                .OrderBy(s => s, StringComparer.Ordinal);

            TestRunner.Equal(string.Join(",", expected), string.Join(",", actual), "执行动作后应得到目标布局");
        }

        private static double BruteForcePlacement(PlacementState state, IPlacementEvaluator evaluator)
        {
            var cells = state.Grid.UsableCells();
            var used = new HashSet<int>();
            var layout = new List<PlacementSlot>();
            double best = double.NegativeInfinity;

            void Rec(int unitIndex, int load)
            {
                if (unitIndex == state.Units.Count)
                {
                    double score = evaluator.Evaluate(state, layout, out _);
                    if (score > best) best = score;
                    return;
                }

                var card = state.Units[unitIndex];

                // 不上船
                Rec(unitIndex + 1, load);

                if (load + card.LoadCost > state.LoadLimit) return;

                for (int i = 0; i < cells.Count; i++)
                {
                    int cell = cells[i];
                    if (!used.Add(cell)) continue;

                    layout.Add(new PlacementSlot(card.Id, state.Grid.XOf(cell), state.Grid.YOf(cell)));
                    Rec(unitIndex + 1, load + card.LoadCost);
                    layout.RemoveAt(layout.Count - 1);
                    used.Remove(cell);
                }
            }

            Rec(0, 0);
            return best;
        }
    }
}
