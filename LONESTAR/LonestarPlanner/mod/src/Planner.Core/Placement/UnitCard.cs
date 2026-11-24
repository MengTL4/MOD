using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 一张"部件卡"：无论是已装备在船上的，还是背包里没用的，都长这样。
    ///
    /// 字段来源（游戏侧）：
    ///   LoadCost      ← UnitData.GetEquiptLimit()
    ///   SlotRarities  ← UnitData.GetSlotsRares()
    ///   Powerable     ← ShipUnitController.powerable（炮类才有能量网络）
    /// </summary>
    public sealed class UnitCard
    {
        public int Id;
        public string Name = string.Empty;

        /// <summary>占用的载重（shipLoadLimit 按这个累加）。</summary>
        public int LoadCost = 1;

        /// <summary>可装能量的槽位稀有度（1=白 2=蓝 3=紫），顺序即游戏内的槽位顺序。</summary>
        public List<int> SlotRarities = new List<int>();

        /// <summary>只有炮类部件参与行战力。</summary>
        public bool Powerable = true;

        /// <summary>装满后的技能价值（近似评分用）。</summary>
        public double FullLoadValue;

        /// <summary>非槽位来源的固定战力（光环等）。</summary>
        public int FlatPowerBonus;

        /// <summary>
        /// 固定不动：求解器不会移动/卸下它。
        /// 辅助部件（powerable=false）的效果依赖位置/相邻，但模型算不出来，
        /// 与其瞎猜，不如保留玩家自己摆的位置。
        /// </summary>
        public bool Pinned;

        public int SlotCount
        {
            get { return SlotRarities == null ? 0 : SlotRarities.Count; }
        }

        /// <summary>可装载能量槽数量；非炮类视为 0。</summary>
        public int EffectiveSlotCount
        {
            get { return Powerable ? SlotCount : 0; }
        }

        /// <summary>槽位稀有度之和，粗略代表"能吃下多少高级能量"。</summary>
        public int SlotRaritySum
        {
            get
            {
                if (!Powerable || SlotRarities == null) return 0;
                int sum = 0;
                for (int i = 0; i < SlotRarities.Count; i++) sum += SlotRarities[i];
                return sum;
            }
        }

        public UnitCard Clone()
        {
            var copy = (UnitCard)MemberwiseClone();
            copy.SlotRarities = SlotRarities == null ? new List<int>() : new List<int>(SlotRarities);
            return copy;
        }

        public override string ToString()
        {
            return "#" + Id + " " + Name + "(cost " + LoadCost + ", slots " + (SlotRarities == null ? 0 : SlotRarities.Count) + ")";
        }
    }

    /// <summary>布局中的一格：哪个部件放在 (X,Y)。</summary>
    public struct PlacementSlot
    {
        public int UnitId;
        public int X;
        public int Y;

        public PlacementSlot(int unitId, int x, int y)
        {
            UnitId = unitId;
            X = x;
            Y = y;
        }

        public override string ToString()
        {
            return "#" + UnitId + "@(" + X + "," + Y + ")";
        }
    }

    /// <summary>一次摆位调整（用于给玩家显示"要从哪挪到哪"）。</summary>
    public sealed class LayoutMove
    {
        public enum MoveKind
        {
            Deploy,   // 从背包上船
            Bench,    // 从船上卸到背包
            Move,     // 船内换格
            Swap,     // 船内两个部件互换（战斗内算 1 次 exchange）
        }

        public MoveKind Kind;
        public int UnitId;
        public string UnitName = string.Empty;
        public int FromX = -1;
        public int FromY = -1;
        public int ToX = -1;
        public int ToY = -1;
        public int OtherUnitId = -1;

        public override string ToString()
        {
            switch (Kind)
            {
                case MoveKind.Deploy: return "上船 " + UnitName + " → (" + ToX + "," + ToY + ")";
                case MoveKind.Bench: return "卸下 " + UnitName + " (" + FromX + "," + FromY + ")";
                case MoveKind.Move: return "移动 " + UnitName + " (" + FromX + "," + FromY + ") → (" + ToX + "," + ToY + ")";
                case MoveKind.Swap: return "交换 " + UnitName + " ↔ #" + OtherUnitId + "  (" + FromX + "," + FromY + ") ↔ (" + ToX + "," + ToY + ")";
                default: return Kind + " " + UnitName;
            }
        }
    }
}
