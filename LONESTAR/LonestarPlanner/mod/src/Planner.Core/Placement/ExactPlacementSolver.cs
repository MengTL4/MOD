using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    public sealed class ExactPlacementSolverOptions
    {
        /// <summary>时间上限（秒）。超时后返回当前最优，IsExact=false。</summary>
        public double TimeBudgetSeconds = 5.0;

        /// <summary>节点上限，防止意外爆掉。</summary>
        public int MaxNodes = 8_000_000;

        public ExactPlacementSolverOptions Clone()
        {
            return (ExactPlacementSolverOptions)MemberwiseClone();
        }
    }

    /// <summary>
    /// 船坞摆位的**穷举**求解器（在评分模型下是精确最优）。
    ///
    /// 为什么可以穷举：
    ///   评分只取决于"每一行里有哪些部件"，同一行内部部件占哪个格子不影响分数
    ///   （行战力 = 该行所有部件的槽位总和，装满价值按部件算）。
    ///   所以决策空间是"把哪些部件分到哪一行 / 谁留背包"，而不是"每个部件放哪个格子"。
    ///   (行数+1)^炮类部件数，通常只有几十万级。
    ///
    /// 约束：
    ///   - 载重 shipLoadLimit
    ///   - 每行可用格数
    ///   - Pinned 的部件（辅助部件）保持原位，不参与枚举
    /// </summary>
    public sealed class ExactPlacementSolver
    {
        private readonly IPlacementEvaluator _evaluator;
        private readonly ExactPlacementSolverOptions _options;

        public ExactPlacementSolver(IPlacementEvaluator evaluator, ExactPlacementSolverOptions options = null)
        {
            _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
            _options = options ?? new ExactPlacementSolverOptions();
        }

        public LayoutPlan Solve(PlacementState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            string invalid = state.Validate();
            if (invalid != null) throw new ArgumentException("invalid PlacementState: " + invalid, nameof(state));

            var search = new Search(state, _evaluator, _options);
            return search.Run();
        }

        // ==================================================================
        private sealed class Search
        {
            private readonly PlacementState _state;
            private readonly IPlacementEvaluator _evaluator;
            private readonly ExactPlacementSolverOptions _options;

            private readonly int _rows;
            private readonly int[] _freeCellsPerRow;
            private readonly List<PlacementSlot> _pinned = new List<PlacementSlot>();
            private readonly List<int> _pinnedCellIndex = new List<int>();
            private readonly List<int> _movable = new List<int>();
            private readonly int[] _assignment;   // 对 _movable 的下标，-1 = 背包

            private readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();
            private long _deadlineTicks;
            private int _nodes;
            private bool _timedOut;

            private double _bestScore = double.NegativeInfinity;
            private List<PlacementSlot> _bestLayout;

            public Search(PlacementState state, IPlacementEvaluator evaluator, ExactPlacementSolverOptions options)
            {
                _state = state;
                _evaluator = evaluator;
                _options = options;
                _rows = state.Grid.Height;

                int[] free = new int[_rows];
                var usedCells = new HashSet<int>();

                // 每行可用格数
                var usable = state.Grid.UsableCells();
                for (int i = 0; i < usable.Count; i++)
                {
                    free[state.Grid.RowOf(usable[i])]++;
                }

                // 锁定的部件（辅助部件）保持原位
                var layoutByUnit = new Dictionary<int, PlacementSlot>();
                for (int i = 0; i < state.CurrentLayout.Count; i++)
                {
                    layoutByUnit[state.CurrentLayout[i].UnitId] = state.CurrentLayout[i];
                }

                for (int i = 0; i < state.Units.Count; i++)
                {
                    var card = state.Units[i];
                    PlacementSlot slot;
                    if (!layoutByUnit.TryGetValue(card.Id, out slot)) continue;

                    if (card.Pinned)
                    {
                        _pinned.Add(slot);
                        int cell = state.Grid.IndexOf(slot.X, slot.Y);
                        _pinnedCellIndex.Add(cell);
                        usedCells.Add(cell);
                        free[slot.Y]--;
                    }
                }

                for (int r = 0; r < _rows; r++)
                {
                    if (free[r] < 0) free[r] = 0;
                }
                _freeCellsPerRow = free;

                // 其余部件：需要决定"哪一行 / 背包"
                for (int i = 0; i < state.Units.Count; i++)
                {
                    if (!state.Units[i].Pinned) _movable.Add(i);
                }

                // 大件优先分配，便于早找到好解
                _movable.Sort((a, b) =>
                {
                    var ca = state.Units[a];
                    var cb = state.Units[b];
                    int cmp = cb.SlotRaritySum.CompareTo(ca.SlotRaritySum);
                    if (cmp != 0) return cmp;
                    return cb.LoadCost.CompareTo(ca.LoadCost);
                });

                _assignment = new int[_movable.Count];
                for (int i = 0; i < _assignment.Length; i++) _assignment[i] = -1;

                _currentRowCount = new int[_rows];
                for (int i = 0; i < _pinned.Count; i++) _currentRowCount[_pinned[i].Y]++;

                _loadUsed = 0;
                for (int i = 0; i < _pinned.Count; i++)
                {
                    var card = _state.FindUnit(_pinned[i].UnitId);
                    if (card != null) _loadUsed += card.LoadCost;
                }

                _usedCells = usedCells;
            }

            private int[] _currentRowCount;
            private int _loadUsed;
            private HashSet<int> _usedCells;

            public LayoutPlan Run()
            {
                _watch.Start();
                _deadlineTicks = _options.TimeBudgetSeconds > 0
                    ? (long)(_options.TimeBudgetSeconds * System.Diagnostics.Stopwatch.Frequency)
                    : -1;

                // 先评估"全部留背包 + 只放锁定部件"这个基线
                EvaluateCurrent();

                Dfs(0);

                var plan = new LayoutPlan();
                if (_bestLayout != null)
                {
                    plan.Layout.AddRange(_bestLayout);
                    plan.Score = _bestScore;
                }

                PlacementOutcome outcome;
                _evaluator.Evaluate(_state, plan.Layout, out outcome);
                plan.Outcome = outcome;
                plan.IsExact = !_timedOut && _nodes < _options.MaxNodes;
                plan.Iterations = _nodes;

                for (int i = 0; i < _state.Units.Count; i++)
                {
                    var card = _state.Units[i];
                    bool placed = false;
                    for (int p = 0; p < plan.Layout.Count; p++)
                    {
                        if (plan.Layout[p].UnitId == card.Id) { placed = true; break; }
                    }
                    if (!placed) plan.BenchedUnitIds.Add(card.Id);
                }

                plan.Moves = BuildMoves(plan.Layout);
                plan.SwapCount = 0;
                return plan;
            }

            private void Dfs(int index)
            {
                if (_timedOut) return;

                if (_nodes++ > _options.MaxNodes)
                {
                    _timedOut = true;
                    return;
                }

                if (_deadlineTicks >= 0 && (_nodes & 1023) == 0 && _watch.ElapsedTicks >= _deadlineTicks)
                {
                    _timedOut = true;
                    return;
                }

                if (index >= _movable.Count)
                {
                    EvaluateCurrent();
                    return;
                }

                int unitIndex = _movable[index];
                var card = _state.Units[unitIndex];

                // 相同部件的镜像消除：按"行号非递减、背包排在最后"的规范顺序枚举
                int prevSame = -1;
                for (int i = 0; i < index; i++)
                {
                    if (SameCard(_state.Units[_movable[i]], card)) { prevSame = i; break; }
                }

                bool mustBench = false;
                int minRow = 0;
                if (prevSame >= 0)
                {
                    if (_assignment[prevSame] < 0) mustBench = true;
                    else minRow = _assignment[prevSame];
                }

                if (!mustBench)
                {
                    for (int row = minRow; row < _rows; row++)
                    {
                        if (_currentRowCount[row] >= _freeCellsPerRow[row]) continue;
                        if (_loadUsed + card.LoadCost > _state.LoadLimit) continue;

                        _assignment[index] = row;
                        _currentRowCount[row]++;
                        _loadUsed += card.LoadCost;

                        Dfs(index + 1);

                        _loadUsed -= card.LoadCost;
                        _currentRowCount[row]--;
                        _assignment[index] = -1;
                    }
                }

                // 留背包
                _assignment[index] = -1;
                Dfs(index + 1);
            }

            private void EvaluateCurrent()
            {
                var layout = new List<PlacementSlot>(_pinned);

                var nextCellInRow = new int[_rows];
                var cellsByRow = new List<int>[_rows];
                for (int r = 0; r < _rows; r++) cellsByRow[r] = new List<int>();

                var usable = _state.Grid.UsableCells();
                for (int i = 0; i < usable.Count; i++)
                {
                    int cell = usable[i];
                    if (_usedCells.Contains(cell)) continue;
                    cellsByRow[_state.Grid.RowOf(cell)].Add(cell);
                }

                for (int i = 0; i < _movable.Count; i++)
                {
                    int row = _assignment[i];
                    if (row < 0) continue;

                    var cells = cellsByRow[row];
                    int idx = nextCellInRow[row];
                    if (idx >= cells.Count) continue;
                    nextCellInRow[row]++;

                    int cell = cells[idx];
                    layout.Add(new PlacementSlot(_state.Units[_movable[i]].Id, _state.Grid.XOf(cell), _state.Grid.YOf(cell)));
                }

                PlacementOutcome ignored;
                double score = _evaluator.Evaluate(_state, layout, out ignored);
                if (score > _bestScore + 1e-9)
                {
                    _bestScore = score;
                    _bestLayout = layout;
                }
            }

            private static bool SameCard(UnitCard a, UnitCard b)
            {
                if (a.Powerable != b.Powerable) return false;
                if (a.LoadCost != b.LoadCost) return false;
                if (Math.Abs(a.FullLoadValue - b.FullLoadValue) > 1e-9) return false;
                if (a.SlotRarities.Count != b.SlotRarities.Count) return false;
                for (int i = 0; i < a.SlotRarities.Count; i++)
                {
                    if (a.SlotRarities[i] != b.SlotRarities[i]) return false;
                }
                return true;
            }

            // ---------------- 操作序列 ----------------

            private List<LayoutMove> BuildMoves(List<PlacementSlot> target)
            {
                return LayoutSequencer.Build(_state, target);
            }
        }
    }
}
