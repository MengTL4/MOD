using System;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 行结算的真实收益模型。
    ///
    /// 依据（反编译 BattleManager 结算段）：
    ///   - 每赢一行：对敌舰造成 result 点伤害，并且破坏条 -1（damageMaker.breakCount）
    ///   - 破坏条归零：敌方整个回合跳过 + 期间对方战力为 0
    ///   - 每输一行：我方舰船掉 result 点血
    ///
    /// 所以"多赢一行"的价值远大于"某一行的差值更大"。
    /// 之前的 diff*权重 模型会去刷分差，而不是去赢行数 —— 这是策略不对的根因。
    /// </summary>
    public static class RowScorer
    {
        /// <summary>
        /// 根据每行差值（或引擎返回的 result）算分。
        /// breakCount &lt; 0 表示未知，不计算破防奖励；enemyHp &lt;= 0 表示未知/已死。
        /// </summary>
        public static double Score(
            int[] diffs,
            int enemyBreakCount,
            int enemyHpLeft,
            ScorerOptions o,
            int playerHpLeft = -1,
            bool enemyBroken = false,
            bool[] playerHasUnit = null,
            bool[] enemyHasUnit = null)
        {
            if (diffs == null || o == null) return 0.0;

            int wins = 0;
            int losses = 0;
            long winMargin = 0;
            long lossMargin = 0;
            int equals = 0;

            for (int i = 0; i < diffs.Length; i++)
            {
                // 反编译 BattleManager 结算段（turnCaculate）：
                //   if (playerShipController == null || enemyShipController == null) continue;
                // 也就是说"某一方这一行没有部件"的行会被**整行跳过**：
                // 不掉血、不破防、不造成伤害。在这个行上堆战力是纯浪费。
                bool pHas = playerHasUnit == null || i >= playerHasUnit.Length || playerHasUnit[i];
                bool eHas = enemyHasUnit == null || i >= enemyHasUnit.Length || enemyHasUnit[i];
                if (!pHas || !eHas) continue;

                int d = diffs[i];
                if (d > 0) { wins++; winMargin += d; }
                else if (d < 0) { losses++; lossMargin += -d; }
                else { equals++; }
            }

            bool die = playerHpLeft > 0 && lossMargin >= playerHpLeft;
            if (die)
            {
                // 这回合会死：其他收益都不重要，只求别死
                return -o.PlayerDeathPenalty;
            }

            bool kill = enemyHpLeft > 0 && winMargin >= enemyHpLeft;
            if (kill)
            {
                // 这回合能击杀：战斗直接结束。
                // 多赢一行、多打伤害都没有额外意义，交给"用更少 token"的平局规则去优化。
                return o.KillBonus;
            }

            double score = 0.0;

            // 敌方已被破防时，"赢下这一行"没有防守/破防收益，只剩伤害
            double winRowValue = enemyBroken ? o.WinRowValue * o.BrokenWinRowValueScale : o.WinRowValue;

            score += wins * winRowValue;
            score += losses * -o.LoseRowPenalty;

            long effectiveWinDamage = winMargin;
            if (enemyHpLeft > 0 && effectiveWinDamage > enemyHpLeft) effectiveWinDamage = enemyHpLeft;
            score += effectiveWinDamage * o.WinMarginWeight;

            score -= lossMargin * o.LoseMarginWeight;

            if (equals > 0)
            {
                score += equals * (o.EqualCountsAsWin ? winRowValue : o.EqualRowValue);
                if (o.EqualCountsAsWin) wins += equals;
            }

            if (enemyBreakCount > 0 && wins >= enemyBreakCount)
            {
                score += o.BreakBonus;
            }

            return score;
        }

        /// <summary>
        /// 搜索剪枝用的宽松上界：假设剩余能量全部转化成胜利行 + 最大伤害 + 破防 + 击杀。
        /// 宁可高估，不能低估（否则会剪掉最优解）。
        /// </summary>
        public static double UpperBound(int[] diffs, int remainingPower, int enemyBreakCount, int enemyHpLeft, ScorerOptions o, int playerHpLeft = -1)
        {
            if (diffs == null || o == null) return remainingPower * 1.0;

            int notWinning = 0;
            for (int i = 0; i < diffs.Length; i++)
            {
                if (diffs[i] <= 0) notWinning++;
            }

            double bound = remainingPower * Math.Max(o.WinMarginWeight, o.LoseMarginWeight);
            bound += notWinning * (o.WinRowValue + o.LoseRowPenalty);
            bound += o.BreakBonus;
            bound += o.KillBonus;
            bound += o.PlayerDeathPenalty;
            return bound;
        }
    }
}
