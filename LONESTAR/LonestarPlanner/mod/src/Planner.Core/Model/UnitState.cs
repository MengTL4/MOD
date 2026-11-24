using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 我方船上的一个部件（游戏里的 ShipUnitController + UnitData）。
    /// </summary>
    public sealed class UnitState
    {
        /// <summary>稳定标识（在 BattleState 内唯一）。</summary>
        public int Id;

        /// <summary>显示名（原始 Name，可能是本地化 key）。</summary>
        public string Name = string.Empty;

        /// <summary>所在行（ship grid 的 y）。战斗按行比较战力。</summary>
        public int Row;

        /// <summary>所在列（ship grid 的 x），仅用于展示与后续摆位规划。</summary>
        public int Col;

        /// <summary>按顺序排列的槽位。</summary>
        public List<SlotState> Slots = new List<SlotState>();

        /// <summary>装满后触发技能的价值（近似评分用；可由引擎探针覆盖）。</summary>
        public double FullLoadValue = 0.0;

        /// <summary>部件本身已生效的基础战力（非 token 来源），默认为 0。</summary>
        public int FlatPowerBonus;

        public int LoadedPower
        {
            get
            {
                int total = FlatPowerBonus;
                for (int i = 0; i < Slots.Count; i++)
                {
                    total += Slots[i].LoadedPower;
                }
                return total;
            }
        }

        public int FreeSlotCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Slots.Count; i++)
                {
                    if (Slots[i].IsFree) n++;
                }
                return n;
            }
        }

        public int LoadedSlotCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Slots.Count; i++)
                {
                    if (Slots[i].Loaded != null) n++;
                }
                return n;
            }
        }

        /// <summary>所有槽位都有能量（游戏 IsLoadedAfterOverAllSlot 的前置条件）。</summary>
        public bool IsFull
        {
            get
            {
                if (Slots.Count == 0) return false;
                for (int i = 0; i < Slots.Count; i++)
                {
                    if (Slots[i].Loaded == null) return false;
                }
                return true;
            }
        }

        /// <summary>本回合是否还能再装一个 token。</summary>
        public bool CanReceive
        {
            get
            {
                for (int i = 0; i < Slots.Count; i++)
                {
                    if (Slots[i].IsFree) return true;
                }
                return false;
            }
        }

        public UnitState Clone()
        {
            var copy = new UnitState
            {
                Id = Id,
                Name = Name,
                Row = Row,
                Col = Col,
                FullLoadValue = FullLoadValue,
                FlatPowerBonus = FlatPowerBonus,
            };
            for (int i = 0; i < Slots.Count; i++)
            {
                copy.Slots.Add(Slots[i].Clone());
            }
            return copy;
        }

        public override string ToString()
        {
            return "#" + Id + " " + Name + " R" + Row;
        }
    }
}
