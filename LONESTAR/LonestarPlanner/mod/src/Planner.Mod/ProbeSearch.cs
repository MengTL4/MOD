using System;
using System.Collections.Generic;
using LonestarPlanner.Core;
using UnityEngine;

namespace LonestarPlanner.Mod
{
    public sealed class ProbeSearchResult
    {
        public List<SwapMove> Swaps = new List<SwapMove>();
        public int Shift;
        public Plan Energy;
        public EngineProbeResult Probe;
        public double Score;
        public int Probes;
        public float ElapsedSeconds;
        public string Note = string.Empty;

        public int SwapCount
        {
            get { return Swaps == null ? 0 : Swaps.Count; }
        }
    }

    /// <summary>
    /// 以"引擎实测分数"为目标函数的搜索，**分帧推进**。
    ///
    /// 为什么分帧：用户可能要求探索 30~60 秒，而 Unity 的 Update 里同步跑这么久
    /// 会把画面完全卡死。这里每次 Step 只花几毫秒，面板可以实时显示进度。
    ///
    /// 搜索阶段：
    ///   Seeds       模型提议的起点（换位变体 × 移动 × 能量方案）
    ///   RandomPlans 完全不看模型的随机装载方案
    ///   RandomSwaps 随机单步换位（模型只负责给它配能量方案）
    ///   LocalSearch 在最优点周围做邻域搜索（改目标/调顺序/拿掉/加回）
    ///   Perturb     局部最优后随机扰动，再继续局部搜索（跳出局部最优）
    ///
    /// 每一步的优劣都由游戏自己算出来的行结果决定。
    /// </summary>
    public sealed class ProbeSearchSession
    {
        public const double SwapPenalty = 6.0;
        public const double ShiftPenalty = 8.0;

        private enum Phase
        {
            Seeds,
            RandomPlans,
            RandomSwaps,
            LocalSearch,
            Perturb,
            Done,
        }

        private readonly BattleSnapshot _snapshot;
        private readonly EngineOracle _oracle;
        private readonly ScorerOptions _scorerOptions;
        private readonly EnergySolverOptions _energyOptions;
        private readonly int _maxProbes;
        private readonly float _deadline;
        private readonly System.Random _rng = new System.Random(20260818);

        private readonly List<SeedCandidate> _seedQueue = new List<SeedCandidate>();
        private readonly List<Plan> _neighborQueue = new List<Plan>();
        private readonly HashSet<string> _seenSwapKeys = new HashSet<string>();

        private Phase _phase = Phase.Seeds;
        private int _seedIndex;
        private ProbeSearchResult _best;
        private int _probes;
        private float _startTime;
        private int _localImprovements;
        private int _perturbations;
        private int _randomPlansDone;
        private int _randomSwapsDone;
        private readonly int _randomPlansLimit;
        private readonly int _randomSwapsLimit;
        private Plan _currentEnergy;
        private List<SwapMove> _currentSwaps;
        private int _currentShift;
        private double _currentScore = double.NegativeInfinity;

        public ProbeSearchSession(
            BattleSnapshot snapshot,
            EngineOracle oracle,
            ScorerOptions scorerOptions,
            EnergySolverOptions energyOptions,
            int maxSwaps,
            int maxProbes,
            float timeBudgetSeconds)
        {
            _snapshot = snapshot;
            _oracle = oracle;
            _scorerOptions = scorerOptions ?? new ScorerOptions();
            _energyOptions = energyOptions ?? new EnergySolverOptions();
            // 有时间预算时用时间控制，次数只作安全上限（防止极端情况）
            _maxProbes = timeBudgetSeconds > 0f ? int.MaxValue : Math.Max(1, maxProbes);
            _startTime = Time.realtimeSinceStartup;
            _deadline = timeBudgetSeconds > 0f ? _startTime + timeBudgetSeconds : float.MaxValue;

            // 有时间预算时用固定上限（不然随机阶段会吃满全部时间），没预算时按次数比例
            int phaseLimit = _maxProbes == int.MaxValue ? 3000 : Math.Max(8, _maxProbes / 5);
            _randomPlansLimit = phaseLimit;
            _randomSwapsLimit = phaseLimit;

            BuildSeeds(maxSwaps);
        }

