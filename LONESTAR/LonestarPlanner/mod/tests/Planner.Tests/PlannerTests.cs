using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using LonestarPlanner.Core;

namespace LonestarPlanner.Tests
{
    public static class PlannerTests
    {
        public static void Register()
        {
            TestRunner.Test("LoadRules: 低稀有度槽装不下时跳过，而不是卡住", () =>
            {
                var state = StateFactory.Blueprint(1, 0);
                var unit = state.Unit(0, 0);
                unit.Slot(PowerColor.Purple);
                unit.Slot(PowerColor.White);

                int slot = LoadRules.FindReceivingSlot(unit, new EnergyToken(0, 2, PowerColor.White));
                TestRunner.Equal(1, slot, "白色 token 应跳过紫色槽，落入第 2 个槽");
            });

            TestRunner.Test("LoadRules: 颜色不足的槽一个都装不进", () =>
            {
                var state = StateFactory.Blueprint(1, 0);
                var unit = state.Unit(0, 0);
                unit.Slot(PowerColor.Purple);
                unit.Slot(PowerColor.Blue);

                int slot = LoadRules.FindReceivingSlot(unit, new EnergyToken(0, 5, PowerColor.White));
                TestRunner.Equal(-1, slot, "白色 token 不能装入紫色/蓝色槽");
            });

            TestRunner.Test("LoadRules: 被干扰的槽不可用", () =>
            {
                var state = StateFactory.Blueprint(1, 0);
                var unit = state.Unit(0, 0);
                unit.Slot(PowerColor.White, disturbed: true);
                unit.Slot(PowerColor.White);

                int slot = LoadRules.FindReceivingSlot(unit, new EnergyToken(0, 3, PowerColor.White));
                TestRunner.Equal(1, slot, "应跳过被封锁的槽");
            });

            TestRunner.Test("求解器: 装载顺序是决策的一部分（白先紫后 > 紫先白后）", () =>
            {
                var state = StateFactory.Blueprint(1, 0);
                var unit = state.Unit(0, 0, "双槽件");
                unit.Slot(PowerColor.White);
                unit.Slot(PowerColor.Purple);
                state.Token(0, 2, PowerColor.White);
                state.Token(1, 9, PowerColor.Purple);

                var plan = NewSolver().Solve(state);

                TestRunner.Equal(11, plan.Outcome.Rows[0].PlayerPower, "最优顺序应得到 2+9=11 的战力");
                TestRunner.Equal(2, plan.Steps.Count, "两枚 token 都应被装入");
                TestRunner.Equal(2, plan.Steps[0].TokenPower, "第一步必须是白色 token（否则紫色槽被白槽占住）");
                TestRunner.True(plan.Outcome.CompletedUnitIds.Contains(0), "部件应被装满");
            });

            TestRunner.Test("求解器: 严格遵守颜色门槛，不会硬塞", () =>
            {
                var state = StateFactory.Blueprint(1, 0);
                var unit = state.Unit(0, 0);
                unit.Slot(PowerColor.Purple);
                unit.Slot(PowerColor.Purple);
                state.Token(0, 5, PowerColor.White);
                state.Token(1, 1, PowerColor.Purple);

                var plan = NewSolver().Solve(state);

                TestRunner.Equal(1, plan.Steps.Count, "只有紫色 token 能装入");
                TestRunner.Equal(1, plan.Steps[0].TokenId, "被使用的是紫色 token");
                TestRunner.True(plan.UnusedTokenIds.Contains(0), "白色 token 应留在手上");
            });

            TestRunner.Test("求解器: 会为了装满部件而牺牲'平均铺开'", () =>
            {
                var state = StateFactory.Blueprint(1, 6);
                var a = state.Unit(0, 0, "双槽件");
                a.Slot(PowerColor.White);
                a.Slot(PowerColor.White);
                var b = state.Unit(1, 0, "单槽件");
                b.Slot(PowerColor.White);
                state.Token(0, 3, PowerColor.White);
                state.Token(1, 3, PowerColor.White);

                var plan = NewSolver().Solve(state);

                TestRunner.Equal(2, plan.Steps.Count, "两枚 token 都应用上");
                TestRunner.True(plan.Steps.All(s => s.UnitId == 0), "两枚都应进入双槽件以触发装满效果");
                TestRunner.True(plan.Outcome.CompletedUnitIds.Contains(0), "双槽件应装满");
            });

            TestRunner.Test("求解器: 会集中战力去翻转某一行，而不是平摊", () =>
            {
                var state = StateFactory.Blueprint(2, 0, 4);
                var row0 = state.Unit(0, 0);
                row0.Slot(PowerColor.White);
                var row1 = state.Unit(1, 1);
                row1.Slot(PowerColor.White);
                row1.Slot(PowerColor.White);
                state.Token(0, 3, PowerColor.White);
                state.Token(1, 3, PowerColor.White);

                var plan = NewSolver().Solve(state);

                TestRunner.True(plan.Steps.All(s => s.UnitId == 1), "两枚 3 点应集中在第 2 行，把 4 打下来");
                TestRunner.Equal(2, plan.Outcome.Rows[1].Diff, "第 2 行净胜 2");
            });

            TestRunner.Test("目标函数: 赢两行 优于 把一行赢更多（行数是主要收益）", () =>
            {
                // 敌方两行都是"差一点能翻"的状态，手牌总量刚好够两行都赢
                var state = StateFactory.Blueprint(2, 5, 4);

                var row0 = state.Unit(0, 0, "四槽件");
                for (int i = 0; i < 4; i++) row0.Slot(PowerColor.White);

                var row1 = state.Unit(1, 1, "双槽件");
                row1.Slot(PowerColor.White);
                row1.Slot(PowerColor.White);

                state.Token(0, 3, PowerColor.White);
                state.Token(1, 3, PowerColor.White);
                state.Token(2, 3, PowerColor.White);
                state.Token(3, 3, PowerColor.White);

                var plan = NewSolver().Solve(state);
                int wins = plan.Outcome.Rows.Count(r => r.Diff > 0);

                TestRunner.Equal(2, wins, "应该把两行都赢下来，而不是把六点全堆进一行");
                TestRunner.True(plan.Steps.Any(s => s.UnitId == 0), "第 1 行要有能量");
                TestRunner.True(plan.Steps.Any(s => s.UnitId == 1), "第 2 行也要有能量");
            });

            TestRunner.Test("目标函数: 赢够行数能清空破坏条时给额外奖励", () =>
            {
                var state = StateFactory.Blueprint(2, 2, 2);
                state.EnemyBreakCount = 2;

                var row0 = state.Unit(0, 0, "双槽件");
                row0.Slot(PowerColor.White);
                row0.Slot(PowerColor.White);

                var row1 = state.Unit(1, 1, "单槽件");
                row1.Slot(PowerColor.White);

                state.Token(0, 5, PowerColor.White);
                state.Token(1, 5, PowerColor.White);
                state.Token(2, 5, PowerColor.White);

                var plan = NewSolver().Solve(state);

                TestRunner.Equal(2, plan.Outcome.Rows.Count(r => r.Diff > 0), "两行都要赢（= 2 点破坏，刚好破防）");
                TestRunner.True(plan.Steps.Count >= 2, "至少两枚 token 分到两行");
            });

            TestRunner.Test("目标函数: 会主动避免'这回合会被打死'的分法", () =>
            {
                var state = StateFactory.Blueprint(2, 4, 4);
                state.PlayerHpLeft = 2; // 任何一行输掉 4 点都会致死

                var row0 = state.Unit(0, 0);
                row0.Slot(PowerColor.White);
                row0.Slot(PowerColor.White);

                var row1 = state.Unit(1, 1);
                row1.Slot(PowerColor.White);
                row1.Slot(PowerColor.White);

                state.Token(0, 4, PowerColor.White);
                state.Token(1, 4, PowerColor.White);

                var plan = NewSolver().Solve(state);

                TestRunner.True(plan.Outcome.Rows.All(r => r.Diff >= 0), "不应留下任何会打死自己的行");
                TestRunner.True(plan.Steps.Any(s => s.UnitId == 0) && plan.Steps.Any(s => s.UnitId == 1), "两行都要照顾到");
            });

            TestRunner.Test("目标函数: 能击杀时，多赢几行不该加分（战斗直接结束）", () =>
            {
                var o = new ScorerOptions();
                var oneWinKill = new[] { 5, 0, 0 };
                var threeWinKill = new[] { 5, 5, 5 };

                double a = RowScorer.Score(oneWinKill, -1, 5, o, -1, false);
                double b = RowScorer.Score(threeWinKill, -1, 5, o, -1, false);

                TestRunner.Equal(a, b, "只要造成致命伤，战斗就结束，多余的赢行没有价值");
            });

            TestRunner.Test("目标函数: 敌方空行不算赢、也不产生伤害和击杀", () =>
            {
                var o = new ScorerOptions();

                var diffs = new[] { 8, -7, -3 };
                var pHas = new[] { true, true, true };
                var eHas = new[] { false, true, true };

                double s = RowScorer.Score(diffs, 2, 8, o, 24, false, pHas, eHas);
                TestRunner.True(s < 0, "空行不该被当成胜利（score=" + s.ToString("0.##") + "）");

                var eHasAll = new[] { true, true, true };
                double s2 = RowScorer.Score(diffs, 2, 8, o, 24, false, pHas, eHasAll);
                TestRunner.True(s2 > s, "有敌人的行赢下来才值钱");

                var pEmpty = new[] { false, false, false };
                double s3 = RowScorer.Score(diffs, 2, 8, o, 24, false, pEmpty, eHasAll);
                TestRunner.Near(0.0, s3, 1e-9, "我方没部件的行不受伤");
            });

            TestRunner.Test("目标函数: 敌方已破防时优先堆伤害，而不是平铺", () =>
            {
                var o = new ScorerOptions();
                var spread = new[] { 1, 1, 1 };
                var focus = new[] { 9, 0, 0 };

                double sSpread = RowScorer.Score(spread, 0, 100, o, -1, true);
                double sFocus = RowScorer.Score(focus, 0, 100, o, -1, true);

                TestRunner.Greater(sFocus, sSpread, "破防后赢行没有防守意义，应该集中打伤害");
            });

            TestRunner.Test("目标函数: 会致死的分法一律压到最低", () =>
            {
                var o = new ScorerOptions();
                var lethal = new[] { 10, -3 };
                var safe = new[] { 0, 0 };

                double sLethal = RowScorer.Score(lethal, -1, 100, o, 3, false);
                double sSafe = RowScorer.Score(safe, -1, 100, o, 3, false);

                TestRunner.Greater(sSafe, sLethal, "宁可这回合不打伤害，也不能被打死");
            });

            TestRunner.Test("求解器: 精确解与'评估每个中间状态'的朴素穷举一致", () =>
            {
                var rng = new Random(20260817);
                var options = new StateGenOptions
                {
                    MinRows = 2,
                    MaxRows = 3,
                    MinUnits = 2,
                    MaxUnits = 3,
                    MinSlotsPerUnit = 1,
                    MaxSlotsPerUnit = 2,
                    MinHand = 3,
                    MaxHand = 4,
                    MinPower = 1,
                    MaxPower = 5,
                    AllowDisturbedSlots = false,
                };

                for (int i = 0; i < 40; i++)
                {
                    var state = StateFactory.RandomBattle(rng, options);
                    var scorer = new ApproxScorer();
                    var solver = new EnergySolver(scorer);

                    var plan = solver.Solve(state);
                    double brute = BruteForceEveryPrefix(state, scorer);

                    TestRunner.Near(brute, plan.Score, 1e-6, "case " + i + " 精确解应等于朴素穷举最优");
                }
            });

            TestRunner.Test("求解器: 束搜索在小规模上与精确解同分", () =>
            {
                var rng = new Random(99991);
                var options = new StateGenOptions
                {
                    MinRows = 2,
                    MaxRows = 3,
                    MinUnits = 2,
                    MaxUnits = 4,
                    MinSlotsPerUnit = 1,
                    MaxSlotsPerUnit = 2,
                    MinHand = 4,
                    MaxHand = 5,
                    MinPower = 1,
                    MaxPower = 6,
                    AllowDisturbedSlots = false,
                };

                for (int i = 0; i < 25; i++)
                {
                    var state = StateFactory.RandomBattle(rng, options);
                    var scorer = new ApproxScorer();

                    var exact = new EnergySolver(scorer, new EnergySolverOptions { ExactTokenLimit = 99, ExactUnitLimit = 99 }).Solve(state);
                    var beam = new EnergySolver(scorer, new EnergySolverOptions { ExactTokenLimit = 0, BeamWidth = 384 }).Solve(state);

                    TestRunner.Near(exact.Score, beam.Score, 1e-6, "case " + i + " 束搜索不应低于精确解");
                    TestRunner.True(beam.IsExact == false, "应走束搜索分支");
                }
            });

            TestRunner.Test("求解器: 同种子结果完全可复现", () =>
            {
                var a = StateFactory.RandomBattles(12345, 1)[0];
                var b = StateFactory.RandomBattles(12345, 1)[0];

                var planA = NewSolver().Solve(a);
                var planB = NewSolver().Solve(b);

                TestRunner.Equal(planA.Score, planB.Score, "分数应一致");
                TestRunner.Equal(
                    string.Join(",", planA.Steps.Select(s => s.TokenId + ">" + s.UnitId)),
                    string.Join(",", planB.Steps.Select(s => s.TokenId + ">" + s.UnitId)),
                    "步骤应一致");
            });

            TestRunner.Test("BattleState: 脏数据会被挡住", () =>
            {
                var bad = StateFactory.Blueprint(1, 0);
                var unit = bad.Unit(0, 0);
                unit.Slot(PowerColor.Purple);
                unit.Slots[0].Loaded = new EnergyToken(0, 3, PowerColor.White); // 颜色不足却已装载

                TestRunner.True(bad.Validate() != null, "应报告非法状态");
                TestRunner.Throws<ArgumentException>(() => NewSolver().Solve(bad), "求解器应拒绝非法状态");
            });

            TestRunner.Test("快照 JSON 可被标准解析器读取", () =>
            {
                var state = StateFactory.RandomBattles(7, 1)[0];
                string json = StateJson.Write(state);

                using var doc = JsonDocument.Parse(json);
                TestRunner.Equal(state.RowCount, doc.RootElement.GetProperty("rowCount").GetInt32(), "rowCount");
                TestRunner.Equal(state.Hand.Count, doc.RootElement.GetProperty("hand").GetArrayLength(), "hand 数量");
                TestRunner.Equal(state.Units.Count, doc.RootElement.GetProperty("units").GetArrayLength(), "unit 数量");
            });

            TestRunner.Test("EstimateScore 与 Score 口径一致（剪枝正确性的前提）", () =>
            {
                var rng = new Random(31337);
                var options = new StateGenOptions { MinRows = 2, MaxRows = 4, MinUnits = 2, MaxUnits = 5, MinHand = 3, MaxHand = 5 };
                for (int i = 0; i < 30; i++)
                {
                    var state = StateFactory.RandomBattle(rng, options);
                    var scorer = new ApproxScorer();
                    var solver = new EnergySolver(scorer);
                    var plan = solver.Solve(state);

                    var replay = state.Clone();
                    foreach (var step in plan.Steps)
                    {
                        var unit = replay.Units.First(u => u.Id == step.UnitId);
                        var token = replay.Hand.First(tk => tk.Id == step.TokenId);
                        int slot = LoadRules.FindReceivingSlot(unit, token);
                        TestRunner.Equal(step.SlotIndex, slot, "回放时槽位应一致");
                        unit.Slots[slot].Loaded = token;
                    }

                    double fast = scorer.EstimateScore(replay, replay.Units);
                    double slow = scorer.Score(replay, replay.Units, out _);
                    TestRunner.Near(slow, fast, 1e-9, "case " + i);
                    TestRunner.Near(plan.Score, slow, 1e-9, "case " + i + " 方案分数应与回放一致");
                }
            });

            TestRunner.Test("性能: 5 token / 6 部件全同色（穷举最坏情况）", () =>
            {
                var state = StateFactory.Blueprint(2, 3, 6);
                for (int i = 0; i < 6; i++)
                {
                    var unit = state.Unit(i, i % 2);
                    unit.Slot(PowerColor.White);
                    unit.Slot(PowerColor.White);
                }
                for (int i = 0; i < 5; i++) state.Token(i, i + 2, PowerColor.White);

                var sw = Stopwatch.StartNew();
                var plan = new EnergySolver(new ApproxScorer(), new EnergySolverOptions { ExactTokenLimit = 5, MaxNodes = 5_000_000 }).Solve(state);
                sw.Stop();

                TestRunner.True(plan.IsExact, "5 token 应能穷举完");
                Console.WriteLine("        5 token/6 部件耗时 " + sw.ElapsedMilliseconds + "ms, 节点 " + plan.NodesEvaluated + ", score=" + plan.Score.ToString("0.##"));
                TestRunner.True(sw.ElapsedMilliseconds < 2000, "穷举最坏情况应在 2 秒内");
            });

            TestRunner.Test("性能: 7 token / 8 部件（走束搜索）在合理时间内出方案", () =>
            {
                var options = new StateGenOptions
                {
                    MinRows = 3,
                    MaxRows = 4,
                    MinUnits = 8,
                    MaxUnits = 8,
                    MinSlotsPerUnit = 1,
                    MaxSlotsPerUnit = 3,
                    MinHand = 7,
                    MaxHand = 7,
                    MinPower = 1,
                    MaxPower = 8,
                };
                var state = StateFactory.RandomBattle(new Random(4242), options);

                var sw = Stopwatch.StartNew();
                var plan = NewSolver().Solve(state);
                sw.Stop();

                TestRunner.True(plan != null, "应产出方案");
                TestRunner.True(plan.Steps.Count > 0, "应至少装一枚 token");
                Console.WriteLine("        7/8 规模耗时 " + sw.ElapsedMilliseconds + "ms, 节点 " + plan.NodesEvaluated + ", score=" + plan.Score.ToString("0.##"));
                TestRunner.True(sw.ElapsedMilliseconds < 3000, "单次求解应在 3 秒内");
            });
        }

        private static EnergySolver NewSolver()
        {
            return new EnergySolver(new ApproxScorer());
        }

        /// <summary>
        /// 朴素穷举：把每个中间状态都拿来评分（不信赖"单调所以只需看终局"的剪枝假设）。
        /// 用来交叉验证求解器的搜索逻辑。
        /// </summary>
        private static double BruteForceEveryPrefix(BattleState state, IAssignmentScorer scorer)
        {
            var units = state.Units.Select(u => u.Clone()).ToArray();
            var used = new bool[state.Hand.Count];
            double best = double.NegativeInfinity;

            void Rec()
            {
                double score = scorer.Score(state, units, out _);
                if (score > best) best = score;

                for (int t = 0; t < state.Hand.Count; t++)
                {
                    if (used[t]) continue;
                    var token = state.Hand[t];
                    for (int u = 0; u < units.Length; u++)
                    {
                        int slot = LoadRules.FindReceivingSlot(units[u], token);
                        if (slot < 0) continue;

                        units[u].Slots[slot].Loaded = token;
                        used[t] = true;
                        Rec();
                        used[t] = false;
                        units[u].Slots[slot].Loaded = null;
                    }
                }
            }

            Rec();
            return best;
        }
    }
}
