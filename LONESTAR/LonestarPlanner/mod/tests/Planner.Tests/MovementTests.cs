using System;
using LonestarPlanner.Core;

namespace LonestarPlanner.Tests
{
    public static class MovementTests
    {
        public static void Register()
        {
            TestRunner.Test("移动: 会为了避开强行而移动（+1）", () =>
            {
                var state = StateFactory.Blueprint(2, 6, 0);
                var unit = state.Unit(0, 0);
                unit.Slot(PowerColor.White);
                unit.Slot(PowerColor.White);
                state.Token(0, 3, PowerColor.White);
                state.Token(1, 3, PowerColor.White);

                state.Lanes = new LaneAlignment
                {
                    PlayerRowToBattleRow = new[] { 0, 1 },
                    EnemyPowerByBattleRow = new[] { 6, 0 },
                    MaxShift = 1,
                };

                var plan = new BattlePlanner(NewEnergySolver()).Plan(state);

                TestRunner.Equal(1, plan.Shift, "应该移动 1 行，把 6 点战力从对面 6 的强行挪到空行");
                TestRunner.Greater(plan.Score, 5.0, "移动后的分数应明显更高");
            });

            TestRunner.Test("移动: 支持反向移动（-1）", () =>
            {
                var state = StateFactory.Blueprint(2, 0, 0);
                var unit = state.Unit(0, 0);
                unit.Slot(PowerColor.White);
                unit.Slot(PowerColor.White);
                state.Token(0, 3, PowerColor.White);
                state.Token(1, 3, PowerColor.White);

                state.Lanes = new LaneAlignment
                {
                    PlayerRowToBattleRow = new[] { 1, 2 },
                    EnemyPowerByBattleRow = new[] { 0, 6, 9 },
                    MaxShift = 1,
                };
                state.EnemyRowPower[0] = 6;
                state.EnemyRowPower[1] = 0;

                var plan = new BattlePlanner(NewEnergySolver()).Plan(state);

                TestRunner.Equal(-1, plan.Shift, "应该向上移动 1 行，离开对面 6 的强行");
            });

            TestRunner.Test("移动: 没有移动次数时不动", () =>
            {
                var state = StateFactory.Blueprint(2, 6, 0);
                var unit = state.Unit(0, 0);
                unit.Slot(PowerColor.White);
                state.Token(0, 3, PowerColor.White);

                state.Lanes = new LaneAlignment
                {
                    PlayerRowToBattleRow = new[] { 0, 1 },
                    EnemyPowerByBattleRow = new[] { 6, 0 },
                    MaxShift = 0,
                };

                var plan = new BattlePlanner(NewEnergySolver()).Plan(state);
                TestRunner.Equal(0, plan.Shift, "移动预算为 0 不应移动");
            });

            TestRunner.Test("移动: 同分时优先不移动（省下 currentMove）", () =>
            {
                var state = StateFactory.Blueprint(2, 0, 0);
                var unit = state.Unit(0, 0);
                unit.Slot(PowerColor.White);
                state.Token(0, 3, PowerColor.White);

                state.Lanes = new LaneAlignment
                {
                    PlayerRowToBattleRow = new[] { 0, 1 },
                    EnemyPowerByBattleRow = new[] { 0, 0 },
                    MaxShift = 1,
                };

                var plan = new BattlePlanner(NewEnergySolver()).Plan(state);
                TestRunner.Equal(0, plan.Shift, "移动不带来收益时不应浪费移动次数");
            });

            TestRunner.Test("移动: PlanCandidates 给出 0/±1 并按分数降序", () =>
            {
                var state = StateFactory.Blueprint(2, 6, 0);
                var unit = state.Unit(0, 0);
                unit.Slot(PowerColor.White);
                unit.Slot(PowerColor.White);
                state.Token(0, 3, PowerColor.White);
                state.Token(1, 3, PowerColor.White);

                state.Lanes = new LaneAlignment
                {
                    PlayerRowToBattleRow = new[] { 0, 1 },
                    EnemyPowerByBattleRow = new[] { 6, 3 },
                    MaxShift = 1,
                };

                var list = new BattlePlanner(NewEnergySolver()).PlanCandidates(state);

                TestRunner.Equal(3, list.Count, "应包含 d=0、d=+1、d=-1");
                for (int i = 1; i < list.Count; i++)
                {
                    TestRunner.True(list[i - 1].Score >= list[i].Score - 1e-9, "候选应按模型分降序");
                }
                TestRunner.True(list.Exists(p => p.Shift == 0), "必须包含不移动");
                TestRunner.True(list.Exists(p => p.Shift == 1), "必须包含 +1");
                TestRunner.True(list.Exists(p => p.Shift == -1), "必须包含 -1");
            });

            TestRunner.Test("移动: ApplyShift 不改动原状态", () =>
            {
                var state = StateFactory.Blueprint(2, 6, 0);
                state.Lanes = new LaneAlignment
                {
                    PlayerRowToBattleRow = new[] { 0, 1 },
                    EnemyPowerByBattleRow = new[] { 6, 0 },
                    MaxShift = 1,
                };

                var clone = state.Clone();
                state.Lanes.ApplyShift(clone, 1);

                TestRunner.Equal(6, state.EnemyRowPower[0], "原状态第 1 行仍是 6");
                TestRunner.Equal(0, clone.EnemyRowPower[0], "移动后第 1 行应变成空行的 0");
            });
        }

        private static EnergySolver NewEnergySolver()
        {
            return new EnergySolver(new ApproxScorer());
        }
    }
}
