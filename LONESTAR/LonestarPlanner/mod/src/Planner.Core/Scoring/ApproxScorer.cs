using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 方案打分器。核心库只依赖这个接口，因此"近似公式"和"游戏引擎探针"可以互换。
    /// </summary>
    public interface IAssignmentScorer
    {
        /// <summary>给最终布局打分，并输出可展示的局面推演。</summary>
        double Score(BattleState state, IReadOnlyList<UnitState> units, out BattleOutcome outcome);

        /// <summary>
        /// 与 Score 等价的快速版本：不产出 BattleOutcome、不分配对象，供搜索内部逐节点剪枝使用。
        /// 实现必须和 Score 的口径一致（测试里有交叉校验）。
        /// </summary>
        double EstimateScore(BattleState state, IReadOnlyList<UnitState> units);

        /// <summary>
        /// 剩余 token 还能带来的上界收益（必须宽松，宁可高估）。
        /// 搜索靠它剪枝：currentScore + bound &lt;= best 就可以砍掉。
        /// </summary>
        double UpperBoundRemainingGain(BattleState state, IReadOnlyList<UnitState> units, IReadOnlyList<EnergyToken> remainingTokens);
    }

    /// <summary>
    /// 近似评估器：按游戏的行的算式 result = playerTotal - enemyTotal 估算，
    /// 再加"装满部件触发技能"的固定价值。
    ///
    /// 这是离线跑搜索和造数据测试用的默认实现；
    /// 进游戏后可以换成引擎探针（同样的接口，用游戏自己算的行结果）。
    /// </summary>
    public sealed class ApproxScorer : IAssignmentScorer
    {
        public readonly ScorerOptions Options;

        public ApproxScorer()
            : this(new ScorerOptions())
        {
        }

        public ApproxScorer(ScorerOptions options)
        {
            Options = options ?? new ScorerOptions();
        }

        public double EstimateScore(BattleState state, IReadOnlyList<UnitState> units)
        {
            return ComputeRows(state, units, null);
        }

        private int[] _diffs;

        public double Score(BattleState state, IReadOnlyList<UnitState> units, out BattleOutcome outcome)
        {
            outcome = new BattleOutcome();
            return ComputeRows(state, units, outcome);
        }

        /// <summary>
        /// 公共计算核心。outcome 传 null 时不分配任何对象（供 EstimateScore 用）。
        /// </summary>
        private double ComputeRows(BattleState state, IReadOnlyList<UnitState> units, BattleOutcome outcome)
        {
            int rowCount = state.RowCount;
            if (_rowPower == null || _rowPower.Length != rowCount) _rowPower = new int[rowCount];
            else Array.Clear(_rowPower, 0, _rowPower.Length);

            if (_diffs == null || _diffs.Length != rowCount) _diffs = new int[rowCount];

            int totalPower = 0;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u.Row < 0 || u.Row >= rowCount) continue;
                _rowPower[u.Row] += u.LoadedPower;
                totalPower += u.LoadedPower;
            }

            if (outcome != null)
            {
                outcome.Rows = new RowOutcome[rowCount];
                outcome.TotalPlayerPower = totalPower;
            }

            for (int r = 0; r < rowCount; r++)
            {
                int player = _rowPower[r];
                if (state.PlayerRowFlatBonus != null && r < state.PlayerRowFlatBonus.Length)
                {
                    player += state.PlayerRowFlatBonus[r];
                }

                int enemy = state.EnemyRowPower != null && r < state.EnemyRowPower.Length ? state.EnemyRowPower[r] : 0;
                _diffs[r] = player - enemy;

                if (outcome != null)
                {
                    outcome.Rows[r] = new RowOutcome
                    {
                        Row = r,
                        PlayerPower = player,
                        EnemyPower = enemy,
                        Diff = _diffs[r],
                        IsWin = _diffs[r] > 0 || (_diffs[r] == 0 && Options.EqualCountsAsWin),
                        Value = 0,
                    };
                }
            }

            double score = RowScorer.Score(_diffs, state.EnemyBreakCount, state.EnemyHpLeft, Options, state.PlayerHpLeft, state.EnemyBroken, state.PlayerRowHasUnit, state.EnemyRowHasUnit);

            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (!u.IsFull) continue;
                if (outcome != null) outcome.CompletedUnitIds.Add(u.Id);
                score += u.FullLoadValue > 0 ? u.FullLoadValue : Options.DefaultFullLoadValue;
            }

            return score;
        }

        public double UpperBoundRemainingGain(BattleState state, IReadOnlyList<UnitState> units, IReadOnlyList<EnergyToken> remainingTokens)
        {
            if (remainingTokens == null || remainingTokens.Count == 0) return 0.0;

            int remainingPower = 0;
            for (int i = 0; i < remainingTokens.Count; i++) remainingPower += remainingTokens[i].Power;

            int rowCount = state.RowCount;
            if (_rowPower == null || _rowPower.Length != rowCount) _rowPower = new int[rowCount];
            else Array.Clear(_rowPower, 0, _rowPower.Length);
            if (_diffs == null || _diffs.Length != rowCount) _diffs = new int[rowCount];

            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u.Row < 0 || u.Row >= rowCount) continue;
                _rowPower[u.Row] += u.LoadedPower;
            }

            for (int r = 0; r < rowCount; r++)
            {
                int player = _rowPower[r];
                if (state.PlayerRowFlatBonus != null && r < state.PlayerRowFlatBonus.Length) player += state.PlayerRowFlatBonus[r];
                int enemy = state.EnemyRowPower != null && r < state.EnemyRowPower.Length ? state.EnemyRowPower[r] : 0;
                _diffs[r] = player - enemy;
            }

            double bound = RowScorer.UpperBound(_diffs, remainingPower, state.EnemyBreakCount, state.EnemyHpLeft, Options, state.PlayerHpLeft);

            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u.IsFull) continue;
                bound += u.FullLoadValue > 0 ? u.FullLoadValue : Options.DefaultFullLoadValue;
            }

            return bound;
        }

        [NonSerialized]
        private int[] _rowPower;
    }
}
