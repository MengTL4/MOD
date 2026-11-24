using System;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 战场"车道对齐"模型。
    ///
    /// 游戏里船是可以整体移动的（ShipMoveCell → ShipController.OnMove →
    /// BattleManager.AdjustPlaperGridPosition），每移动 1 行，
    /// 玩家第 r 行对上的敌方行就整体偏移 1 行。
    ///
    /// 所以"敌方每行战力"不能只按玩家行号存，必须按**战场行号**存，
    /// 再通过 PlayerRowToBattleRow 映射，才能评估"如果我移动 d 行会怎样"。
    /// </summary>
    public sealed class LaneAlignment
    {
        /// <summary>玩家第 r 行当前对应的战场行号（= BattleManager.GetBattleGridYByPlayerShipY(r)）。</summary>
        public int[] PlayerRowToBattleRow = new int[0];

        /// <summary>按战场行号索引的敌方战力；没有敌人行的位置为 0。</summary>
        public int[] EnemyPowerByBattleRow = new int[0];

        /// <summary>本回合最多能移动几行（受每回合动作数 + currentMove 限制）。</summary>
        public int MaxShift;

        /// <summary>展示用：+1 对应的方向描述（例如"下移"）。</summary>
        public string PositiveShiftLabel = "移动";

        public int EnemyPowerAt(int battleRow)
        {
            if (EnemyPowerByBattleRow == null) return 0;
            if (battleRow < 0 || battleRow >= EnemyPowerByBattleRow.Length) return 0;
            return EnemyPowerByBattleRow[battleRow];
        }

        public string Validate(int playerRowCount)
        {
            if (PlayerRowToBattleRow == null || PlayerRowToBattleRow.Length != playerRowCount)
                return "PlayerRowToBattleRow length must equal player row count";
            if (EnemyPowerByBattleRow == null)
                return "EnemyPowerByBattleRow is null";
            if (MaxShift < 0)
                return "MaxShift < 0";
            return null;
        }

        public LaneAlignment Clone()
        {
            return new LaneAlignment
            {
                PlayerRowToBattleRow = (int[])PlayerRowToBattleRow.Clone(),
                EnemyPowerByBattleRow = (int[])EnemyPowerByBattleRow.Clone(),
                MaxShift = MaxShift,
                PositiveShiftLabel = PositiveShiftLabel,
            };
        }

        /// <summary>把"移动 d 行"应用到一份 BattleState 的敌方行战力上。</summary>
        public void ApplyShift(BattleState state, int shift)
        {
            if (state == null) return;
            for (int r = 0; r < state.RowCount; r++)
            {
                int battleRow = r < PlayerRowToBattleRow.Length ? PlayerRowToBattleRow[r] : r;
                state.EnemyRowPower[r] = EnemyPowerAt(battleRow + shift);
            }
        }
    }
}
