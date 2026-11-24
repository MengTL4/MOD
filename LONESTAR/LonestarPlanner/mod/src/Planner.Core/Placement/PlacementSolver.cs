using System;
using System.Collections.Generic;

namespace LonestarPlanner.Core
{
    public sealed class PlacementSolverOptions
    {
        public int Iterations = 30000;
        public int Restarts = 3;
        public double StartTemperature = 4.0;
        public double EndTemperature = 0.02;
        public int Seed = 20260817;

        /// <summary>允许把部件卸回背包（船坞阶段为 true）。</summary>
        public bool AllowBench = true;

        /// <summary>允许把背包里的部件装上船。</summary>
        public bool AllowDeploy = true;

        /// <summary>
        /// 时间预算（秒）。&gt;0 时忽略 Iterations，一直搜索到时间用完。
        /// 船坞没有可实测的战场数据，所以这里仍然是模型评分——只是搜更久。
        /// </summary>
        public double TimeBudgetSeconds = 0.0;

        public PlacementSolverOptions Clone()
        {
            return (PlacementSolverOptions)MemberwiseClone();
        }
    }

    /// <summary>
    /// 船坞摆位求解器（无成本调整：可以随意挪、上船、下船，只受载重和格子限制）。
    ///
    /// 用模拟退火：邻域 = 移动到空格 / 两部件互换 / 卸下 / 装上一个背包部件。
    /// 目标函数交给 IPlacementEvaluator，所以换成引擎探针时这里不用改。
    /// </summary>
    public sealed class PlacementSolver
    {
        private readonly IPlacementEvaluator _evaluator;
        private readonly PlacementSolverOptions _options;

        public PlacementSolver(IPlacementEvaluator evaluator, PlacementSolverOptions options = null)
        {
            _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
            _options = options ?? new PlacementSolverOptions();
        }

        public IPlacementEvaluator Evaluator
        {
            get { return _evaluator; }
        }

        public LayoutPlan Solve(PlacementState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            string invalid = state.Validate();
            if (invalid != null) throw new ArgumentException("invalid PlacementState: " + invalid, nameof(state));

            var cells = state.Grid.UsableCells();
            var unitIndex = new Dictionary<int, int>();
            for (int i = 0; i < state.Units.Count; i++) unitIndex[state.Units[i].Id] = i;

            LayoutPlan best = null;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            long deadlineTicks = _options.TimeBudgetSeconds > 0
                ? (long)(_options.TimeBudgetSeconds * System.Diagnostics.Stopwatch.Frequency)
                : -1;

            int restart = 0;
            while (true)
            {
                if (deadlineTicks < 0 && restart >= Math.Max(1, _options.Restarts)) break;
                if (deadlineTicks >= 0 && watch.ElapsedTicks >= deadlineTicks) break;

                var rng = new Random(_options.Seed + restart * 7919);
                var run = new SearchRun(state, cells, unitIndex, _evaluator, _options);
                run.Initialize();
                run.Anneal(rng, deadlineTicks, watch);
                var plan = run.BuildPlan();
                if (best == null || plan.Score > best.Score) best = plan;

                restart++;
                if (deadlineTicks < 0 && restart >= Math.Max(1, _options.Restarts)) break;
                if (deadlineTicks >= 0 && watch.ElapsedTicks >= deadlineTicks) break;
            }

            best.Iterations = _options.Iterations * Math.Max(1, _options.Restarts);
            return best;
        }

        // ==================================================================
        private sealed class SearchRun
        {
            private readonly PlacementState _state;
            private readonly List<int> _cells;
            private readonly Dictionary<int, int> _unitIndex;
            private readonly IPlacementEvaluator _evaluator;
            private readonly PlacementSolverOptions _options;

            private readonly int[] _cellOfUnit;   // -1 = 未上船
            private readonly int[] _unitAtCell;   // -1 = 空
            private readonly List<PlacementSlot> _buffer = new List<PlacementSlot>();

            private int _load;

            public SearchRun(PlacementState state, List<int> cells, Dictionary<int, int> unitIndex,
                             IPlacementEvaluator evaluator, PlacementSolverOptions options)
            {
                _state = state;
                _cells = cells;
                _unitIndex = unitIndex;
                _evaluator = evaluator;
                _options = options;
                _cellOfUnit = new int[state.Units.Count];
                for (int i = 0; i < _cellOfUnit.Length; i++) _cellOfUnit[i] = -1;
                _unitAtCell = new int[state.Grid.Width * state.Grid.Height];
                for (int i = 0; i < _unitAtCell.Length; i++) _unitAtCell[i] = -1;
            }

