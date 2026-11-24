using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    /// <summary>一次"回合决策"：先移动几行，再按哪套能量方案装载。</summary>
    public sealed class BattleTurnPlan
    {
        /// <summary>0 = 不移动；正负号由 LaneAlignment 的方向约定决定。</summary>
        public int Shift;

        public Plan Energy;

        public double Score;

        public string Note = string.Empty;

        public bool Moves
        {
            get { return Shift != 0; }
        }

        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            if (Shift != 0) sb.AppendLine("先" + (Shift > 0 ? "+" : "") + Shift + " 行");
            else sb.AppendLine("不移动");
            if (Energy != null) sb.Append(Energy.Describe());
            return sb.ToString();
        }
    }

    /// <summary>
    /// 回合规划器：把"要不要移动"和"能量怎么分"放在一起比较。
    ///
    /// 之所以要联合优化：移动改变车道对齐，而车道对齐决定了哪些行值得投资源。
    /// 先算能量再决定移动，等于默认"行对齐固定"，很容易给出反的建议。
    ///
    /// 现在的实现是枚举所有可行移动（通常 -1/0/+1），每个都用能量求解器算一遍。
    /// 移动收益完全来自"对齐变化"，不模拟移动触发的技能（那需要引擎探针）。
    /// </summary>
    public sealed class BattlePlanner
    {
        private readonly EnergySolver _energySolver;

        /// <summary>
        /// 每次移动的机会成本。currentMove 是战斗级资源（靠技能回复），
        /// 不扣分的话求解器会因为一点点伤害差就浪费掉移动次数。
        /// </summary>
        public double MovePenalty = 8.0;

        /// <summary>
        /// 是否剪掉"不改变胜负/破防/击杀"的多余装载。
        /// 默认关闭：在新目标函数下，多余能量仍然在造成真实 HP 伤害，
        /// 盲目剪掉会让"为了伤害去移动"这种正确决策失去价值。
        /// 只有在做多回合规划（判断该不该留 token）时才建议打开。
        /// </summary>
        public bool PruneRedundantSteps = false;

        public BattlePlanner(EnergySolver energySolver)
        {
            _energySolver = energySolver ?? throw new ArgumentNullException(nameof(energySolver));
        }

        public BattleTurnPlan Plan(BattleState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var lanes = state.Lanes;
            if (lanes == null || lanes.MaxShift <= 0)
            {
                return new BattleTurnPlan
                {
                    Shift = 0,
                    Energy = _energySolver.Solve(state),
                    Score = 0,
                    Note = lanes == null ? "没有车道信息，按当前对齐计算" : "本回合无法移动",
                };
            }

            BattleTurnPlan best = null;

            // 从 0 开始：同分时优先"不移动"，省下 currentMove
            for (int d = 0; d <= lanes.MaxShift; d++)
            {
                if (d == 0)
                {
                    best = Evaluate(state, 0);
                }
                else
                {
                    var plus = Evaluate(state, d);
                    if (plus != null && (best == null || plus.Score > best.Score + 1e-9)) best = plus;

                    var minus = Evaluate(state, -d);
                    if (minus != null && (best == null || minus.Score > best.Score + 1e-9)) best = minus;
                }
            }

            return best;
        }

        /// <summary>
        /// 返回所有候选（不移动 + 每个可行位移），按模型分从高到低。
        /// 给"引擎探针"用：先粗筛，再让游戏自己算真实结果。
        /// </summary>
        public System.Collections.Generic.List<BattleTurnPlan> PlanCandidates(BattleState state)
        {
            var list = new System.Collections.Generic.List<BattleTurnPlan>();
            var lanes = state.Lanes;
            int maxShift = lanes == null ? 0 : lanes.MaxShift;

            for (int d = 0; d <= maxShift; d++)
            {
                if (d == 0)
                {
                    var zero = Evaluate(state, 0);
                    if (zero != null) list.Add(zero);
                    continue;
                }

                var plus = Evaluate(state, d);
                if (plus != null) list.Add(plus);

                var minus = Evaluate(state, -d);
                if (minus != null) list.Add(minus);
            }

            list.Sort((a, b) => b.Score.CompareTo(a.Score));
            return list;
        }

        private BattleTurnPlan Evaluate(BattleState state, int shift)
        {
            var effective = state.Clone();

            // 有车道信息时，所有候选位移（包括 0）都从 Lanes 推导，
            // 避免"d=0 用旧快照、d≠0 用推导值"的口径不一致。
            if (state.Lanes != null)
            {
                state.Lanes.ApplyShift(effective, shift);
            }

            Plan energy;
            try
            {
                energy = _energySolver.Solve(effective);
            }
            catch (ArgumentException)
            {
                return null;
            }

            if (PruneRedundantSteps)
            {
                energy = PlanPruner.Prune(effective, energy);
            }

            // 修剪后用模拟重算一次分数，保证候选排序和最终评分一致
            BattleOutcome outcome;
            List<UnitState> units;
            double score = PlanEvaluator.Score(effective, energy, _energySolver.Scorer, out outcome, out units);
            energy.Score = score;
            energy.Outcome = outcome;

            return new BattleTurnPlan
            {
                Shift = shift,
                Energy = energy,
                Score = score - (shift == 0 ? 0.0 : MovePenalty),
                Note = shift == 0 ? string.Empty : "移动后按新对齐重新分配",
            };
        }
    }
}
