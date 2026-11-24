using System;
using System.Collections.Generic;
using System.Text;

namespace LonestarPlanner.Core
{
    public sealed class EnergySolverOptions
    {
        /// <summary>token 数不超过它、且部件数不超过 ExactUnitLimit 时使用穷举（精确解）。</summary>
        public int ExactTokenLimit = 4;

        /// <summary>部件数不超过它时才使用穷举。</summary>
        public int ExactUnitLimit = 6;

        /// <summary>超过穷举上限时的束搜索宽度。</summary>
        public int BeamWidth = 384;

        /// <summary>穷举节点上限，防止意外爆掉（超过后返回当前最优解，IsExact=false）。</summary>
        public int MaxNodes = 400_000;

        public EnergySolverOptions Clone()
        {
            return (EnergySolverOptions)MemberwiseClone();
        }
    }

    /// <summary>
    /// 能量分配求解器。
    ///
    /// 问题形式：手牌 token → 部件槽位，一枚 token 只能用一次；
    /// 装入时遵循游戏的"第一个可装槽"规则，因此**装载顺序本身就是决策的一部分**
    /// （高级 token 先打进低级槽，会把低级 token 挤出去）。
    ///
    /// 搜索方式：对"装载序列"做 DFS（先某枚 token 打某个部件，再下一枚……）。
    /// 因为计分对已装能量单调不降，只需在"再也装不下任何 token"的终局上评估，
    /// 中间节点只用上界剪枝。规模超过穷举上限时切换成束搜索（启发式，快）。
    /// </summary>
    public sealed class EnergySolver
    {
        private readonly IAssignmentScorer _scorer;
        private readonly EnergySolverOptions _options;

        public EnergySolver(IAssignmentScorer scorer, EnergySolverOptions options = null)
        {
            _scorer = scorer ?? throw new ArgumentNullException(nameof(scorer));
            _options = options ?? new EnergySolverOptions();
        }

        public IAssignmentScorer Scorer
        {
            get { return _scorer; }
        }

        public EnergySolverOptions Options
        {
            get { return _options; }
        }

        public Plan Solve(BattleState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            string invalid = state.Validate();
            if (invalid != null) throw new ArgumentException("invalid BattleState: " + invalid, nameof(state));

            var context = new SearchContext(this, state.Clone());

            if (context.CanSolveExactly)
            {
                context.IsExact = true;
                context.ExploreExhaustive();
            }
            else
            {
                context.IsExact = false;
                context.ExploreBeam(_options.BeamWidth);
            }

            return context.Best ?? context.BuildEmptyPlan();
        }

        // ------------------------------------------------------------------
        private sealed class SearchContext
        {
            private readonly EnergySolver _owner;
            private readonly BattleState _state;
            private readonly UnitState[] _units;
            private readonly bool[] _used;
            private readonly List<PlanStep> _steps = new List<PlanStep>();
            private readonly HashSet<string> _seenLayouts = new HashSet<string>();
            private readonly List<EnergyToken> _remainingBuffer = new List<EnergyToken>();

            /// <summary>按"能量从大到小"排序后的 token 处理顺序，只影响搜索顺序，不影响完备性。</summary>
            private readonly int[] _tokenOrder;

            private int _nodes;
            private double _bestScore = double.NegativeInfinity;

            public Plan Best;
            public bool IsExact = true;

            public SearchContext(EnergySolver owner, BattleState state)
            {
                _owner = owner;
                _state = state;
                _units = state.Units.ToArray();
                _used = new bool[state.Hand.Count];
                _tokenOrder = BuildTokenOrder(state.Hand);

                // 空方案作为兜底基线
                RecordCurrentLayout(string.Empty);
            }

            public bool CanSolveExactly
            {
                get
                {
                    return _state.Hand.Count <= _owner._options.ExactTokenLimit
                           && _units.Length <= _owner._options.ExactUnitLimit;
                }
            }

            private IAssignmentScorer Scorer
            {
                get { return _owner._scorer; }
            }

            private EnergySolverOptions Options
            {
                get { return _owner._options; }
            }

            // ---------------- 穷举 ----------------

            public void ExploreExhaustive()
            {
                ExploreRecursive();
            }

            private void ExploreRecursive()
            {
                if (_nodes++ > Options.MaxNodes)
                {
                    IsExact = false;
                    return;
                }

                if (Prune())
                {
                    return;
                }

                bool expandedAny = false;

                for (int oi = 0; oi < _tokenOrder.Length; oi++)
                {
                    int t = _tokenOrder[oi];
                    if (_used[t]) continue;
                    if (HasEquivalentUnusedTokenEarlier(oi)) continue;

                    var token = _state.Hand[t];

                    for (int u = 0; u < _units.Length; u++)
                    {
                        var unit = _units[u];
                        if (HasEquivalentUnitEarlier(u)) continue;

                        int slotIndex = LoadRules.FindReceivingSlot(unit, token);
                        if (slotIndex < 0) continue;

                        expandedAny = true;

                        bool wasFull = unit.IsFull;
                        unit.Slots[slotIndex].Loaded = token;
                        _used[t] = true;
                        _steps.Add(MakeStep(token, unit, slotIndex, wasFull));

                        ExploreRecursive();

                        _steps.RemoveAt(_steps.Count - 1);
                        _used[t] = false;
                        unit.Slots[slotIndex].Loaded = null;

                        if (!IsExact) return;
                    }
                }

                // 终局（再也装不下任何 token）才评估 —— 计分单调，中间状态不可能更优。
                if (!expandedAny)
                {
                    RecordCurrentLayout(BuildSignature());
                }
            }

