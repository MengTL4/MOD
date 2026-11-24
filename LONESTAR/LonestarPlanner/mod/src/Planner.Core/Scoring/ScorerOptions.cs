using System;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 收益权重。默认值按游戏真实结算结构设定：
    /// 赢一行本身价值很高（破坏条 -1 + 少挨打），差值只是附加的 HP 伤害。
    /// </summary>
    public sealed class ScorerOptions
    {
        /// <summary>赢下一行的固定价值（破坏条 -1 + 避免掉血）。</summary>
        public double WinRowValue = 10.0;

        /// <summary>赢行差值的价值（HP 伤害，会被敌舰剩余 HP 封顶）。</summary>
        public double WinMarginWeight = 1.0;

        /// <summary>输掉一行的固定代价（自己掉血）。</summary>
        public double LoseRowPenalty = 10.0;

        /// <summary>输行差值的代价。</summary>
        public double LoseMarginWeight = 1.0;

        /// <summary>本回合赢的行数足以清空敌方破坏条时的奖励（敌人将跳过整个回合）。</summary>
        public double BreakBonus = 25.0;

        /// <summary>敌方已破防时，"赢一行"的价值缩放（破防时赢行没有防守/破防意义，只剩伤害）。</summary>
        public double BrokenWinRowValueScale = 0.15;

        /// <summary>本回合伤害足以击杀敌舰时的奖励。</summary>
        public double KillBonus = 100.0;

        /// <summary>本回合承受的伤害足以打死自己时的惩罚（避免自杀式分配）。</summary>
        public double PlayerDeathPenalty = 200.0;

        /// <summary>平局是否算赢（对应游戏 Define.defineValue.equalBreak）。</summary>
        public bool EqualCountsAsWin = false;

        /// <summary>平局的固定价值。</summary>
        public double EqualRowValue = 0.0;

        /// <summary>装满一个部件（触发技能）的近似价值，部件没单独配置时使用。</summary>
        public double DefaultFullLoadValue = 5.0;

        /// <summary>
        /// 辅助部件（powerable=false）的占位价值。
        /// 它们不产生行战力，但技能/光环是实打实有用的，
        /// 给 0 会导致船坞求解器为了腾载重把它们直接卸掉。
        /// </summary>
        public double SupportUnitValue = 6.0;

        public ScorerOptions Clone()
        {
            return (ScorerOptions)MemberwiseClone();
        }
    }
}
