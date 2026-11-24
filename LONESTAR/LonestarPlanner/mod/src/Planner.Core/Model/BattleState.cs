using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 一次决策所需的完整快照：我方部件 + 手牌 token + 敌方每行战力。
    /// 这是核心库和游戏之间的唯一契约 —— 造数据测试也只需要造这个对象。
    /// </summary>
    public sealed class BattleState
    {
        public List<UnitState> Units = new List<UnitState>();

        /// <summary>本回合手上的能量 token。</summary>
        public List<EnergyToken> Hand = new List<EnergyToken>();

        /// <summary>行数（ship grid 高度）。</summary>
        public int RowCount = 1;

        /// <summary>敌方每行当前战力，索引 = 行号。</summary>
        /// <summary>玩家这一行有没有部件（false = 空行，游戏结算会跳过，不吃伤害也不造成伤害）。</summary>
        public bool[] PlayerRowHasUnit;

        /// <summary>敌方这一行有没有部件（false = 空行，在它上面堆战力毫无收益）。</summary>
        public bool[] EnemyRowHasUnit;

        public int[] EnemyRowPower = new int[1];

        /// <summary>我方每行非 token 来源的固定战力（舰船技能、宝物等），索引 = 行号。</summary>
        public int[] PlayerRowFlatBonus = new int[1];

        /// <summary>
        /// 车道对齐信息（可选）。有它才能评估"移动几行"；没有就按当前对齐固定计算。
        /// EnemyRowPower 始终表示"当前对齐下"的敌方行战力。
        /// </summary>
        public LaneAlignment Lanes;

        /// <summary>敌方破坏条剩余点数（每赢一行 -1；0 = 已破防，敌人跳过回合）。-1 = 未知。</summary>
        public int EnemyBreakCount = -1;

        /// <summary>敌方破坏条上限（用于展示）。</summary>
        public int EnemyBreakLimit = -1;

        /// <summary>敌方是否处于破防状态（breakTurn / breakCount == 0）。</summary>
        public bool EnemyBroken;

        /// <summary>敌舰剩余 HP（用于给伤害封顶 + 计算击杀）。&lt;= 0 表示未知。</summary>
        public int EnemyHpLeft = -1;

        /// <summary>我方剩余 HP（暂未参与计算，留作后续"不能死"约束）。</summary>
        public int PlayerHpLeft = -1;

        /// <summary>平局是否算赢（对应 Define.defineValue.equalBreak），剪枝和评分都用它。</summary>
        public bool EqualCountsAsWin;

        /// <summary>调试用标签（例如 "turn 3"），不参与计算。</summary>
        public string Tag = string.Empty;

        public BattleState Clone()
        {
            var copy = new BattleState
            {
                RowCount = RowCount,
                Tag = Tag,
                EnemyRowPower = (int[])EnemyRowPower.Clone(),
                PlayerRowFlatBonus = (int[])PlayerRowFlatBonus.Clone(),
                Lanes = Lanes == null ? null : Lanes.Clone(),
                EnemyBreakCount = EnemyBreakCount,
                EnemyBreakLimit = EnemyBreakLimit,
                EnemyBroken = EnemyBroken,
                EnemyHpLeft = EnemyHpLeft,
                PlayerHpLeft = PlayerHpLeft,
                EqualCountsAsWin = EqualCountsAsWin,
            };
            for (int i = 0; i < Units.Count; i++)
            {
                copy.Units.Add(Units[i].Clone());
            }
            for (int i = 0; i < Hand.Count; i++)
            {
                copy.Hand.Add(Hand[i].Clone());
            }
            return copy;
        }

        /// <summary>
        /// 校验快照自身一致性；返回 null 表示没问题。
        /// 造数据/读游戏时都能用它挡住明显错误，避免求解器在脏数据上给出胡话。
        /// </summary>
        public string Validate()
        {
            if (RowCount <= 0) return "RowCount must be > 0";
            if (EnemyRowPower == null || EnemyRowPower.Length != RowCount) return "EnemyRowPower length mismatch";
            if (PlayerRowFlatBonus == null || PlayerRowFlatBonus.Length != RowCount) return "PlayerRowFlatBonus length mismatch";
            if (Lanes != null)
            {
                string laneError = Lanes.Validate(RowCount);
                if (laneError != null) return laneError;
            }
            if (EnemyBreakCount < -1) return "EnemyBreakCount < -1";
            if (EnemyHpLeft < -1) return "EnemyHpLeft < -1";

            var unitIds = new HashSet<int>();
            for (int i = 0; i < Units.Count; i++)
            {
                var u = Units[i];
                if (u == null) return "Units[" + i + "] is null";
                if (!unitIds.Add(u.Id)) return "duplicate unit id " + u.Id;
                if (u.Row < 0 || u.Row >= RowCount) return "unit " + u.Id + " row out of range: " + u.Row;
                for (int s = 0; s < u.Slots.Count; s++)
                {
                    var slot = u.Slots[s];
                    if (slot == null) return "unit " + u.Id + " slot " + s + " is null";
                    if (slot.Requirement < PowerColor.White || slot.Requirement > PowerColor.Purple)
                        return "unit " + u.Id + " slot " + s + " bad requirement";
                    if (slot.Loaded != null && !slot.Loaded.Color.Satisfies(slot.Requirement))
                        return "unit " + u.Id + " slot " + s + " loaded with insufficient color";
                }
            }

            var tokenIds = new HashSet<int>();
            for (int i = 0; i < Hand.Count; i++)
            {
                var t = Hand[i];
                if (t == null) return "Hand[" + i + "] is null";
                if (!tokenIds.Add(t.Id)) return "duplicate token id " + t.Id;
                if (t.Power < 1) return "token " + t.Id + " power < 1";
            }

            return null;
        }
    }
}
