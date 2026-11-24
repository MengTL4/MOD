using System;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 部件上的一个能量槽（游戏里的 ShipUnitSlot）。
    /// Requirement 对应 slot.rare：1=白, 2=蓝, 3=紫。
    /// </summary>
    public sealed class SlotState
    {
        /// <summary>槽位在部件内的顺序（0-based）。游戏按这个顺序找第一个可装入的槽。</summary>
        public int Index;

        /// <summary>颜色门槛：token 最高颜色 &gt;= Requirement 才能装入。</summary>
        public PowerColor Requirement = PowerColor.White;

        /// <summary>已被装入的 token；null 表示空。</summary>
        public EnergyToken Loaded;

        /// <summary>被干扰封锁（游戏 ShipUnitSlot.isDisturbed），本回合不可装入。</summary>
        public bool Disturbed;

        public bool IsFree
        {
            get { return Loaded == null && !Disturbed; }
        }

        public int LoadedPower
        {
            get { return Loaded == null ? 0 : Loaded.Power; }
        }

        public SlotState Clone()
        {
            return new SlotState
            {
                Index = Index,
                Requirement = Requirement,
                Loaded = Loaded == null ? null : Loaded.Clone(),
                Disturbed = Disturbed,
            };
        }

        public override string ToString()
        {
            return "[" + Requirement.ShortName() + (Disturbed ? "/锁" : (Loaded == null ? "/空" : "/" + Loaded.Power)) + "]";
        }
    }
}