            public void Initialize()
            {
                // 先照当前布局摆，超载/非法就按价值从高到低保留
                for (int i = 0; i < _state.CurrentLayout.Count; i++)
                {
                    var slot = _state.CurrentLayout[i];
                    int ui;
                    if (!_unitIndex.TryGetValue(slot.UnitId, out ui)) continue;
                    int cell = _state.Grid.IndexOf(slot.X, slot.Y);
                    if (!_state.Grid.IsUsable(cell) || _unitAtCell[cell] >= 0) continue;
                    _cellOfUnit[ui] = cell;
                    _unitAtCell[cell] = ui;
                    _load += _state.Units[ui].LoadCost;
                }

                while (_load > _state.LoadLimit)
                {
                    int worst = -1;
                    for (int u = 0; u < _state.Units.Count; u++)
                    {
                        if (_cellOfUnit[u] < 0) continue;
                        if (worst < 0 || ValueOf(u) < ValueOf(worst)) worst = u;
                    }
                    if (worst < 0) break;
                    Remove(worst);
                }

                // 兜底：如果完全没布局，塞几个进去
                if (CountPlaced() == 0)
                {
                    for (int u = 0; u < _state.Units.Count; u++)
                    {
                        int cell = FirstEmptyCell();
                        if (cell < 0) break;
                        if (_load + _state.Units[u].LoadCost > _state.LoadLimit) continue;
                        Place(u, cell);
                    }
                }
            }

            public void Anneal(Random rng, long deadlineTicks = -1, System.Diagnostics.Stopwatch watch = null)
            {
                double current = Evaluate();
                double bestScore = current;
                var bestCells = (int[])_cellOfUnit.Clone();
                int bestLoad = _load;

                int total = Math.Max(1, _options.Iterations);
                for (int iter = 0; iter < total; iter++)
                {
                    if (deadlineTicks >= 0 && watch != null && (iter & 511) == 0 && watch.ElapsedTicks >= deadlineTicks)
                    {
                        break;
                    }

                    double t = _options.StartTemperature *
                               Math.Pow(Math.Max(1e-6, _options.EndTemperature / _options.StartTemperature), (double)iter / total);

                    var undo = Propose(rng);
                    if (undo == null) continue;

                    double candidate = Evaluate();
                    double delta = candidate - current;

                    if (delta >= 0 || rng.NextDouble() < Math.Exp(delta / Math.Max(1e-6, t)))
                    {
                        current = candidate;
                        if (candidate > bestScore)
                        {
                            bestScore = candidate;
                            bestCells = (int[])_cellOfUnit.Clone();
                            bestLoad = _load;
                        }
                    }
                    else
                    {
                        undo();
                    }
                }

                Array.Copy(bestCells, _cellOfUnit, _cellOfUnit.Length);
                _load = bestLoad;
                RebuildCellIndex();
            }

            // ---------------- 邻域 ----------------

            /// <summary>返回一个"撤销操作"；无效提案返回 null。</summary>
            private Action Propose(Random rng)
            {
                int kind = rng.Next(4);
                if (kind == 0) return ProposeMove(rng);
                if (kind == 1) return ProposeSwap(rng);
                if (kind == 2) return _options.AllowBench ? ProposeBench(rng) : null;
                return _options.AllowDeploy ? ProposeDeploy(rng) : null;
            }

            private Action ProposeMove(Random rng)
            {
                if (_cells.Count == 0) return null;
                int unit = RandomPlacedUnit(rng, movableOnly: true);
                if (unit < 0) return null;

                int cell = _cells[rng.Next(_cells.Count)];
                if (_unitAtCell[cell] >= 0) return null;

                int from = _cellOfUnit[unit];
                _unitAtCell[from] = -1;
                _cellOfUnit[unit] = cell;
                _unitAtCell[cell] = unit;

                return () =>
                {
                    _unitAtCell[cell] = -1;
                    _cellOfUnit[unit] = from;
                    _unitAtCell[from] = unit;
                };
            }

            private Action ProposeSwap(Random rng)
            {
                int a = RandomPlacedUnit(rng, movableOnly: true);
                if (a < 0) return null;
                int b = RandomPlacedUnit(rng);
                if (b < 0 || b == a) return null;

                int ca = _cellOfUnit[a];
                int cb = _cellOfUnit[b];

                _unitAtCell[ca] = b;
                _unitAtCell[cb] = a;
                _cellOfUnit[a] = cb;
                _cellOfUnit[b] = ca;

                return () =>
                {
                    _unitAtCell[ca] = a;
                    _unitAtCell[cb] = b;
                    _cellOfUnit[a] = ca;
                    _cellOfUnit[b] = cb;
                };
            }

            private Action ProposeBench(Random rng)
            {
                int unit = RandomPlacedUnit(rng, movableOnly: true);
                if (unit < 0 || CountPlaced() <= 1) return null;

                int cell = _cellOfUnit[unit];
                Remove(unit);

                return () =>
                {
                    _cellOfUnit[unit] = cell;
                    _unitAtCell[cell] = unit;
                    _load += _state.Units[unit].LoadCost;
                };
            }

            private Action ProposeDeploy(Random rng)
            {
                if (_cells.Count == 0) return null;
                int unit = RandomBenchedUnit(rng);
                if (unit < 0) return null;
                if (_load + _state.Units[unit].LoadCost > _state.LoadLimit) return null;

                int cell = FirstEmptyCell();
                if (cell < 0) return null;

                Place(unit, cell);

                return () =>
                {
                    Remove(unit);
                };
            }

