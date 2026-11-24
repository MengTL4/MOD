using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    /// <summary>
    /// 摆位评分器。和能量分配一样，核心只依赖接口，方便以后换成"引擎探针"实现。
    /// </summary>
    public interface IPlacementEvaluator
    {
        double Evaluate(PlacementState state, IReadOnlyList<PlacementSlot> layout, out PlacementOutcome outcome);
    }

    /// <summary>
    /// 近似摆位评分：
    ///   1. 把能量池里的 token 分配到各部件槽位（贪心：先满足高稀有度槽）；
    ///   2. 汇总成每行战力，套用和 ApproxScorer 一样的行价值函数；
    ///   3. 加上"装满部件"的价值。
    ///
    /// 注意这是摆位阶段的**近似**：真正精确的交战结果需要引擎探针。
    /// 但它足以区分"把紫色槽部件放到要抗压的行"这类关键差异。
    /// </summary>
    public sealed class ApproxPlacementEvaluator : IPlacementEvaluator
    {
        public const double InvalidScore = -1e9;

        public readonly ScorerOptions Options;

        private readonly List<EnergyToken> _pool = new List<EnergyToken>();
        private readonly List<bool> _poolUsed = new List<bool>();

        public ApproxPlacementEvaluator()
            : this(new ScorerOptions())
        {
        }

        public ApproxPlacementEvaluator(ScorerOptions options)
        {
            Options = options ?? new ScorerOptions();
        }

        public double Evaluate(PlacementState state, IReadOnlyList<PlacementSlot> layout, out PlacementOutcome outcome)
        {
            int rows = state.Grid.Height;
            outcome = new PlacementOutcome
            {
                Rows = new RowOutcome[rows],
                LoadLimit = state.LoadLimit,
            };

            var rowPower = new int[rows];
            if (state.PlayerRowFlatBonus != null)
            {
                for (int i = 0; i < rows && i < state.PlayerRowFlatBonus.Length; i++)
                {
                    rowPower[i] = state.PlayerRowFlatBonus[i];
                }
            }

            // ---- 校验布局 ----
            var usedCells = new HashSet<int>();
            int load = 0;
            var placed = new List<PlacedRef>(layout.Count);

            for (int i = 0; i < layout.Count; i++)
            {
                var slot = layout[i];
                var unit = state.FindUnit(slot.UnitId);
                if (unit == null)
                {
                    outcome.Warnings.Add("未知部件 #" + slot.UnitId);
                    outcome.LoadUsed = load;
                    return InvalidScore;
                }
                if (!state.Grid.IsUsable(slot.X, slot.Y))
                {
                    outcome.Warnings.Add("格子不可用 (" + slot.X + "," + slot.Y + ")");
                    outcome.LoadUsed = load;
                    return InvalidScore;
                }
                if (!usedCells.Add(state.Grid.IndexOf(slot.X, slot.Y)))
                {
                    outcome.Warnings.Add("格子重复 (" + slot.X + "," + slot.Y + ")");
                    outcome.LoadUsed = load;
                    return InvalidScore;
                }

                load += unit.LoadCost;
                placed.Add(new PlacedRef { Unit = unit, Row = slot.Y });
            }

            outcome.LoadUsed = load;
            if (load > state.LoadLimit)
            {
                outcome.Warnings.Add("载重超限 " + load + "/" + state.LoadLimit);
                return InvalidScore;
            }

            // ---- 能量池 ----
            _pool.Clear();
            _poolUsed.Clear();
            for (int i = 0; i < state.TokenPool.Count; i++)
            {
                _pool.Add(state.TokenPool[i]);
                _poolUsed.Add(false);
            }

            // 优先照顾"缺口最大"的行：把高级槽部件放在那行，收益最高
            placed.Sort((a, b) =>
            {
                int defA = EnemyOf(state, a.Row) - rowPower[a.Row];
                int defB = EnemyOf(state, b.Row) - rowPower[b.Row];
                if (defA != defB) return defB.CompareTo(defA);
                return b.Unit.SlotRaritySum.CompareTo(a.Unit.SlotRaritySum);
            });

            double fullValue = 0.0;

            for (int p = 0; p < placed.Count; p++)
            {
                var unit = placed[p].Unit;
                int row = placed[p].Row;

                rowPower[row] += unit.FlatPowerBonus;

                if (!unit.Powerable)
                {
                    // 辅助部件不产战力，但占着格子/载重且技能有用：按固定价值计入，
                    // 避免求解器为了腾位置把它卸掉。
                    fullValue += unit.FullLoadValue > 0 ? unit.FullLoadValue : Options.SupportUnitValue;
                    continue;
                }

                if (unit.SlotCount == 0) continue;

                // 槽位按稀有度从高到低处理：先喂最难满足的槽
                var slotOrder = new int[unit.SlotCount];
                for (int s = 0; s < slotOrder.Length; s++) slotOrder[s] = s;
                Array.Sort(slotOrder, (x, y) => unit.SlotRarities[y].CompareTo(unit.SlotRarities[x]));

                int filled = 0;
                for (int si = 0; si < slotOrder.Length; si++)
                {
                    int requirement = unit.SlotRarities[slotOrder[si]];
                    int tokenIndex = PickBestToken(requirement);
                    if (tokenIndex < 0) continue;

                    _poolUsed[tokenIndex] = true;
                    rowPower[row] += _pool[tokenIndex].Power;
                    filled++;
                }

                if (filled == unit.SlotCount)
                {
                    outcome.FullUnitIds.Add(unit.Id);
                    fullValue += unit.FullLoadValue > 0 ? unit.FullLoadValue : Options.DefaultFullLoadValue;
                }
            }

            // ---- 行价值（和能量分配共用同一套真实收益模型）----
            var diffs = new int[rows];
            for (int r = 0; r < rows; r++)
            {
                int enemy = EnemyOf(state, r);
                diffs[r] = rowPower[r] - enemy;
            }

            // 船坞里通常没有敌人信息：这时只最大化"能吃下的总战力 + 装满价值"，
            // 否则"每行有 1 点战力就算赢"会误导求解器去平铺。
            double score;
            if (HasEnemyInfo(state))
            {
                score = RowScorer.Score(diffs, state.EnemyBreakCount, state.EnemyHpLeft, Options, -1, false);
            }
            else
            {
                int totalAssigned = 0;
                for (int r = 0; r < rows; r++) totalAssigned += rowPower[r];
                score = totalAssigned * Options.WinMarginWeight;
            }

            for (int r = 0; r < rows; r++)
            {
                int enemy = EnemyOf(state, r);
                int diff = diffs[r];
                bool isWin = diff > 0 || (diff == 0 && Options.EqualCountsAsWin);

                outcome.Rows[r] = new RowOutcome
                {
                    Row = r,
                    PlayerPower = rowPower[r],
                    EnemyPower = enemy,
                    Diff = diff,
                    IsWin = isWin,
                    Value = 0,
                };
                outcome.TotalPower += rowPower[r];
            }

            return score + fullValue;
        }

        private static bool HasEnemyInfo(PlacementState state)
        {
            if (state.EnemyBreakCount >= 0) return true;
            if (state.EnemyHpLeft > 0) return true;
            if (state.EnemyRowPower != null)
            {
                for (int i = 0; i < state.EnemyRowPower.Length; i++)
                {
                    if (state.EnemyRowPower[i] > 0) return true;
                }
            }
            return false;
        }

        private static int EnemyOf(PlacementState state, int row)
        {
            if (state.EnemyRowPower == null || row < 0 || row >= state.EnemyRowPower.Length) return 0;
            return state.EnemyRowPower[row];
        }

        /// <summary>挑一枚还没用掉、颜色够、能量最大的 token；找不到返回 -1。</summary>
        private int PickBestToken(int requirement)
        {
            int best = -1;
            for (int i = 0; i < _pool.Count; i++)
            {
                if (_poolUsed[i]) continue;
                if ((int)_pool[i].Color < requirement) continue;
                if (best < 0 || _pool[i].Power > _pool[best].Power) best = i;
            }
            return best;
        }

        private struct PlacedRef
        {
            public UnitCard Unit;
            public int Row;
        }
    }
}
