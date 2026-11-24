using System;
using System.Collections.Generic;
using System.Linq;

namespace Lonestar.Engine
{
    /// <summary>One cell of the player's ship grid as the layout optimizer sees it.</summary>
    public sealed class CellInfo
    {
        public int X;
        public int Y;
        public int Status;       // ShipCellStatus: 0 = None (never usable), 1 = Empty, 2 = Equipped
        public int UnitId;       // 0 when empty
        public int Lv;
        public int EquipLimit;
    }

    /// <summary>A unit available to be placed: either already on the ship or in the bag.</summary>
    public sealed class AvailableUnit
    {
        public int Index;
        public int Id;
        public int Lv;
        public int EquipLimit;
        public bool OnShip;
        public int X;
        public int Y;
        public UnitData Data;
    }

    /// <summary>
    /// Reads and rewrites the pre-battle ship layout (which unit sits on which grid cell).
    /// The change is applied in memory after the save is restored and before the battle starts,
    /// which is exactly where the game's own refit screen leaves the state.
    /// </summary>
    public static class LayoutEditor
    {
        public static PlayerShipData Ship => Singleton<ShipDatasManager>.Instance().currentPlayerShipData;

        public static List<CellInfo> Cells()
        {
            var cells = new List<CellInfo>();
            Ship.grid.TraversalGridContent(cell =>
            {
                cells.Add(new CellInfo
                {
                    X = cell.coor.x,
                    Y = cell.coor.y,
                    Status = (int)cell.shipCellStatus,
                    UnitId = cell.unitData == null ? 0 : cell.unitData.id,
                    Lv = cell.unitData == null ? 0 : cell.unitData.lv,
                    EquipLimit = cell.unitData == null ? 0 : cell.unitData.GetEquiptLimit(),
                });
            });
            return cells.OrderBy(c => c.Y).ThenBy(c => c.X).ToList();
        }

        /// <summary>Ship cells plus bag units; index order is stable across identical boots.</summary>
        public static List<AvailableUnit> AvailableUnits()
        {
            var units = new List<AvailableUnit>();
            int index = 0;
            foreach (CellInfo cell in Cells())
            {
                if (cell.UnitId == 0)
                {
                    continue;
                }
                UnitData data = Cell(cell.X, cell.Y)?.unitData;
                units.Add(new AvailableUnit
                {
                    Index = index++,
                    Id = cell.UnitId,
                    Lv = cell.Lv,
                    EquipLimit = cell.EquipLimit,
                    OnShip = true,
                    X = cell.X,
                    Y = cell.Y,
                    Data = data,
                });
            }
            foreach (ItemUnit item in Singleton<ItemManager>.Instance().itemUnits)
            {
                if (item?.unitData == null)
                {
                    continue;
                }
                units.Add(new AvailableUnit
                {
                    Index = index++,
                    Id = item.unitData.id,
                    Lv = item.unitData.lv,
                    EquipLimit = item.unitData.GetEquiptLimit(),
                    Data = item.unitData,
                });
            }
            return units;
        }

        public static ShipCell Cell(int x, int y) => Ship.grid.GetContent(x, y);

        public static bool Validate(IReadOnlyList<(int X, int Y, int Unit)> entries, List<AvailableUnit> units, out string error)
        {
            error = null;
            var seen = new HashSet<(int, int)>();
            int limit = 0;
            foreach ((int x, int y, int unit) in entries)
            {
                if (unit < 0 || unit >= units.Count)
                {
                    error = $"unit index {unit} out of range";
                    return false;
                }
                if (!seen.Add((x, y)))
                {
                    error = $"two units on cell {x},{y}";
                    return false;
                }
                ShipCell cell = Cell(x, y);
                if (cell == null || cell.shipCellStatus == ShipCellStatus.None)
                {
                    error = $"cell {x},{y} is not usable";
                    return false;
                }
                limit += units[unit].EquipLimit;
            }
            if (limit > Ship.shipLoadLimit)
            {
                error = $"load limit exceeded: {limit} > {Ship.shipLoadLimit}";
                return false;
            }
            return true;
        }

        /// <summary>Clears every usable cell and places the given units. Call after Boot, before battle.</summary>
        public static bool Apply(IReadOnlyList<(int X, int Y, int Unit)> entries, out string error)
        {
            List<AvailableUnit> units = AvailableUnits();
            if (!Validate(entries, units, out error))
            {
                return false;
            }
            foreach (CellInfo info in Cells())
            {
                if (info.Status == (int)ShipCellStatus.None)
                {
                    continue;
                }
                ShipCell cell = Cell(info.X, info.Y);
                cell.unitData = null;
                cell.shipCellStatus = ShipCellStatus.Empty;
            }
            foreach ((int x, int y, int unit) in entries)
            {
                ShipCell cell = Cell(x, y);
                cell.unitData = units[unit].Data;
                cell.shipCellStatus = ShipCellStatus.Equipped;
            }
            error = null;
            return true;
        }

        public static string Describe()
        {
            var available = AvailableUnits();
            var sb = new System.Text.StringBuilder();
            sb.Append("cells: ");
            foreach (CellInfo c in Cells())
            {
                sb.Append($"({c.X},{c.Y}){(c.Status == (int)ShipCellStatus.None ? "none" : c.UnitId == 0 ? "empty" : $"{c.UnitId}/L{c.Lv}")} ");
            }
            sb.AppendLine();
            sb.Append("available: ");
            foreach (AvailableUnit u in available)
            {
                sb.Append($"[{u.Index}]={u.Id}/L{u.Lv}@{u.EquipLimit}{(u.OnShip ? $"({u.X},{u.Y})" : "(bag)")} ");
            }
            return sb.ToString();
        }

        // ---- energy core (power / colour deck) -------------------------------------------------

        /// <summary>Counts of power-1..5 cards currently in the ship's core.</summary>
        public static int[] CorePowers()
        {
            var counts = new int[5];
            foreach (PowerWeight weight in Ship.powerWeights)
            {
                int p = weight.power;
                if (p >= 1 && p <= 5)
                {
                    counts[p - 1]++;
                }
            }
            return counts;
        }

        /// <summary>Counts of white/blue/purple colour cards currently in the ship's core.</summary>
        public static int[] CoreColors()
        {
            var counts = new int[3];
            foreach (PowerColorWeight weight in Ship.powerColorWeights)
            {
                foreach (int color in weight.powerColors.colorList.Select(c => (int)c))
                {
                    if (color >= 1 && color <= 3)
                    {
                        counts[color - 1]++;
                        break;
                    }
                }
            }
            return counts;
        }

        /// <summary>
        /// Replaces the core composition. The game only lets a run change this through events/rewards;
        /// the solver uses it to evaluate candidate cores before committing to one.
        /// </summary>
        public static void SetCore(int[] powers, int[] colors)
        {
            PlayerShipData ship = Ship;
            ship.powerWeights.Clear();
            for (int i = 0; i < powers.Length; i++)
            {
                for (int n = 0; n < powers[i]; n++)
                {
                    ship.powerWeights.Add(new PowerWeight(i + 1));
                }
            }
            ship.powerColorWeights.Clear();
            for (int i = 0; i < colors.Length; i++)
            {
                for (int n = 0; n < colors[i]; n++)
                {
                    ship.powerColorWeights.Add(new PowerColorWeight(new PowerColors((PowerColor)(i + 1))));
                }
            }
        }
    }
}