        private void SyncCurrentWithBest()
        {
            if (_best == null) return;
            _currentEnergy = _best.Energy;
            _currentSwaps = _best.Swaps;
            _currentShift = _best.Shift;
            _currentScore = _best.Score;
        }

        public bool Done
        {
            get { return _phase == Phase.Done; }
        }

        public ProbeSearchResult Best
        {
            get { return _best; }
        }

        public string Progress
        {
            get
            {
                float elapsed = Time.realtimeSinceStartup - _startTime;
                string phase;
                switch (_phase)
                {
                    case Phase.Seeds: phase = "种子"; break;
                    case Phase.RandomPlans: phase = "随机装载"; break;
                    case Phase.RandomSwaps: phase = "随机换位"; break;
                    case Phase.LocalSearch: phase = "局部搜索"; break;
                    case Phase.Perturb: phase = "跳出局部最优"; break;
                    default: phase = "完成"; break;
                }

                return phase + "  " + elapsed.ToString("0.0") + "s  实测 " + _probes
                       + (_best == null ? string.Empty : "  最优 " + _best.Score.ToString("0.##"));
            }
        }

        /// <summary>推进搜索，最多花 timeSliceSeconds 秒（或把工作队列跑空）。</summary>
        public void Step(float timeSliceSeconds)
        {
            if (_phase == Phase.Done) return;

            float sliceStart = Time.realtimeSinceStartup;
            int guard = 0;

            while (!Done && guard++ < 512)
            {
                if (_probes >= _maxProbes) { Finish(); return; }
                if (Time.realtimeSinceStartup >= _deadline) { Finish(); return; }
                if (Time.realtimeSinceStartup - sliceStart >= timeSliceSeconds) return;

                DoOneWorkUnit();
            }
        }

        public ProbeSearchResult Finish()
        {
            _phase = Phase.Done;
            if (_best == null)
            {
                return new ProbeSearchResult { Note = "实测全部失败（探针不可用）", Probes = _probes };
            }

            _best.Probes = _probes;
            _best.ElapsedSeconds = Time.realtimeSinceStartup - _startTime;
            _best.Note = "实测 " + _probes + " 次 / " + _best.ElapsedSeconds.ToString("0.0") + "s";
            return _best;
        }

        // ------------------------------------------------------------------

        private void DoOneWorkUnit()
        {
            switch (_phase)
            {
                case Phase.Seeds:
                    WorkSeeds();
                    break;
                case Phase.RandomPlans:
                    WorkRandomPlan();
                    break;
                case Phase.RandomSwaps:
                    WorkRandomSwap();
                    break;
                case Phase.LocalSearch:
                    WorkLocalSearch();
                    break;
                case Phase.Perturb:
                    WorkPerturb();
                    break;
            }
        }

