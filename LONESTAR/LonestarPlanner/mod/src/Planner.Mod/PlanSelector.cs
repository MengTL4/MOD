using LonestarPlanner.Core;

namespace LonestarPlanner.Mod
{
    public sealed class PlanSelection
    {
        public BattleTurnPlan Selected;
        public int Candidates;
        public string Note = string.Empty;
    }

    /// <summary>
    /// 退路：探针不可用时，只用近似模型给一个方案。
    /// 正式路径是 ProbeSearch（引擎实测驱动的搜索）。
    /// </summary>
    public static class PlanSelector
    {
        /// <summary>
        /// 统一的收益权重。平局是否算赢跟着游戏的 Define.defineValue.equalBreak 走。
        /// </summary>
        public static ScorerOptions NewOptions()
        {
            var options = new ScorerOptions();
            try
            {
                options.EqualCountsAsWin = Define.defineValue.equalBreak;
            }
            catch
            {
                // 拿不到就用默认值
            }
            return options;
        }

        public static PlanSelection Select(
            BattleState battleState,
            BattleSnapshot snapshot,
            EnergySolverOptions energyOptions,
            ScorerOptions scorerOptions)
        {
            var selection = new PlanSelection();

            var solver = new EnergySolver(new ApproxScorer(scorerOptions), energyOptions);
            var planner = new BattlePlanner(solver);
            var candidates = planner.PlanCandidates(battleState);
            selection.Candidates = candidates.Count;

            if (candidates.Count == 0)
            {
                selection.Note = "没有候选方案";
                return selection;
            }

            selection.Selected = candidates[0];
            selection.Note = "模型（探针不可用）";
            return selection;
        }
    }
}
