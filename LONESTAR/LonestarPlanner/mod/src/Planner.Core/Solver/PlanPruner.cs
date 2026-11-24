using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 去掉"装了也不改变结果"的装载步骤。
    ///
    /// 为什么需要它：
    ///   - 槽位每回合清空，但 token 用掉就没了（手牌跨回合保留，上限 10）
    ///   - 所以"把 9 点堆进一个已经赢的行"只是在刷伤害，代价是少一枚下回合能用的 token
    ///   - 真正值得花 token 的是：改变某一行的输赢 / 凑够破坏条 / 打出致命伤
    ///
    /// 判定：逐个试着删掉某一步并重新模拟，如果
    ///   每行胜负符号、是否破防、是否击杀 都没变，就把它删掉（token 留手）。
    /// </summary>
    public static class PlanPruner
    {
        public struct Signature : IEquatable<Signature>
        {
            public int Wins;
            public int Losses;
            public bool Break;
            public bool Kill;

            public bool Equals(Signature other)
            {
                return Wins == other.Wins && Losses == other.Losses && Break == other.Break && Kill == other.Kill;
            }

            public override bool Equals(object obj)
            {
                return obj is Signature other && Equals(other);
            }

            public override int GetHashCode()
            {
                return Wins * 397 ^ Losses * 31 ^ (Break ? 1 : 0) ^ (Kill ? 2 : 0);
            }
        }

        public static Plan Prune(BattleState state, Plan plan)
        {
            if (state == null || plan == null || plan.Steps.Count <= 1) return plan;

            var best = new List<PlanStep>(plan.Steps);
            var signature = Sign(state, best);

            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = best.Count - 1; i >= 0; i--)
                {
                    var candidate = new List<PlanStep>(best);
                    candidate.RemoveAt(i);

                    var candidateSignature = Sign(state, candidate);
                    if (!candidateSignature.Equals(signature)) continue;

                    best = candidate;
                    changed = true;
                }
            }

            if (best.Count == plan.Steps.Count) return plan;

            var pruned = new Plan
            {
                Steps = best,
                Score = plan.Score,
                Outcome = plan.Outcome,
                IsExact = plan.IsExact,
                IsOptimisticBound = plan.IsOptimisticBound,
                NodesEvaluated = plan.NodesEvaluated,
            };

            var used = new HashSet<int>();
            for (int i = 0; i < best.Count; i++) used.Add(best[i].TokenId);
            for (int i = 0; i < state.Hand.Count; i++)
            {
                if (!used.Contains(state.Hand[i].Id)) pruned.UnusedTokenIds.Add(state.Hand[i].Id);
            }

            return pruned;
        }

        /// <summary>按顺序模拟一遍装载，返回"结果签名"。</summary>
        public static Signature Sign(BattleState state, IReadOnlyList<PlanStep> steps)
        {
            var units = new UnitState[state.Units.Count];
            for (int i = 0; i < units.Length; i++) units[i] = state.Units[i].Clone();

            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                UnitState unit = null;
                for (int u = 0; u < units.Length; u++)
                {
                    if (units[u].Id == step.UnitId) { unit = units[u]; break; }
                }
                if (unit == null) continue;

                var token = new EnergyToken(step.TokenId, step.TokenPower, step.TokenColor);
                int slot;
                if (!LoadRules.TryLoad(unit, token, out slot)) continue;
            }

            var rows = new int[state.RowCount];
            if (state.PlayerRowFlatBonus != null)
            {
                for (int r = 0; r < rows.Length && r < state.PlayerRowFlatBonus.Length; r++) rows[r] = state.PlayerRowFlatBonus[r];
            }
            for (int u = 0; u < units.Length; u++)
            {
                if (units[u].Row >= 0 && units[u].Row < rows.Length) rows[units[u].Row] += units[u].LoadedPower;
            }

            int wins = 0, losses = 0;
            long damage = 0;
            for (int r = 0; r < rows.Length; r++)
            {
                int enemy = state.EnemyRowPower != null && r < state.EnemyRowPower.Length ? state.EnemyRowPower[r] : 0;
                int diff = rows[r] - enemy;
                if (diff > 0) { wins++; damage += diff; }
                else if (diff < 0) losses++;
                else if (state.EqualCountsAsWin) wins++;
            }

            return new Signature
            {
                Wins = wins,
                Losses = losses,
                Break = state.EnemyBreakCount > 0 && wins >= state.EnemyBreakCount,
                Kill = state.EnemyHpLeft > 0 && damage >= state.EnemyHpLeft,
            };
        }
    }
}
