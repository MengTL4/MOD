using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 摆位问题的一次决策快照：船网格 + 候选部件（船上 + 背包） + 敌方行战力 + 能量池。
    ///
    /// 这是"摆位求解器"和游戏之间的契约，造数据测试也只需要造它。
    /// </summary>
    public sealed class PlacementState
    {
        public ShipGridModel Grid;

        /// <summary>候选部件池：已装备的 + 背包里的，都在这里。</summary>
        public List<UnitCard> Units = new List<UnitCard>();

        /// <summary>当前布局（已装备部件的位置）。</summary>
        public List<PlacementSlot> CurrentLayout = new List<PlacementSlot>();

        /// <summary>载重上限（游戏 ShipData.shipLoadLimit，默认 6）。</summary>
        public int LoadLimit = 6;

        /// <summary>敌方每行战力，索引 = 行号（y）。</summary>
        public int[] EnemyRowPower;

        /// <summary>我方每行非槽位来源的固定战力。</summary>
        public int[] PlayerRowFlatBonus;

        /// <summary>
        /// 评分用的能量池：可以是"当前手牌"，也可以是"期望供给"（几回合的 token 汇总）。
        /// 摆位阶段通常用期望供给，因为要评估的是整场战斗的表现。
        /// </summary>
        public List<EnergyToken> TokenPool = new List<EnergyToken>();

        /// <summary>战斗内可用交换次数（船坞阶段无意义，设为 int.MaxValue 表示自由调整）。</summary>
        public int SwapBudget = int.MaxValue;

        /// <summary>敌方破坏条剩余点数（船坞里通常未知，-1）。</summary>
        public int EnemyBreakCount = -1;

        /// <summary>敌舰剩余 HP（船坞里通常未知，-1）。</summary>
        public int EnemyHpLeft = -1;

        public string Tag = string.Empty;

        public int RowCount
        {
            get { return Grid == null ? 0 : Grid.Height; }
        }

        public UnitCard FindUnit(int unitId)
        {
            for (int i = 0; i < Units.Count; i++)
            {
                if (Units[i].Id == unitId) return Units[i];
            }
            return null;
        }

        public int LoadUsed(IReadOnlyList<PlacementSlot> layout)
        {
            int total = 0;
            for (int i = 0; i < layout.Count; i++)
            {
                var unit = FindUnit(layout[i].UnitId);
                if (unit != null) total += unit.LoadCost;
            }
            return total;
        }

        /// <summary>返回 null 表示没问题，否则是错误描述。</summary>
        public string Validate()
        {
            if (Grid == null) return "Grid is null";
            if (Grid.Width <= 0 || Grid.Height <= 0) return "Grid size invalid";
            if (LoadLimit < 0) return "LoadLimit < 0";

            if (EnemyRowPower == null || EnemyRowPower.Length != Grid.Height) return "EnemyRowPower length must equal grid height";
            if (PlayerRowFlatBonus == null || PlayerRowFlatBonus.Length != Grid.Height) return "PlayerRowFlatBonus length must equal grid height";

            var unitIds = new HashSet<int>();
            for (int i = 0; i < Units.Count; i++)
            {
                if (Units[i] == null) return "Units[" + i + "] is null";
                if (!unitIds.Add(Units[i].Id)) return "duplicate unit id " + Units[i].Id;
                if (Units[i].LoadCost < 0) return "unit " + Units[i].Id + " negative LoadCost";
            }

            var cells = new HashSet<int>();
            int load = 0;
            for (int i = 0; i < CurrentLayout.Count; i++)
            {
                var slot = CurrentLayout[i];
                if (FindUnit(slot.UnitId) == null) return "layout references unknown unit " + slot.UnitId;
                if (!Grid.IsUsable(slot.X, slot.Y)) return "layout uses unusable cell (" + slot.X + "," + slot.Y + ")";
                if (!cells.Add(Grid.IndexOf(slot.X, slot.Y))) return "layout has duplicate cell (" + slot.X + "," + slot.Y + ")";
                load += FindUnit(slot.UnitId).LoadCost;
            }
            if (load > LoadLimit) return "layout exceeds load limit: " + load + " > " + LoadLimit;

            return null;
        }

        public PlacementState Clone()
        {
            var copy = new PlacementState
            {
                Grid = Grid,
                LoadLimit = LoadLimit,
                SwapBudget = SwapBudget,
                Tag = Tag,
                EnemyBreakCount = EnemyBreakCount,
                EnemyHpLeft = EnemyHpLeft,
                EnemyRowPower = EnemyRowPower == null ? null : (int[])EnemyRowPower.Clone(),
                PlayerRowFlatBonus = PlayerRowFlatBonus == null ? null : (int[])PlayerRowFlatBonus.Clone(),
            };
            for (int i = 0; i < Units.Count; i++) copy.Units.Add(Units[i].Clone());
            copy.CurrentLayout.AddRange(CurrentLayout);
            for (int i = 0; i < TokenPool.Count; i++) copy.TokenPool.Add(TokenPool[i].Clone());
            return copy;
        }
    }

    /// <summary>摆位评分的结果明细，用于 UI 展示与测试断言。</summary>
    public sealed class PlacementOutcome
    {
        public RowOutcome[] Rows;
        public List<int> FullUnitIds = new List<int>();
        public int TotalPower;
        public int LoadUsed;
        public int LoadLimit;
        public List<string> Warnings = new List<string>();

        public bool Overloaded
        {
            get { return LoadUsed > LoadLimit; }
        }
    }

    /// <summary>一套布局方案（可能是船坞里的目标布局，也可能只是战斗内几次交换）。</summary>
    public sealed class LayoutPlan
    {
        public List<PlacementSlot> Layout = new List<PlacementSlot>();
        public List<int> BenchedUnitIds = new List<int>();
        public List<LayoutMove> Moves = new List<LayoutMove>();
        public double Score;
        public PlacementOutcome Outcome;
        public int SwapCount;
        public bool IsExact;
        public int Iterations;
        public List<string> Notes = new List<string>();

        public LayoutPlan Clone()
        {
            var copy = new LayoutPlan
            {
                Score = Score,
                SwapCount = SwapCount,
                IsExact = IsExact,
                Iterations = Iterations,
            };
            copy.Layout.AddRange(Layout);
            copy.BenchedUnitIds.AddRange(BenchedUnitIds);
            copy.Moves.AddRange(Moves);
            copy.Notes.AddRange(Notes);
            copy.Outcome = Outcome;
            return copy;
        }

        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("score=" + Score.ToString("0.##") + "  载重 " + (Outcome == null ? 0 : Outcome.LoadUsed) + "/" + (Outcome == null ? 0 : Outcome.LoadLimit)
                          + (SwapCount > 0 ? "  需要 " + SwapCount + " 次交换" : string.Empty));

            if (Outcome != null && Outcome.Rows != null)
            {
                for (int i = 0; i < Outcome.Rows.Length; i++)
                {
                    var row = Outcome.Rows[i];
                    string tag = row.Diff > 0 ? "赢 +" + row.Diff : (row.Diff < 0 ? "输 " + row.Diff : "平");
                    sb.AppendLine("  R" + row.Row + ": " + row.PlayerPower + " vs " + row.EnemyPower + "   " + tag);
                }
                if (Outcome.FullUnitIds.Count > 0)
                {
                    sb.AppendLine("  装满: " + string.Join(",", Outcome.FullUnitIds.ConvertAll(x => "#" + x).ToArray()));
                }
            }

            if (Moves.Count > 0)
            {
                sb.AppendLine("  操作顺序:");
                for (int i = 0; i < Moves.Count; i++)
                {
                    sb.AppendLine("   " + (i + 1) + ". " + Moves[i]);
                }
            }
            else
            {
                sb.AppendLine("  当前布局已是最优");
            }

            for (int i = 0; i < Notes.Count; i++) sb.AppendLine("  * " + Notes[i]);
            return sb.ToString();
        }
    }
}