        private void BuildSeeds(int maxSwaps)
        {
            var variants = new List<List<SwapMove>>();
            variants.Add(new List<SwapMove>());
            _seenSwapKeys.Add(KeyOf(variants[0]));

            if (maxSwaps > 0 && _snapshot.Placement != null && _snapshot.Placement.SwapBudget > 0)
            {
                try
                {
                    var swapPlan = new SwapSolver(new ApproxPlacementEvaluator(), new SwapSolverOptions
                    {
                        MaxSwaps = Math.Min(maxSwaps, _snapshot.Placement.SwapBudget),
                        BeamWidth = 24,
                    }).Solve(_snapshot.Placement);

                    if (swapPlan != null && swapPlan.Moves.Count > 0)
                    {
                        var swaps = new List<SwapMove>();
                        for (int i = 0; i < swapPlan.Moves.Count; i++)
                        {
                            var m = swapPlan.Moves[i];
                            swaps.Add(new SwapMove(m.FromX, m.FromY, m.ToX, m.ToY));
                        }
                        if (_seenSwapKeys.Add(KeyOf(swaps))) variants.Add(swaps);
                    }

                    var supportSwaps = BuildSupportSwapCandidates(_snapshot, 8);
                    for (int i = 0; i < supportSwaps.Count; i++)
                    {
                        if (_seenSwapKeys.Add(KeyOf(supportSwaps[i]))) variants.Add(supportSwaps[i]);
                    }

                    // 关键种子：把某个守不住的行的部件整体挪走。
                    // 反编译结算段：某一方没有部件的行会被整行跳过（不掉血），
                    // 所以"空掉一行"是实打实的免伤手段，光靠能量分配做不到。
                    var vacate = BuildVacateRowCandidates(_snapshot, Math.Min(maxSwaps, _snapshot.Placement.SwapBudget), 12);
                    for (int i = 0; i < vacate.Count; i++)
                    {
                        if (_seenSwapKeys.Add(KeyOf(vacate[i]))) variants.Add(vacate[i]);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Planner] seed swap candidates failed: " + ex.Message);
                }
            }

            int perVariant = variants.Count > 3 ? 2 : 4;

            for (int v = 0; v < variants.Count; v++)
            {
                var swaps = variants[v];
                BattleState variantBattle;
                try
                {
                    variantBattle = ApplySwaps(_snapshot.Battle, _snapshot.Placement, swaps);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Planner] ApplySwaps failed: " + ex.Message);
                    continue;
                }

                List<BattleTurnPlan> candidates;
                try
                {
                    var solver = new EnergySolver(new ApproxScorer(_scorerOptions), _energyOptions);
                    candidates = new BattlePlanner(solver).PlanCandidates(variantBattle);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Planner] seed generation failed: " + ex.Message);
                    continue;
                }

