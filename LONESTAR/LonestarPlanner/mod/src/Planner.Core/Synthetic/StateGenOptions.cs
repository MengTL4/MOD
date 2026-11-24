using System;

namespace LonestarPlanner.Core
{
    public sealed class StateGenOptions
    {
        public int MinRows = 2;
        public int MaxRows = 4;
        public int MinUnits = 2;
        public int MaxUnits = 6;
        public int MinSlotsPerUnit = 1;
        public int MaxSlotsPerUnit = 3;
        public int MinHand = 3;
        public int MaxHand = 6;
        public int MinPower = 1;
        public int MaxPower = 6;
        public bool AllowDisturbedSlots = true;

        /// <summary>是否允许部件上已经有上一回合残留的能量（默认否，保持测试场景干净）。</summary>
        public bool AllowPreloadedSlots = false;

        /// <summary>敌方每行战力相对我方空船战力的倍率范围。</summary>
        public double EnemyPowerFactorMin = 0.6;
        public double EnemyPowerFactorMax = 1.4;

        public StateGenOptions Clone()
        {
            return (StateGenOptions)MemberwiseClone();
        }
    }
}