            /// <summary>
            /// 上界剪枝：当前分数 + 剩余 token 的乐观收益 &lt;= 已知最优，则不可能更好。
            /// </summary>
            private bool Prune()
            {
                if (double.IsNegativeInfinity(_bestScore)) return false;

                double current = Scorer.EstimateScore(_state, _units);

                _remainingBuffer.Clear();
                for (int i = 0; i < _state.Hand.Count; i++)
                {
                    if (!_used[i]) _remainingBuffer.Add(_state.Hand[i]);
                }
                if (_remainingBuffer.Count == 0) return false;

                double bound = current + Scorer.UpperBoundRemainingGain(_state, _units, _remainingBuffer);
                return bound <= _bestScore + 1e-9;
            }

            // ---------------- 束搜索 ----------------

            public void ExploreBeam(int width)
            {
                var level = new List<BeamNode> { BeamNode.Empty(_state) };

                for (int depth = 0; depth < _state.Hand.Count; depth++)
                {
                    var next = new List<BeamNode>();
                    var keys = new HashSet<string>();

                    for (int n = 0; n < level.Count; n++)
                    {
                        var node = level[n];

                        for (int oi = 0; oi < _tokenOrder.Length; oi++)
                        {
                            int t = _tokenOrder[oi];
                            if (node.Used[t]) continue;

                            var token = _state.Hand[t];

                            for (int u = 0; u < node.Units.Length; u++)
                            {
                                var unit = node.Units[u];
                                int slotIndex = LoadRules.FindReceivingSlot(unit, token);
                                if (slotIndex < 0) continue;

                                var child = node.Clone();
                                bool wasFull = child.Units[u].IsFull;
                                child.Units[u].Slots[slotIndex].Loaded = token;
                                child.Used[t] = true;
                                child.Steps.Add(MakeStep(token, child.Units[u], slotIndex, wasFull));

                                string key = child.Key();
                                if (!keys.Add(key)) continue;

                                child.Rank = Rank(child);
                                next.Add(child);
                                _nodes++;
                            }
                        }
                    }

                    if (next.Count == 0) break;

                    for (int n = 0; n < next.Count; n++)
                    {
                        AdoptBeamNode(next[n]);
                    }

                    next.Sort((a, b) => b.Rank.CompareTo(a.Rank));
                    if (next.Count > width) next.RemoveRange(width, next.Count - width);
                    level = next;
                }
            }

            private double Rank(BeamNode node)
            {
                double score = Scorer.EstimateScore(_state, node.Units);

                _remainingBuffer.Clear();
                for (int i = 0; i < _state.Hand.Count; i++)
                {
                    if (!node.Used[i]) _remainingBuffer.Add(_state.Hand[i]);
                }

                return score + Scorer.UpperBoundRemainingGain(_state, node.Units, _remainingBuffer);
            }

            private void AdoptBeamNode(BeamNode node)
            {
                if (!node.IsTerminal(_state.Hand)) return;

                BattleOutcome outcome;
                double score = Scorer.Score(_state, node.Units, out outcome);
                if (score > _bestScore + 1e-9)
                {
                    _bestScore = score;
                    Best = NewPlan(node.Steps, score, outcome);
                }
            }

            // ---------------- 记录 / 工具 ----------------

            private void RecordCurrentLayout(string signature)
            {
                if (signature.Length > 0 && !_seenLayouts.Add(signature)) return;

                BattleOutcome outcome;
                double score = Scorer.Score(_state, _units, out outcome);
                if (score > _bestScore + 1e-9)
                {
                    _bestScore = score;
                    Best = NewPlan(_steps, score, outcome);
                }
            }

            private Plan NewPlan(List<PlanStep> steps, double score, BattleOutcome outcome)
            {
                var plan = new Plan
                {
                    Score = score,
                    Outcome = outcome,
                    IsExact = IsExact,
                    NodesEvaluated = _nodes,
                };

                for (int i = 0; i < steps.Count; i++)
                {
                    plan.Steps.Add(steps[i]);
                }

                var usedIds = new HashSet<int>();
                for (int i = 0; i < steps.Count; i++) usedIds.Add(steps[i].TokenId);
                for (int i = 0; i < _state.Hand.Count; i++)
                {
                    if (!usedIds.Contains(_state.Hand[i].Id)) plan.UnusedTokenIds.Add(_state.Hand[i].Id);
                }

                return plan;
            }

            public Plan BuildEmptyPlan()
            {
                BattleOutcome outcome;
                double score = Scorer.Score(_state, _units, out outcome);
                return NewPlan(new List<PlanStep>(), score, outcome);
            }

