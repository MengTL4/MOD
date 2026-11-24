using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    /// <summary>把一份"步骤列表"真的模拟到状态上，再用评分器算分。修剪/复核都靠它。</summary>
    public static class PlanEvaluator
    {
        public static double Score(BattleState state, Plan plan, IAssignmentScorer scorer, out BattleOutcome outcome, out List<UnitState> units)
        {
            units = Simulate(state, plan);
            return scorer.Score(state, units, out outcome);
        }

        public static List<UnitState> Simulate(BattleState state, Plan plan)
        {
            var units = new List<UnitState>(state.Units.Count);
            for (int i = 0; i < state.Units.Count; i++) units.Add(state.Units[i].Clone());

            if (plan == null) return units;

            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                UnitState unit = null;
                for (int u = 0; u < units.Count; u++)
                {
                    if (units[u].Id == step.UnitId) { unit = units[u]; break; }
                }
                if (unit == null) continue;

                int slot;
                LoadRules.TryLoad(unit, new EnergyToken(step.TokenId, step.TokenPower, step.TokenColor), out slot);
            }

            return units;
        }
    }
}
