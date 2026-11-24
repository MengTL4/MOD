using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 飞船网格（对应游戏 ShipData.grid + ShipCellStatus）。
    /// 只有 IsUsable 的格子能放部件；部件一律占 1 格。
    /// </summary>
    public sealed class ShipGridModel
    {
        private readonly bool[] _usable;

        public readonly int Width;
        public readonly int Height;

        public ShipGridModel(int width, int height)
            : this(width, height, (x, y) => true)
        {
        }

        public ShipGridModel(int width, int height, Func<int, int, bool> isUsable)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("grid size must be positive");

            Width = width;
            Height = height;
            _usable = new bool[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    _usable[x + width * y] = isUsable == null || isUsable(x, y);
                }
            }
        }

        public bool IsUsable(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
            return _usable[x + Width * y];
        }

        public bool IsUsable(int cellIndex)
        {
            if (cellIndex < 0 || cellIndex >= _usable.Length) return false;
            return _usable[cellIndex];
        }

        public int IndexOf(int x, int y)
        {
            return x + Width * y;
        }

        public int XOf(int cellIndex)
        {
            return cellIndex % Width;
        }

        public int YOf(int cellIndex)
        {
            return cellIndex / Width;
        }

        /// <summary>行号 = y，直接对应战斗里的行（ShipCell.coor.y）。</summary>
        public int RowOf(int cellIndex)
        {
            return cellIndex / Width;
        }

        public int UsableCellCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _usable.Length; i++)
                {
                    if (_usable[i]) n++;
                }
                return n;
            }
        }

        public List<int> UsableCells()
        {
            var list = new List<int>();
            for (int i = 0; i < _usable.Length; i++)
            {
                if (_usable[i]) list.Add(i);
            }
            return list;
        }
    }
}