            // ---------------- 工具 ----------------

            private double Evaluate()
            {
                _buffer.Clear();
                for (int u = 0; u < _cellOfUnit.Length; u++)
                {
                    if (_cellOfUnit[u] < 0) continue;
                    int cell = _cellOfUnit[u];
                    _buffer.Add(new PlacementSlot(_state.Units[u].Id, cell % _state.Grid.Width, cell / _state.Grid.Width));
                }

                PlacementOutcome ignored;
                return _evaluator.Evaluate(_state, _buffer, out ignored);
            }

            public LayoutPlan BuildPlan()
            {
                var plan = new LayoutPlan();
                PlacementOutcome outcome;
                plan.Score = _evaluator.Evaluate(_state, CurrentLayoutList(), out outcome);
                plan.Outcome = outcome;
                plan.Layout.AddRange(CurrentLayoutList());

                for (int u = 0; u < _state.Units.Count; u++)
                {
                    if (_cellOfUnit[u] < 0) plan.BenchedUnitIds.Add(_state.Units[u].Id);
                }

                plan.Moves = BuildMoves();
                plan.Notes.AddRange(Notes);
                plan.SwapCount = 0;
                plan.IsExact = false;
                return plan;
            }

            private List<PlacementSlot> CurrentLayoutList()
            {
                var list = new List<PlacementSlot>();
                for (int u = 0; u < _cellOfUnit.Length; u++)
                {
                    if (_cellOfUnit[u] < 0) continue;
                    int cell = _cellOfUnit[u];
                    list.Add(new PlacementSlot(_state.Units[u].Id, cell % _state.Grid.Width, cell / _state.Grid.Width));
                }
                return list;
            }

            private List<LayoutMove> BuildMoves()
            {
                return LayoutSequencer.Build(_state, CurrentLayoutList());
            }

            public List<string> Notes = new List<string>();

            private string NameOf(int unitId)
            {
                for (int i = 0; i < _state.Units.Count; i++)
                {
                    if (_state.Units[i].Id == unitId) return _state.Units[i].Name;
                }
                return "#" + unitId;
            }

            private void RebuildCellIndex()
            {
                for (int i = 0; i < _unitAtCell.Length; i++) _unitAtCell[i] = -1;
                for (int u = 0; u < _cellOfUnit.Length; u++)
                {
                    if (_cellOfUnit[u] >= 0) _unitAtCell[_cellOfUnit[u]] = u;
                }
            }

            private void Place(int unit, int cell)
            {
                _cellOfUnit[unit] = cell;
                _unitAtCell[cell] = unit;
                _load += _state.Units[unit].LoadCost;
            }

            private void Remove(int unit)
            {
                int cell = _cellOfUnit[unit];
                if (cell < 0) return;
                _unitAtCell[cell] = -1;
                _cellOfUnit[unit] = -1;
                _load -= _state.Units[unit].LoadCost;
            }

            private int FirstEmptyCell()
            {
                for (int i = 0; i < _cells.Count; i++)
                {
                    if (_unitAtCell[_cells[i]] < 0) return _cells[i];
                }
                return -1;
            }

            private int RandomPlacedUnit(Random rng, bool movableOnly = false)
            {
                int count = 0;
                for (int u = 0; u < _cellOfUnit.Length; u++)
                {
                    if (_cellOfUnit[u] < 0) continue;
                    if (movableOnly && _state.Units[u].Pinned) continue;
                    count++;
                }
                if (count == 0) return -1;

                int pick = rng.Next(count);
                for (int u = 0; u < _cellOfUnit.Length; u++)
                {
                    if (_cellOfUnit[u] < 0) continue;
                    if (movableOnly && _state.Units[u].Pinned) continue;
                    if (pick-- == 0) return u;
                }
                return -1;
            }

            private int RandomBenchedUnit(Random rng)
            {
                int count = 0;
                for (int u = 0; u < _cellOfUnit.Length; u++) if (_cellOfUnit[u] < 0) count++;
                if (count == 0) return -1;

                int pick = rng.Next(count);
                for (int u = 0; u < _cellOfUnit.Length; u++)
                {
                    if (_cellOfUnit[u] >= 0) continue;
                    if (pick-- == 0) return u;
                }
                return -1;
            }

            private int CountPlaced()
            {
                int n = 0;
                for (int u = 0; u < _cellOfUnit.Length; u++) if (_cellOfUnit[u] >= 0) n++;
                return n;
            }

            private double ValueOf(int unit)
            {
                var card = _state.Units[unit];
                if (!card.Powerable)
                {
                    // 辅助部件：不产战力，但技能有用，不要优先裁掉
                    return (card.FullLoadValue > 0 ? card.FullLoadValue : 6.0) * 10 + 1;
                }
                return card.FullLoadValue * 10 + card.SlotRaritySum + card.LoadCost * 0.01;
            }
        }
    }
}
