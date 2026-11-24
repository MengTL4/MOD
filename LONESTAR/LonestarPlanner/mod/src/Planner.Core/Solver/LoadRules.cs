using System;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 把游戏里的装载规则原样复刻成纯函数，求解器和测试都依赖它。
    ///
    /// 游戏源码对应位置：
    ///   ShipUnitController.LoadSlot()      —— 遍历 unitSlots，取第一个装入成功的槽
    ///   ShipUnitSlot.Load() / LoadCheckColor() —— (int)token.GetHighestColor() &gt;= slot.rare
    ///
    /// 关键点（容易漏）：
    ///   1. 一个 token 整块进入一个槽，power 不拆分；
    ///   2. 是"第一个能装的槽"，不是"按顺序必须装第 i 个"——
    ///      所以低稀有度槽装不下时，token 可以跳过它去装后面的槽；
    ///   3. 因为 2，装载顺序会影响最终布局（高级 token 抢占低级槽会挤掉低级 token）。
    /// </summary>
    public static class LoadRules
    {
        /// <summary>
        /// 返回 token 装入该部件时会落到的槽位下标；-1 表示装不进去。
        /// </summary>
        public static int FindReceivingSlot(UnitState unit, EnergyToken token)
        {
            if (unit == null || token == null) return -1;
            for (int i = 0; i < unit.Slots.Count; i++)
            {
                var slot = unit.Slots[i];
                if (slot.Loaded != null) continue;
                if (slot.Disturbed) continue;
                if (!token.Color.Satisfies(slot.Requirement)) continue;
                return i;
            }
            return -1;
        }

        public static bool CanLoad(UnitState unit, EnergyToken token)
        {
            return FindReceivingSlot(unit, token) >= 0;
        }

        /// <summary>
        /// 执行一次装载（会修改 unit 状态）。成功返回 true 并输出槽位下标。
        /// </summary>
        public static bool TryLoad(UnitState unit, EnergyToken token, out int slotIndex)
        {
            slotIndex = FindReceivingSlot(unit, token);
            if (slotIndex < 0) return false;
            unit.Slots[slotIndex].Loaded = token;
            return true;
        }

        /// <summary>撤销一次装载（供搜索回溯用）。</summary>
        public static void Unload(UnitState unit, int slotIndex)
        {
            unit.Slots[slotIndex].Loaded = null;
        }
    }
}
