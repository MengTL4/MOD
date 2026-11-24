using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 造数据用的工厂：用可控的随机数生成合法的 BattleState。
    ///
    /// 目的（也是本项目测试策略的基础）：
    ///   求解器只依赖 BattleState 这个纯数据结构，所以我们可以完全脱离游戏，
    ///   用函数批量生成"边界情况"（颜色不够、槽位顺序陷阱、刚好装满、行战力差 1 分……），
    ///   对求解器做可重复、可断言的验证。
    /// </summary>
    public static class StateFactory
    {
        public static BattleState RandomBattle(Random rng, StateGenOptions options = null)
        {
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            var o = options ?? new StateGenOptions();

            int rows = rng.Next(o.MinRows, o.MaxRows + 1);
            int unitCount = rng.Next(o.MinUnits, o.MaxUnits + 1);

            var state = new BattleState
            {
                RowCount = rows,
                EnemyRowPower = new int[rows],
                PlayerRowFlatBonus = new int[rows],
                Tag = "random",
            };

            for (int i = 0; i < unitCount; i++)
            {
                var unit = new UnitState
                {
                    Id = i,
                    Name = "U" + i,
                    Row = rng.Next(0, rows),
                    Col = i,
                };

                int slotCount = rng.Next(o.MinSlotsPerUnit, o.MaxSlotsPerUnit + 1);
                for (int s = 0; s < slotCount; s++)
                {
                    var slot = new SlotState
                    {
                        Index = s,
                        Requirement = RollRequirement(rng),
                    };

                    if (o.AllowDisturbedSlots && rng.NextDouble() < 0.06)
                    {
                        slot.Disturbed = true;
                    }

                    unit.Slots.Add(slot);
                }

                unit.FullLoadValue = 2.0 + rng.NextDouble() * 6.0;
                state.Units.Add(unit);
            }

            int handSize = rng.Next(o.MinHand, o.MaxHand + 1);
            for (int i = 0; i < handSize; i++)
            {
                state.Hand.Add(new EnergyToken(i, rng.Next(o.MinPower, o.MaxPower + 1), RollColor(rng)));
            }

            // 敌方战力参考"如果把所有 token 平均铺到各行"的量级，避免出现必输/必胜的废局
            double avgRowPower = 0.0;
            if (unitCount > 0)
            {
                avgRowPower = (double)handSize * ((o.MinPower + o.MaxPower) / 2.0) / rows;
            }

            for (int r = 0; r < rows; r++)
            {
                int baseline = (int)Math.Round(avgRowPower * (o.EnemyPowerFactorMin + rng.NextDouble() * (o.EnemyPowerFactorMax - o.EnemyPowerFactorMin)));
                state.EnemyRowPower[r] = Math.Max(0, baseline);
            }

            return state;
        }

        /// <summary>生成一批固定种子的状态，便于回归测试。</summary>
        public static List<BattleState> RandomBattles(int seed, int count, StateGenOptions options = null)
        {
            var rng = new Random(seed);
            var list = new List<BattleState>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(RandomBattle(rng, options));
            }
            return list;
        }

        public static PowerColor RollColor(Random rng)
        {
            double r = rng.NextDouble();
            if (r < 0.5) return PowerColor.White;
            if (r < 0.85) return PowerColor.Blue;
            return PowerColor.Purple;
        }

        public static PowerColor RollRequirement(Random rng)
        {
            double r = rng.NextDouble();
            if (r < 0.55) return PowerColor.White;
            if (r < 0.85) return PowerColor.Blue;
            return PowerColor.Purple;
        }

        /// <summary>用代码直接描述一个局面的小工具，测试里读起来比手工 new 清楚。</summary>
        public static BattleState Blueprint(int rows, params int[] enemyRowPower)
        {
            if (enemyRowPower == null || enemyRowPower.Length != rows)
            {
                throw new ArgumentException("enemyRowPower length must equal rows");
            }
            return new BattleState
            {
                RowCount = rows,
                EnemyRowPower = (int[])enemyRowPower.Clone(),
                PlayerRowFlatBonus = new int[rows],
            };
        }
    }

    public static class BlueprintExtensions
    {
        public static UnitState Unit(this BattleState state, int id, int row, string name = null)
        {
            var unit = new UnitState
            {
                Id = id,
                Name = name ?? ("U" + id),
                Row = row,
            };
            state.Units.Add(unit);
            return unit;
        }

        public static SlotState Slot(this UnitState unit, PowerColor requirement, bool disturbed = false)
        {
            var slot = new SlotState
            {
                Index = unit.Slots.Count,
                Requirement = requirement,
                Disturbed = disturbed,
            };
            unit.Slots.Add(slot);
            return slot;
        }

        public static EnergyToken Token(this BattleState state, int id, int power, PowerColor color)
        {
            var token = new EnergyToken(id, power, color);
            state.Hand.Add(token);
            return token;
        }
    }
}