            private PlanStep MakeStep(EnergyToken token, UnitState unit, int slotIndex, bool wasFull)
            {
                return new PlanStep
                {
                    TokenId = token.Id,
                    TokenPower = token.Power,
                    TokenColor = token.Color,
                    UnitId = unit.Id,
                    UnitName = unit.Name,
                    UnitRow = unit.Row,
                    SlotIndex = slotIndex,
                    CompletesUnit = !wasFull && unit.IsFull,
                };
            }

            private string BuildSignature()
            {
                var sb = new StringBuilder();
                for (int i = 0; i < _units.Length; i++)
                {
                    sb.Append(_units[i].Id).Append(':');
                    for (int s = 0; s < _units[i].Slots.Count; s++)
                    {
                        var loaded = _units[i].Slots[s].Loaded;
                        sb.Append(loaded == null ? "-" : loaded.Id.ToString()).Append(',');
                    }
                    sb.Append(';');
                }
                return sb.ToString();
            }

            // ---------------- 对称性破除 ----------------

            /// <summary>完全等价的 token 只尝试一次（同电量同颜色）。</summary>
            private bool HasEquivalentUnusedTokenEarlier(int orderIndex)
            {
                var token = _state.Hand[_tokenOrder[orderIndex]];
                for (int i = 0; i < orderIndex; i++)
                {
                    int other = _tokenOrder[i];
                    if (_used[other]) continue;
                    var candidate = _state.Hand[other];
                    if (candidate.Power == token.Power && candidate.Color == token.Color) return true;
                }
                return false;
            }

            /// <summary>完全等价的部件（同行、同槽位配置、同状态）只尝试一次。</summary>
            private bool HasEquivalentUnitEarlier(int unitIndex)
            {
                for (int i = 0; i < unitIndex; i++)
                {
                    if (UnitsEquivalent(_units[i], _units[unitIndex])) return true;
                }
                return false;
            }

            private static bool UnitsEquivalent(UnitState a, UnitState b)
            {
                if (ReferenceEquals(a, b)) return true;
                if (a.Row != b.Row) return false;
                if (a.Slots.Count != b.Slots.Count) return false;
                if (Math.Abs(a.FullLoadValue - b.FullLoadValue) > 1e-9) return false;
                if (a.FlatPowerBonus != b.FlatPowerBonus) return false;

                for (int i = 0; i < a.Slots.Count; i++)
                {
                    var sa = a.Slots[i];
                    var sb = b.Slots[i];
                    if (sa.Requirement != sb.Requirement) return false;
                    if (sa.Disturbed != sb.Disturbed) return false;
                    if (sa.LoadedPower != sb.LoadedPower) return false;
                }
                return true;
            }

            private static int[] BuildTokenOrder(List<EnergyToken> hand)
            {
                var order = new int[hand.Count];
                for (int i = 0; i < order.Length; i++) order[i] = i;

                Array.Sort(order, (x, y) =>
                {
                    int cmp = hand[y].Power.CompareTo(hand[x].Power);
                    if (cmp != 0) return cmp;
                    cmp = ((int)hand[y].Color).CompareTo((int)hand[x].Color);
                    if (cmp != 0) return cmp;
                    return hand[x].Id.CompareTo(hand[y].Id);
                });
                return order;
            }
        }

        // ------------------------------------------------------------------
        private sealed class BeamNode
        {
            public UnitState[] Units;
            public bool[] Used;
            public List<PlanStep> Steps;
            public double Rank;

            public static BeamNode Empty(BattleState state)
            {
                var units = new UnitState[state.Units.Count];
                for (int i = 0; i < units.Length; i++) units[i] = state.Units[i].Clone();
                return new BeamNode
                {
                    Units = units,
                    Used = new bool[state.Hand.Count],
                    Steps = new List<PlanStep>(),
                };
            }

            public BeamNode Clone()
            {
                var units = new UnitState[Units.Length];
                for (int i = 0; i < units.Length; i++) units[i] = Units[i].Clone();
                return new BeamNode
                {
                    Units = units,
                    Used = (bool[])Used.Clone(),
                    Steps = new List<PlanStep>(Steps),
                    Rank = Rank,
                };
            }

            public bool IsTerminal(IReadOnlyList<EnergyToken> hand)
            {
                for (int t = 0; t < hand.Count; t++)
                {
                    if (Used[t]) continue;
                    for (int u = 0; u < Units.Length; u++)
                    {
                        if (LoadRules.CanLoad(Units[u], hand[t])) return false;
                    }
                }
                return true;
            }

            public string Key()
            {
                var sb = new StringBuilder();
                for (int i = 0; i < Used.Length; i++) sb.Append(Used[i] ? '1' : '0');
                sb.Append('|');
                for (int i = 0; i < Units.Length; i++)
                {
                    sb.Append(Units[i].Id).Append(':');
                    for (int s = 0; s < Units[i].Slots.Count; s++)
                    {
                        var loaded = Units[i].Slots[s].Loaded;
                        sb.Append(loaded == null ? "-" : loaded.Id.ToString()).Append(',');
                    }
                    sb.Append(';');
                }
                return sb.ToString();
            }
        }
    }
}