                for (int c = 0; c < candidates.Count && c < perVariant; c++)
                {
                    _seedQueue.Add(new SeedCandidate { Swaps = swaps, Plan = candidates[c] });
                }
            }
        }

        private void WorkSeeds()
        {
            if (_seedIndex >= _seedQueue.Count)
            {
                _phase = Phase.RandomPlans;
                SyncCurrentWithBest();
                Debug.Log("[Planner] probe seeds done: best=" + (_best == null ? "-" : _best.Score.ToString("0.##"))
                          + " (" + _probes + " probes)");
                return;
            }

            var seed = _seedQueue[_seedIndex++];
            var probe = _oracle.Probe(_snapshot, seed.Plan.Energy, seed.Plan.Shift, seed.Swaps, _scorerOptions);
            _probes++;
            if (!probe.Success) return;

            double score = Score(probe, seed.Swaps, seed.Plan.Shift);
            if (IsBetter(score, seed.Plan.Energy))
            {
                _best = new ProbeSearchResult
                {
                    Swaps = seed.Swaps,
                    Shift = seed.Plan.Shift,
                    Energy = seed.Plan.Energy,
                    Probe = probe,
                    Score = score,
                };
            }
        }

        private void WorkRandomPlan()
        {
            if (_randomPlansDone++ >= _randomPlansLimit)
            {
                _phase = Phase.RandomSwaps;
                return;
            }

            Plan plan = null;
            for (int attempt = 0; attempt < 64 && plan == null; attempt++)
            {
                var built = BuildRandomPlan(_snapshot.Battle, _rng);
                if (built != null && built.Steps.Count > 0) plan = built;
                _randomPlansDone++;
            }

            if (plan == null)
            {
                _phase = Phase.RandomSwaps;
                return;
            }

            var probe = _oracle.Probe(_snapshot, plan, _best == null ? 0 : _best.Shift, _best == null ? null : _best.Swaps, _scorerOptions);
            _probes++;
            if (!probe.Success) return;

            double score = Score(probe, _best == null ? null : _best.Swaps, _best == null ? 0 : _best.Shift);
            if (IsBetter(score, plan))
            {
                if (_best == null) _best = new ProbeSearchResult();
                _best.Energy = plan;
                _best.Probe = probe;
                _best.Score = score;
                SyncCurrentWithBest();
                Debug.Log("[Planner] random restart improved -> " + score.ToString("0.##") + " : " + probe.Summary());
            }
        }

        private void WorkRandomSwap()
        {
            if (_randomSwapsDone++ >= _randomSwapsLimit)
            {
                _phase = Phase.LocalSearch;
                SyncCurrentWithBest();
                SeedNeighbors();
                return;
            }

            List<SwapMove> swaps = null;
            for (int attempt = 0; attempt < 64 && swaps == null; attempt++)
            {
                var built = BuildRandomSwap(_snapshot, _rng);
                if (built != null && _seenSwapKeys.Add(KeyOf(built))) swaps = built;
                _randomSwapsDone++;
            }

            if (swaps == null)
            {
                _phase = Phase.LocalSearch;
                SyncCurrentWithBest();
                SeedNeighbors();
                return;
            }

            BattleState variantBattle;
            try
            {
                variantBattle = ApplySwaps(_snapshot.Battle, _snapshot.Placement, swaps);
            }
            catch
            {
                return;
            }

            List<BattleTurnPlan> candidates;
            try
            {
                var solver = new EnergySolver(new ApproxScorer(_scorerOptions), _energyOptions);
                candidates = new BattlePlanner(solver).PlanCandidates(variantBattle);
            }
            catch
            {
                return;
            }

            if (candidates.Count == 0) return;

            var candidate = candidates[0];
            var probe = _oracle.Probe(_snapshot, candidate.Energy, candidate.Shift, swaps, _scorerOptions);
            _probes++;
            if (!probe.Success) return;

            double score = Score(probe, swaps, candidate.Shift);
            if (IsBetter(score, candidate.Energy))
            {
                _best = new ProbeSearchResult
                {
                    Swaps = swaps,
                    Shift = candidate.Shift,
                    Energy = candidate.Energy,
                    Probe = probe,
                    Score = score,
                };
                Debug.Log("[Planner] random swap improved -> " + score.ToString("0.##") + " : " + probe.Summary());
            }
        }

        private void SeedNeighbors()
        {
            _neighborQueue.Clear();
            var from = _currentEnergy ?? (_best == null ? null : _best.Energy);
            if (from == null) return;

            _neighborQueue.AddRange(GenerateNeighbors(_snapshot.Battle, from, _rng, 10));
        }

        private void WorkLocalSearch()
        {
            if (_best == null || _currentEnergy == null)
            {
                _phase = Phase.Perturb;
                return;
            }

            if (_neighborQueue.Count == 0)
            {
                _phase = Phase.Perturb;
                return;
            }

            var plan = _neighborQueue[0];
            _neighborQueue.RemoveAt(0);

            var probe = _oracle.Probe(_snapshot, plan, _currentShift, _currentSwaps, _scorerOptions);
            _probes++;
            if (!probe.Success) return;

            double score = Score(probe, _currentSwaps, _currentShift);

            // 比"当前点"好就接受（哪怕还不如最优解，这样可以沿着坡走）
            if (score > _currentScore + 1e-6)
            {
                _currentEnergy = plan;
                _currentScore = score;
                _localImprovements++;

                bool betterThanBest = _best == null || score > _best.Score + 1e-6;
                bool tieButFrugal = _best != null
                                    && Math.Abs(score - _best.Score) <= 1e-6
                                    && plan.Steps.Count < (_best.Energy == null ? int.MaxValue : _best.Energy.Steps.Count);

                if (betterThanBest || tieButFrugal)
                {
                    _best.Energy = plan;
                    _best.Probe = probe;
                    _best.Score = score;
                    _best.Swaps = _currentSwaps;
                    _best.Shift = _currentShift;
                    Debug.Log("[Planner] local improve -> " + score.ToString("0.##") + " : " + probe.Summary());
                }

                SeedNeighbors();
            }
        }

        private void WorkPerturb()
        {
            if (_best == null || _best.Energy == null)
            {
                Finish();
                return;
            }

            // 没有时间预算时用 60 次扰动兜底；有时间预算就一直跑到时间用完
            if (_deadline == float.MaxValue && _perturbations++ >= 60)
            {
                Finish();
                return;
            }
            _perturbations++;

            var basePlan = _currentEnergy ?? _best.Energy;
            var perturbed = Perturb(basePlan, 1 + _rng.Next(3));
            if (perturbed == null || perturbed.Steps.Count == 0)
            {
                Finish();
                return;
            }

            var probe = _oracle.Probe(_snapshot, perturbed, _currentShift, _currentSwaps, _scorerOptions);
            _probes++;
            if (!probe.Success) return;

            // 接受扰动点作为新的搜索起点（即使更差），继续爬坡
            double score = Score(probe, _currentSwaps, _currentShift);
            _currentEnergy = perturbed;
            _currentScore = score;

            if (_best == null || score > _best.Score + 1e-6)
            {
                _best.Energy = perturbed;
                _best.Probe = probe;
                _best.Score = score;
                _best.Swaps = _currentSwaps;
                _best.Shift = _currentShift;
                Debug.Log("[Planner] perturb improved -> " + score.ToString("0.##") + " : " + probe.Summary());
            }

            SeedNeighbors();
            _phase = Phase.LocalSearch;
        }

        private Plan Perturb(Plan basePlan, int moves)
        {
            var plan = new Plan();
            plan.Steps.AddRange(basePlan.Steps);

            for (int i = 0; i < moves; i++)
            {
                var neighbors = GenerateNeighbors(_snapshot.Battle, plan, _rng, 1);
                if (neighbors.Count == 0) break;
                plan = neighbors[0];
            }

            return plan;
        }

        // ---------------- 工具 ----------------

        private double Score(EngineProbeResult probe, List<SwapMove> swaps, int shift)
        {
            int swapCount = swaps == null ? 0 : swaps.Count;
            return probe.Score - swapCount * SwapPenalty - Math.Abs(shift) * ShiftPenalty;
        }

        private bool IsBetter(double score, Plan candidate)
        {
            if (_best == null) return true;
            if (score > _best.Score + 1e-6) return true;
            if (score < _best.Score - 1e-6) return false;

            int a = candidate == null ? int.MaxValue : candidate.Steps.Count;
            int b = _best.Energy == null ? int.MaxValue : _best.Energy.Steps.Count;
            return a < b;
        }

        private struct SeedCandidate
        {
            public List<SwapMove> Swaps;
            public BattleTurnPlan Plan;
        }

        /// <summary>随机装载方案：token 随机打散到部件，偶尔留手。完全不看模型。</summary>
        private static Plan BuildRandomPlan(BattleState state, System.Random rng)
        {
            if (state == null || state.Units.Count == 0 || state.Hand.Count == 0) return null;

            var plan = new Plan();
            var tokens = new List<EnergyToken>(state.Hand);

            for (int i = tokens.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                var tmp = tokens[i];
                tokens[i] = tokens[j];
                tokens[j] = tmp;
            }

            for (int i = 0; i < tokens.Count; i++)
            {
                if (rng.NextDouble() < 0.15) continue;

                var unit = state.Units[rng.Next(state.Units.Count)];
                plan.Steps.Add(new PlanStep
                {
                    TokenId = tokens[i].Id,
                    TokenPower = tokens[i].Power,
                    TokenColor = tokens[i].Color,
                    UnitId = unit.Id,
                    UnitName = unit.Name,
                    UnitRow = unit.Row,
                    SlotIndex = 0,
                });
            }

            return plan;
        }

        /// <summary>邻域：改一枚 token 的目标 / 调换顺序 / 拿掉一枚 / 加回一枚。</summary>
        private static List<Plan> GenerateNeighbors(BattleState state, Plan current, System.Random rng, int count)
        {
            var list = new List<Plan>();
            if (current == null || state == null) return list;

            var steps = new List<PlanStep>(current.Steps);
            var usedTokenIds = new HashSet<int>();
            for (int i = 0; i < steps.Count; i++) usedTokenIds.Add(steps[i].TokenId);

            var heldTokens = new List<EnergyToken>();
            for (int i = 0; i < state.Hand.Count; i++)
            {
                if (!usedTokenIds.Contains(state.Hand[i].Id)) heldTokens.Add(state.Hand[i]);
            }

            for (int attempt = 0; attempt < count * 6 && list.Count < count; attempt++)
            {
                int kind = rng.Next(4);
                var candidate = new List<PlanStep>(steps);

                if (kind == 0 && candidate.Count > 0)
                {
                    int i = rng.Next(candidate.Count);
                    var unit = state.Units[rng.Next(state.Units.Count)];
                    if (unit == null || unit.Id == candidate[i].UnitId) continue;

                    var step = CloneStep(candidate[i]);
                    step.UnitId = unit.Id;
                    step.UnitName = unit.Name;
                    step.UnitRow = unit.Row;
                    candidate[i] = step;
                }
                else if (kind == 1 && candidate.Count >= 2)
                {
                    int i = rng.Next(candidate.Count);
                    int j = rng.Next(candidate.Count);
                    if (i == j) continue;
                    var tmp = candidate[i];
                    candidate[i] = candidate[j];
                    candidate[j] = tmp;
                }
                else if (kind == 2 && candidate.Count > 1)
                {
                    candidate.RemoveAt(rng.Next(candidate.Count));
                }
                else if (kind == 3 && heldTokens.Count > 0 && state.Units.Count > 0)
                {
                    var token = heldTokens[rng.Next(heldTokens.Count)];
                    var unit = state.Units[rng.Next(state.Units.Count)];
                    candidate.Insert(rng.Next(candidate.Count + 1), new PlanStep
                    {
                        TokenId = token.Id,
                        TokenPower = token.Power,
                        TokenColor = token.Color,
                        UnitId = unit.Id,
                        UnitName = unit.Name,
                        UnitRow = unit.Row,
                        SlotIndex = 0,
                    });
                }
                else
                {
                    continue;
                }

                var plan = new Plan();
                plan.Steps.AddRange(candidate);
                list.Add(plan);
            }

            return list;
        }

        private static PlanStep CloneStep(PlanStep step)
        {
            return new PlanStep
            {
                TokenId = step.TokenId,
                TokenPower = step.TokenPower,
                TokenColor = step.TokenColor,
                UnitId = step.UnitId,
                UnitName = step.UnitName,
                UnitRow = step.UnitRow,
                SlotIndex = step.SlotIndex,
                CompletesUnit = step.CompletesUnit,
            };
        }

        /// <summary>随机单步换位：挑两个可用格，至少一格有部件。</summary>
        private static List<SwapMove> BuildRandomSwap(BattleSnapshot snapshot, System.Random rng)
        {
            if (snapshot == null || snapshot.Placement == null) return null;

            var placement = snapshot.Placement;
            var grid = placement.Grid;
            var usable = grid.UsableCells();
            if (usable.Count < 2 || placement.CurrentLayout.Count == 0) return null;

            int a = usable[rng.Next(usable.Count)];
            int b = usable[rng.Next(usable.Count)];
            if (a == b) return null;

            bool hasA = false, hasB = false;
            for (int i = 0; i < placement.CurrentLayout.Count; i++)
            {
                int idx = grid.IndexOf(placement.CurrentLayout[i].X, placement.CurrentLayout[i].Y);
                if (idx == a) hasA = true;
                if (idx == b) hasB = true;
            }
            if (!hasA && !hasB) return null;

            return new List<SwapMove> { new SwapMove(grid.XOf(a), grid.YOf(a), grid.XOf(b), grid.YOf(b)) };
        }


        /// <summary>
        /// 生成"把某一行清空"的换位方案：把该行所有部件搬到另一行的空格。
        /// 依据：结算时 playerShipController == null 的行整行跳过，不会掉血。
        /// 优先搬到"敌方没有部件的行"（那里放东西既不挨打也不浪费）。
        /// </summary>
        private static List<List<SwapMove>> BuildVacateRowCandidates(BattleSnapshot snapshot, int maxSwaps, int limit)
        {
            var result = new List<List<SwapMove>>();
            if (maxSwaps <= 0 || snapshot == null || snapshot.Placement == null) return result;

            var placement = snapshot.Placement;
            var grid = placement.Grid;
            int rows = grid.Height;

            var unitCells = new List<int>[rows];
            var occupied = new HashSet<int>();
            for (int r = 0; r < rows; r++) unitCells[r] = new List<int>();

            for (int i = 0; i < placement.CurrentLayout.Count; i++)
            {
                var slot = placement.CurrentLayout[i];
                int cell = grid.IndexOf(slot.X, slot.Y);
                occupied.Add(cell);
                if (slot.Y >= 0 && slot.Y < rows) unitCells[slot.Y].Add(cell);
            }

            var freeByRow = new List<int>[rows];
            var usable = grid.UsableCells();
            for (int r = 0; r < rows; r++) freeByRow[r] = new List<int>();
            for (int i = 0; i < usable.Count; i++)
            {
                int cell = usable[i];
                if (occupied.Contains(cell)) continue;
                freeByRow[grid.RowOf(cell)].Add(cell);
            }

            var enemyEmpty = new bool[rows];
            for (int r = 0; r < rows; r++)
            {
                enemyEmpty[r] = snapshot.Battle.EnemyRowHasUnit != null
                                && r < snapshot.Battle.EnemyRowHasUnit.Length
                                && !snapshot.Battle.EnemyRowHasUnit[r];
            }

            for (int from = 0; from < rows; from++)
            {
                var moving = unitCells[from];
                if (moving.Count == 0 || moving.Count > maxSwaps) continue;

                // 目的地排序：敌方空行优先
                var targets = new List<int>();
                for (int t = 0; t < rows; t++) if (t != from) targets.Add(t);
                targets.Sort((a, b) => enemyEmpty[b].CompareTo(enemyEmpty[a]));

                for (int ti = 0; ti < targets.Count; ti++)
                {
                    int to = targets[ti];
                    var free = freeByRow[to];
                    if (free.Count < moving.Count) continue;

                    var swaps = new List<SwapMove>();
                    for (int i = 0; i < moving.Count; i++)
                    {
                        swaps.Add(new SwapMove(grid.XOf(moving[i]), grid.YOf(moving[i]),
                                               grid.XOf(free[i]), grid.YOf(free[i])));
                    }

                    result.Add(swaps);
                    if (result.Count >= limit) return result;

                    // 同一行也做一份"只搬走可以装满的目标行"的变体后停止
                    break;
                }
            }

            return result;
        }

        /// <summary>构造"把辅助部件挪到别的行"的候选：优先找空位。</summary>
        private static List<List<SwapMove>> BuildSupportSwapCandidates(BattleSnapshot snapshot, int limit)
        {
            var result = new List<List<SwapMove>>();
            if (limit <= 0 || snapshot == null) return result;

            var placement = snapshot.Placement;
            var grid = placement.Grid;

            var posOfUnit = new Dictionary<int, Vector2Int>();
            var occupied = new HashSet<int>();
            for (int i = 0; i < placement.CurrentLayout.Count; i++)
            {
                var slot = placement.CurrentLayout[i];
                posOfUnit[slot.UnitId] = new Vector2Int(slot.X, slot.Y);
                occupied.Add(grid.IndexOf(slot.X, slot.Y));
            }

            var supports = new List<int>();
            for (int i = 0; i < placement.Units.Count; i++)
            {
                if (!placement.Units[i].Powerable) supports.Add(placement.Units[i].Id);
            }
            if (supports.Count == 0) return result;

            var usable = grid.UsableCells();
            for (int s = 0; s < supports.Count && result.Count < limit; s++)
            {
                Vector2Int from;
                if (!posOfUnit.TryGetValue(supports[s], out from)) continue;

                for (int r = 0; r < grid.Height && result.Count < limit; r++)
                {
                    if (r == from.y) continue;

                    int targetCell = -1;
                    for (int c = 0; c < usable.Count; c++)
                    {
                        int cell = usable[c];
                        if (grid.RowOf(cell) != r) continue;
                        if (occupied.Contains(cell)) continue;
                        targetCell = cell;
                        break;
                    }
                    if (targetCell < 0) continue;

                    result.Add(new List<SwapMove>
                    {
                        new SwapMove(from.x, from.y, grid.XOf(targetCell), grid.YOf(targetCell))
                    });
                }
            }

            return result;
        }

        private static string KeyOf(List<SwapMove> swaps)
        {
            if (swaps == null || swaps.Count == 0) return "none";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < swaps.Count; i++)
            {
                sb.Append(swaps[i].X1).Append(',').Append(swaps[i].Y1).Append('-')
                  .Append(swaps[i].X2).Append(',').Append(swaps[i].Y2).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>把换位应用到 BattleState 的行号上（用于生成该变体的能量方案）。</summary>
        public static BattleState ApplySwaps(BattleState battle, PlacementState placement, IReadOnlyList<SwapMove> swaps)
        {
            var clone = battle.Clone();
            if (swaps == null || swaps.Count == 0 || placement == null) return clone;

            var cellOfUnit = new Dictionary<int, Vector2Int>();
            var unitAtCell = new Dictionary<int, int>();

            for (int i = 0; i < placement.CurrentLayout.Count; i++)
            {
                var slot = placement.CurrentLayout[i];
                cellOfUnit[slot.UnitId] = new Vector2Int(slot.X, slot.Y);
                unitAtCell[slot.X + slot.Y * 1000] = slot.UnitId;
            }

            for (int i = 0; i < swaps.Count; i++)
            {
                var s = swaps[i];
                int keyA = s.X1 + s.Y1 * 1000;
                int keyB = s.X2 + s.Y2 * 1000;

                int unitA, unitB;
                bool hasA = unitAtCell.TryGetValue(keyA, out unitA);
                bool hasB = unitAtCell.TryGetValue(keyB, out unitB);

                unitAtCell.Remove(keyA);
                unitAtCell.Remove(keyB);

                if (hasA)
                {
                    cellOfUnit[unitA] = new Vector2Int(s.X2, s.Y2);
                    unitAtCell[keyB] = unitA;
                }
                if (hasB)
                {
                    cellOfUnit[unitB] = new Vector2Int(s.X1, s.Y1);
                    unitAtCell[keyA] = unitB;
                }
            }

            for (int i = 0; i < clone.Units.Count; i++)
            {
                Vector2Int pos;
                if (cellOfUnit.TryGetValue(clone.Units[i].Id, out pos))
                {
                    clone.Units[i].Row = pos.y;
                }
            }

            // 换位可能把某一行清空 —— 空行在结算里会被整行跳过，必须同步给评分器
            if (clone.RowCount > 0)
            {
                var playerRows = new bool[clone.RowCount];
                for (int i = 0; i < clone.Units.Count; i++)
                {
                    int row = clone.Units[i].Row;
                    if (row >= 0 && row < clone.RowCount) playerRows[row] = true;
                }
                clone.PlayerRowHasUnit = playerRows;
            }

            return clone;
        }
    }
}
