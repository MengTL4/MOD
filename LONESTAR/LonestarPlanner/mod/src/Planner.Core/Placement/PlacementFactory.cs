using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    public sealed class PlacementGenOptions
    {
        public int Width = 5;
        public int Height = 4;
        public double UnusableCellChance = 0.15;

        public int MinUnits = 4;
        public int MaxUnits = 8;
        public int MinSlots = 0;
        public int MaxSlots = 3;

        public int LoadLimit = 6;
        public double InventoryRatio = 0.35;
        public int TokenPoolSize = 8;
    }

    /// <summary>造摆位测试数据：随机船网格、部件（含背包）、敌方行战力、能量池。</summary>
    public static class PlacementFactory
    {
        public static PlacementState RandomPlacement(Random rng, PlacementGenOptions options = null)
        {
            var o = options ?? new PlacementGenOptions();

            var grid = new ShipGridModel(o.Width, o.Height, (x, y) => rng.NextDouble() >= o.UnusableCellChance);
            var state = new PlacementState
            {
                Grid = grid,
                LoadLimit = o.LoadLimit,
                Tag = "random-placement",
                EnemyRowPower = new int[o.Height],
                PlayerRowFlatBonus = new int[o.Height],
            };

            int unitCount = rng.Next(o.MinUnits, o.MaxUnits + 1);
            for (int i = 0; i < unitCount; i++)
            {
                var card = new UnitCard
                {
                    Id = i,
                    Name = "P" + i,
                    LoadCost = rng.NextDouble() < 0.25 ? 2 : 1,
                    FullLoadValue = 2.0 + rng.NextDouble() * 6.0,
                    Powerable = rng.NextDouble() > 0.15,
                };

                int slots = rng.Next(o.MinSlots, o.MaxSlots + 1);
                for (int s = 0; s < slots; s++) card.SlotRarities.Add(RollRarity(rng));
                if (card.SlotRarities.Count > 0) card.SlotRarities.Sort();
                state.Units.Add(card);
            }

            // 一部分先摆上船
            var freeCells = grid.UsableCells();
            Shuffle(rng, freeCells);
            int load = 0;
            int next = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                if (rng.NextDouble() < o.InventoryRatio) continue;
                if (next >= freeCells.Count) break;
                var card = state.Units[i];
                if (load + card.LoadCost > o.LoadLimit) continue;

                int cell = freeCells[next++];
                state.CurrentLayout.Add(new PlacementSlot(card.Id, grid.XOf(cell), grid.YOf(cell)));
                load += card.LoadCost;
            }

            for (int r = 0; r < o.Height; r++)
            {
                state.EnemyRowPower[r] = rng.Next(0, 12);
            }

            for (int i = 0; i < o.TokenPoolSize; i++)
            {
                state.TokenPool.Add(new EnergyToken(i, rng.Next(1, 7), StateFactory.RollColor(rng)));
            }

            return state;
        }

        public static PlacementState Blueprint(int width, int height, params int[] enemyRowPower)
        {
            if (enemyRowPower == null || enemyRowPower.Length != height)
                throw new ArgumentException("enemyRowPower length must equal height");

            return new PlacementState
            {
                Grid = new ShipGridModel(width, height),
                EnemyRowPower = (int[])enemyRowPower.Clone(),
                PlayerRowFlatBonus = new int[height],
                LoadLimit = 99,
            };
        }

        /// <summary>
        /// 按"每回合抽 3 枚、打几回合"的粗略期望生成能量池。
        /// 真正的游戏里应该用 PowerCore 的权重分布，这里给出可复现的近似。
        /// </summary>
        public static List<EnergyToken> ExpectedPool(Random rng, int turns, int perTurn, int minPower = 1, int maxPower = 6)
        {
            var pool = new List<EnergyToken>();
            int id = 0;
            for (int t = 0; t < turns * perTurn; t++)
            {
                pool.Add(new EnergyToken(id++, rng.Next(minPower, maxPower + 1), StateFactory.RollColor(rng)));
            }
            return pool;
        }

        private static int RollRarity(Random rng)
        {
            double r = rng.NextDouble();
            if (r < 0.5) return 1;
            if (r < 0.82) return 2;
            return 3;
        }

        private static void Shuffle<T>(Random rng, List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                var tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }
    }

    public static class PlacementBlueprintExtensions
    {
        public static UnitCard Place(this PlacementState state, int id, int x, int y, int loadCost, params int[] slotRarities)
        {
            var card = new UnitCard
            {
                Id = id,
                Name = "P" + id,
                LoadCost = loadCost,
                FullLoadValue = 0,
                Powerable = true,
            };
            if (slotRarities != null) card.SlotRarities.AddRange(slotRarities);
            state.Units.Add(card);
            state.CurrentLayout.Add(new PlacementSlot(id, x, y));
            return card;
        }

        public static UnitCard Bench(this PlacementState state, int id, int loadCost, params int[] slotRarities)
        {
            var card = new UnitCard
            {
                Id = id,
                Name = "P" + id,
                LoadCost = loadCost,
                FullLoadValue = 0,
                Powerable = true,
            };
            if (slotRarities != null) card.SlotRarities.AddRange(slotRarities);
            state.Units.Add(card);
            return card;
        }

        public static UnitCard WithFullLoadValue(this UnitCard card, double value)
        {
            card.FullLoadValue = value;
            return card;
        }

        public static PlacementState WithToken(this PlacementState state, int id, int power, PowerColor color)
        {
            state.TokenPool.Add(new EnergyToken(id, power, color));
            return state;
        }
    }
}
