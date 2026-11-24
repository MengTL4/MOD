using System.Diagnostics;

namespace Brain;

/// <summary>The one search setting (the former "deep" preset); how long it may run is a deadline, not a preset.</summary>
public sealed class SearchConfig
{
    /// <summary>States kept per depth of the in-turn beam.</summary>
    public int Beam = 20;

    /// <summary>Most actions in one turn before ending it.</summary>
    public int Depth = 10;

    /// <summary>Turn plans that get a real resolution + playout.</summary>
    public int Candidates = 48;

    /// <summary>Best plans whose next turn is searched too (more follow while the deadline allows).</summary>
    public int Lookahead = 8;

    public int LookBeam = 10;
    public int LookDepth = 8;
    public int LookCandidates = 16;

    public bool Swaps = true;
    public bool Skills = true;

    /// <summary>Whether the next-turn lookahead also branches on exchanges.</summary>
    public bool LookSwaps = true;

    /// <summary>Exchanges per turn the in-turn beam may chain (each one is ~25 extra branches per node).</summary>
    public int MaxSwapsPerTurn = 3;
    public int MaxTurns = 40;

    /// <summary>Playout policies; a plan's value is its best playout, and each policy's own turn is always a candidate.</summary>
    public List<string> Policies = new() { "default", "mover", "defensive", "aggressive" };
}

/// <summary>One evaluated turn plan.</summary>
public sealed class PlanValue
{
    public string Prefix;
    public NodeInfo Node;
    public double Heuristic;
    public EvalResult Eval;
    public double Score = double.NegativeInfinity;
    public double LookScore = double.NegativeInfinity;
    public string LookPlan;
    public EvalResult LookEval;
    public bool Greedy;

    public double Best => Math.Max(Score, LookScore);
}

public sealed class Decision
{
    public string Plan;
    public PlanValue Best;
    public List<PlanValue> Ranked = new();
    public long Jobs;
    public double Seconds;
    public List<string> Log = new();

    /// <summary>State hash right after this plan's turn resolves, and the plan the lookahead found for that turn.</summary>
    public string NextHash => Best?.Eval?.Next?.Hash;

    public string NextPlan => Best?.LookScore >= Best?.Score ? Best?.LookPlan : null;
}

/// <summary>
/// Decision search over real game states (worker pool):
///   1. in-turn beam over action sequences, ranked by the game's own clash preview;
///   2. every visited node is a candidate "end the turn here"; the best K by preview get the real
///      resolution (damage, shields, destruction, enemy turn) plus a greedy playout to the end;
///   3. the best few by that score get their next turn searched as well (depth-2 lookahead);
///   4. plans are ranked lexicographically: win, then remaining HP, then fewer turns.
/// </summary>
public sealed class Search
{
    private readonly WorkerPool pool;
    private readonly SearchConfig cfg;
    private readonly Action<string> log;

    /// <summary>
    /// Progress for a user interface: stage key ("search", "evaluate", "lookahead"), fraction of the
    /// whole decision done (an estimate), and a count with its total where the stage has one.
    /// </summary>
    public Action<string, double, int, int> Stage;

    private void Report(string stage, double frac, int n = 0, int of = 0) => Stage?.Invoke(stage, Math.Clamp(frac, 0, 1), n, of);

    /// <summary>Optional plan for this turn to always consider (the previous decision's lookahead line).</summary>
    public string Hint;

    /// <summary>
    /// Time limit for one decision (UTC; <see cref="DateTime.MaxValue"/> = none: the full search runs).
    /// Past it the in-turn beam stops going deeper and no new lookahead starts (work already running
    /// finishes); with a limit, time left after the configured lookahead is spent on looking ahead
    /// from further candidates, best first. <see cref="Hurry"/> moves it to now from another thread.
    /// </summary>
    public DateTime Deadline
    {
        get => new(Volatile.Read(ref deadlineTicks), DateTimeKind.Utc);
        set => Volatile.Write(ref deadlineTicks, value.Ticks);
    }

    private long deadlineTicks = DateTime.MaxValue.Ticks;

    private bool Unlimited => Volatile.Read(ref deadlineTicks) == DateTime.MaxValue.Ticks;

    /// <summary>
    /// The deadline is a target to use up (a per-turn time budget): time left after the configured
    /// lookahead goes into looking ahead from more plans. Otherwise it is only a cap.
    /// </summary>
    public bool UseSpareTime;

    /// <summary>"Answer now": finish the step in progress and return the best plan found so far.</summary>
    public void Hurry() => Deadline = DateTime.UtcNow;

    /// <summary>
    /// "Drop it": the decision is no longer wanted (its game was cancelled). Work not yet handed to a
    /// worker is skipped and <see cref="Decide"/> returns as soon as the jobs in flight are back.
    /// </summary>
    public void Abort()
    {
        aborted = true;
        Hurry();
    }

    private volatile bool aborted;

    private bool Aborted() => aborted;

    private Task<List<Response>> RunAll(IReadOnlyList<Request> jobs) => pool.RunAll(jobs, Aborted);

    private bool Late => DateTime.UtcNow >= Deadline;

    public Search(WorkerPool pool, SearchConfig cfg, Action<string> log)
    {
        this.pool = pool;
        this.cfg = cfg;
        this.log = log ?? (_ => { });
    }

    public async Task<Decision> Decide(string rootPrefix = "")
    {
        var sw = Stopwatch.StartNew();
        long jobs0 = pool.Jobs;
        var d = new Decision();
        void Log(string m)
        {
            lock (d.Log)
            {
                d.Log.Add(m);
                log(m);
            }
        }

        Report("search", 0);
        List<PlanValue> cands = await TurnCandidates(rootPrefix, cfg.Beam, cfg.Depth, cfg.Candidates, Log, report: true);
        if (!string.IsNullOrEmpty(Hint))
        {
            // the plan the previous decision's lookahead was counting on for this turn
            string hp = Join(rootPrefix, Hint);
            if (!cands.Any(c => c.Prefix == hp))
            {
                Response st = (await RunAll(new[] { new Request { Op = "state", Prefix = hp } }))[0];
                if (st?.Ok == true && st.Node != null && st.Node.InTurn)
                {
                    cands.Add(new PlanValue { Prefix = hp, Node = st.Node, Heuristic = Heuristic(st.Node) });
                }
            }
        }
        if (aborted)
        {
            return d;
        }
        Report("evaluate", 0.4, 0, cands.Count);
        await Evaluate(cands);
        foreach (PlanValue c in cands)
        {
            c.Score = OutcomeScore(c.Eval);
        }
        Report("evaluate", 0.55, cands.Count, cands.Count);
        Log($"evaluated {cands.Count} plans ({sw.ElapsedMilliseconds}ms); best playout {Describe(cands.MaxBy(c => c.Score))}");

        NodeInfo rootNode = cands.FirstOrDefault(c => c.Prefix == rootPrefix)?.Node;
        var queue = cands.Where(c => c.Eval?.Ok == true && !c.Eval.Next.Over && !CannotImprove(c, rootNode))
            .OrderByDescending(c => c.Score).ThenByDescending(c => c.Heuristic).ToList();
        // a full round of lookahead costs about 1.5x the search so far: with less time left, look
        // ahead from fewer plans (at least the best one) instead of overrunning the deadline
        int first = cfg.Lookahead;
        if (!Unlimited)
        {
            double left = (Deadline - DateTime.UtcNow).TotalSeconds;
            first = left <= 0 ? 0 : Math.Clamp((int)(cfg.Lookahead * left / (1.5 * sw.Elapsed.TotalSeconds)), 1, cfg.Lookahead);
        }
        List<PlanValue> top = queue.Take(first).ToList();
        int looked = 0, lookTotal = top.Count;
        Report("lookahead", 0.55, 0, lookTotal);
        var lsw = Stopwatch.StartNew();
        await Task.WhenAll(top.Select(LookAhead));
        // anytime: while the deadline allows another round like the first one, look ahead from more plans
        double round = lsw.Elapsed.TotalSeconds;
        int done = top.Count;
        while (UseSpareTime && !Unlimited && done < queue.Count && round > 0)
        {
            // a partial round when a whole one no longer fits
            int n = (int)(top.Count * (Deadline - DateTime.UtcNow).TotalSeconds / round);
            if (n < 1)
            {
                break;
            }
            List<PlanValue> more = queue.Skip(done).Take(Math.Min(n, cfg.Lookahead)).ToList();
            done += more.Count;
            Interlocked.Add(ref lookTotal, more.Count);
            await Task.WhenAll(more.Select(LookAhead));
        }
        Log($"lookahead done: {done}/{queue.Count} plans ({sw.ElapsedMilliseconds}ms)");

        d.Ranked = cands.OrderByDescending(c => c.Best).ThenByDescending(c => c.Heuristic).ToList();
        d.Best = d.Ranked.FirstOrDefault();
        d.Plan = d.Best == null ? "" : Short(d.Best.Prefix, rootPrefix);
        d.Jobs = pool.Jobs - jobs0;
        d.Seconds = sw.Elapsed.TotalSeconds;
        Log($"decision: {d.Plan}  value {Fmt(d.Best?.Best ?? double.NegativeInfinity)}  ({d.Jobs} jobs, {d.Seconds:F1}s)");
        return d;

        // the lookahead searches are independent: they run together over the whole pool
        async Task LookAhead(PlanValue t)
        {
            try
            {
                await LookAheadFrom(t);
            }
            finally
            {
                int k = Interlocked.Increment(ref looked), of = Volatile.Read(ref lookTotal);
                Report("lookahead", 0.55 + 0.45 * k / Math.Max(1, of), k, of);
            }
        }

        async Task LookAheadFrom(PlanValue t)
        {
            string next = Join(t.Prefix, "end");
            List<PlanValue> sub = await TurnCandidates(next, cfg.LookBeam, cfg.LookDepth, cfg.LookCandidates, null, cfg.LookSwaps);
            if (Late)
            {
                return; // out of time (or hurried): the plan keeps its own playout value
            }
            await Evaluate(sub);
            PlanValue bestSub = null;
            foreach (PlanValue s in sub)
            {
                s.Score = OutcomeScore(s.Eval);
                if (bestSub == null || s.Score > bestSub.Score)
                {
                    bestSub = s;
                }
            }
            if (bestSub != null)
            {
                t.LookScore = bestSub.Score;
                t.LookPlan = bestSub.Prefix.Substring(next.Length).TrimStart(';');
                t.LookEval = bestSub.Eval;
            }
            Log($"  lookahead {Short(t.Prefix, rootPrefix)} : {Fmt(t.Score)} -> {Fmt(t.LookScore)} (next: {t.LookPlan})");
        }
    }

    /// <summary>Already wins next turn without losing HP or fuel: nothing a deeper look could improve.</summary>
    private static bool CannotImprove(PlanValue c, NodeInfo root)
    {
        if (root == null || c.Eval?.Rollout == null || !c.Eval.Rollout.Won)
        {
            return false;
        }
        return c.Eval.Rollout.PlayerHp >= root.PHp && c.Eval.Rollout.PlayerFuel >= root.Move && c.Eval.Rollout.MaxRound <= root.Round + 1;
    }

    private async Task Evaluate(List<PlanValue> plans)
    {
        var jobs = plans.Select(p => new Request { Op = "eval", Prefix = p.Prefix, Rollout = true, MaxTurns = cfg.MaxTurns, Policies = cfg.Policies }).ToList();
        List<Response> res = await RunAll(jobs);
        for (int i = 0; i < plans.Count; i++)
        {
            plans[i].Eval = res[i]?.Ok == true ? res[i].Evals?.FirstOrDefault() : new EvalResult { Ok = false, Error = res[i]?.Error };
        }
    }

    /// <summary>In-turn beam search from <paramref name="prefix"/>; returns the most promising places to end the turn.</summary>
    public async Task<List<PlanValue>> TurnCandidates(string prefix, int beam, int depth, int keep, Action<string> logf, bool? swaps = null, bool report = false)
    {
        bool withSwaps = swaps ?? cfg.Swaps;
        var tsw = System.Diagnostics.Stopwatch.StartNew();
        Response rootResp = (await RunAll(new[] { ExpandReq(prefix, withSwaps) }))[0];
        logf?.Invoke($"  root expand {rootResp?.Children?.Count} children in {rootResp?.Ms:F0}ms");
        if (rootResp == null || !rootResp.Ok)
        {
            logf?.Invoke("expand failed: " + rootResp?.Error);
            return new List<PlanValue>();
        }
        NodeInfo root = rootResp.Node;
        var seen = new HashSet<string> { root.Hash };
        var all = new List<PlanValue> { new PlanValue { Prefix = prefix, Node = root, Heuristic = Heuristic(root) } };
        var layer = new List<(string parent, Response resp)> { (prefix, rootResp) };
        for (int level = 1; level <= depth; level++)
        {
            var kids = new List<PlanValue>();
            foreach (var (parent, resp) in layer)
            {
                if (resp?.Ok != true || resp.Children == null)
                {
                    continue;
                }
                foreach (NodeInfo c in resp.Children)
                {
                    if (!c.Ok || c.Hash == null || !seen.Add(c.Hash))
                    {
                        continue;
                    }
                    if (!c.Over && (!c.InTurn || c.Round != root.Round))
                    {
                        continue; // the action ended the turn by itself (rare skill effect)
                    }
                    kids.Add(new PlanValue { Prefix = Join(parent, c.Tok), Node = c, Heuristic = Heuristic(c) });
                }
            }
            if (kids.Count == 0)
            {
                break;
            }
            all.AddRange(kids);
            List<PlanValue> frontier = Diverse(kids.Where(k => !k.Node.Over), beam);
            logf?.Invoke($"  depth {level}: {kids.Count} new states, expanding {frontier.Count} (best h {Fmt(kids.Max(k => k.Heuristic))}) t={tsw.ElapsedMilliseconds}ms");
            if (report)
            {
                Report("search", 0.4 * level / depth, all.Count);
            }
            if (frontier.Count == 0 || level == depth || level >= 2 && Late || aborted)
            {
                break;
            }
            var lsw = System.Diagnostics.Stopwatch.StartNew();
            List<Response> next = await RunAll(frontier.Select(f => ExpandReq(f.Prefix, withSwaps)).ToList());
            logf?.Invoke($"    expand x{frontier.Count}: wall {lsw.ElapsedMilliseconds}ms, worker avg {next.Average(r => r?.Ms ?? 0):F0}ms max {next.Max(r => r?.Ms ?? 0):F0}ms, children {next.Sum(r => r?.Children?.Count ?? 0)}");
            layer = frontier.Select((f, i) => (f.Prefix, next[i])).ToList();
        }
        // A pending battle choice must be answered before the turn can end.
        var endable = all.Where(p => !p.Node.Choice).ToList();
        var picked = Diverse(endable, keep);
        if (!picked.Any(p => p.Prefix == prefix) && !root.Choice)
        {
            picked.Add(all[0]); // always consider doing nothing more
        }
        // Plans are scored by greedy playouts, so the greedy turn itself must always be available:
        // then the value a plan was chosen for can actually be realised next turn, and the search
        // never does worse than the greedy policy (rollout policy improvement).
        if (!root.Choice && !aborted)
        {
            Response g = (await RunAll(new[] { new Request { Op = "greedy", Prefix = prefix, Policies = cfg.Policies } }))[0];
            List<List<string>> plans = g?.Ok == true ? g.Plans ?? new List<List<string>>() : new List<List<string>>();
            for (int i = 0; i < plans.Count; i++)
            {
                List<string> plan = plans[i];
                NodeInfo gnode = g.Children != null && i < g.Children.Count ? g.Children[i] : g.Node;
                string gp = plan.Count == 0 ? prefix : prefix + (prefix.Length > 0 ? ";" : "") + string.Join(";", plan);
                PlanValue existing = picked.FirstOrDefault(p => p.Prefix == gp);
                if (existing != null)
                {
                    existing.Greedy = true;
                    continue;
                }
                picked.Add(new PlanValue { Prefix = gp, Node = gnode, Heuristic = Heuristic(gnode), Greedy = true });
            }
        }
        return picked;
    }

    private Request ExpandReq(string prefix, bool swaps) => new() { Op = "expand", Prefix = prefix, Swaps = swaps && cfg.Swaps, MaxSwaps = cfg.MaxSwapsPerTurn, Skills = cfg.Skills };

    /// <summary>Top <paramref name="n"/> by heuristic, at most two per identical clash preview.</summary>
    private static List<PlanValue> Diverse(IEnumerable<PlanValue> items, int n)
    {
        var result = new List<PlanValue>();
        var perSignature = new Dictionary<string, int>();
        foreach (PlanValue p in items.OrderByDescending(p => p.Heuristic).ThenBy(p => p.Prefix.Length))
        {
            string sig = string.Join("|", p.Node.Lines.Select(l => $"{l[1]}:{l[2]}")) + $"#{p.Node.Hand}#{p.Node.Exchange}";
            perSignature.TryGetValue(sig, out int k);
            if (k >= 2)
            {
                continue;
            }
            perSignature[sig] = k + 1;
            result.Add(p);
            if (result.Count >= n)
            {
                break;
            }
        }
        return result;
    }

    /// <summary>
    /// Ranks partial turns by the game's own clash preview: damage the turn would deal and take,
    /// lethal either way, breaking the enemy (it then skips a turn), and resources kept.
    /// </summary>
    public static double Heuristic(NodeInfo n)
    {
        if (n.Over)
        {
            return n.Won ? 1e8 + n.PHp * 1000 : -1e8;
        }
        int eLife = n.EHp + n.ESh;
        int pLife = n.PHp + n.PSh;
        int dealt = Math.Min(n.Dealt, eLife);
        int taken = Math.Min(n.Taken, pLife);
        double h = 1000.0 * dealt - 1200.0 * taken;
        if (eLife > 0 && n.Dealt >= eLife)
        {
            h += 1e6;
        }
        if (n.Taken >= pLife)
        {
            h -= 1e6;
        }
        if (n.EBreak > 0 && n.Wins >= n.EBreak)
        {
            h += 6000;
        }
        h += 40.0 * n.HandPower + 400.0 * n.Exchange + 250.0 * n.Move;
        return h;
    }

    /// <summary>
    /// Lexicographic battle value: win ≫ remaining HP ≫ remaining fuel ≫ fewer turns; losses by
    /// damage dealt. Fuel (燃料, shipData.currentMove) is not refilled between battles — like HP it
    /// carries over — so a move is only worth making when it saves HP (or the battle).
    /// </summary>
    public static double OutcomeScore(EvalResult e)
    {
        if (e == null || !e.Ok || e.Next == null)
        {
            return double.NegativeInfinity;
        }
        NodeInfo n = e.Next;
        if (n.Over)
        {
            return n.Won ? Won(n.PHp, n.Move, n.Round) : Lost(n.EMaxHp - n.EHp, n.Round);
        }
        BattleOutcomeView o = BattleOutcomeView.From(e.Rollout);
        if (o == null)
        {
            return -5e8;
        }
        if (o.Won)
        {
            return Won(o.PlayerHp, o.Fuel, o.Round);
        }
        if (o.Ended)
        {
            return Lost(n.EMaxHp - o.EnemyHp, o.Round);
        }
        return -5e8 + (n.EMaxHp - o.EnemyHp) * 1000.0 + o.PlayerHp * 10.0;
    }

    /// <summary>A win: 1e9 + hp·1e4 + fuel·100 + (99 − round); fuel and round are clamped to 0..49 and 0..99.</summary>
    public static double Won(int hp, int fuel, int round) =>
        1e9 + Math.Max(0, hp) * 1e4 + Math.Clamp(fuel, 0, 49) * 100.0 + (99 - Math.Clamp(round, 0, 99));

    public static double Lost(int damage, int round) => -1e9 + damage * 1e3 + round;

    /// <summary>The parts of a <see cref="Won"/> value (false when <paramref name="v"/> is not a win).</summary>
    public static bool WinParts(double v, out int hp, out int fuel, out int round)
    {
        hp = fuel = round = 0;
        if (!(v >= 1e9 - 1e6) || double.IsInfinity(v))
        {
            return false;
        }
        long r = (long)Math.Round(v - 1e9);
        hp = (int)(r / 10000);
        fuel = (int)(r % 10000 / 100);
        round = 99 - (int)(r % 100);
        return true;
    }

    /// <summary>
    /// The most fuel the player can still hold when the battle ends, from this decision point on.
    /// Fuel never exceeds its maximum; after round 1 it only comes back through "+1 fuel after a
    /// battle that ended at 0" (TS_NoMoveAddMove) — the round-1 exchange event and the round-2
    /// treasure have already happened. (A custom game's post-battle repair is not bounded by this.)
    /// </summary>
    public static int FuelCap(NodeInfo at) =>
        at.Round <= 1 ? Math.Max(at.Move, at.PMaxMove) : Math.Max(at.Move, Math.Min(1, at.PMaxMove));

    public static string Fmt(double v)
    {
        if (double.IsNegativeInfinity(v))
        {
            return "invalid";
        }
        if (WinParts(v, out int hp, out int fuel, out int round))
        {
            return $"WIN hp={hp} fuel={fuel} round={round}";
        }
        if (v <= -1e9 + 1e7)
        {
            return $"LOSS dmg={(int)((v + 1e9) / 1e3)}";
        }
        if (v <= -5e8 + 1e8 && v >= -5e8 - 1e8)
        {
            return "UNFINISHED";
        }
        return v.ToString("F0");
    }

    private static string Describe(PlanValue p) => p == null ? "-" : $"{Fmt(p.Score)} [{p.Prefix}]";

    public static string Join(string prefix, string token) =>
        string.IsNullOrEmpty(prefix) ? token : prefix + ";" + token;

    public static string Short(string prefix, string root) =>
        string.IsNullOrEmpty(root) ? prefix : prefix.Length > root.Length ? prefix.Substring(root.Length).TrimStart(';') : "";
}

/// <summary>Uniform view over a playout result.</summary>
public sealed class BattleOutcomeView
{
    public bool Won;
    public bool Ended;
    public int PlayerHp;
    public int Fuel;
    public int EnemyHp;
    public int Round;

    public static BattleOutcomeView From(Lonestar.Engine.BattleOutcome o) =>
        o == null ? null : new BattleOutcomeView { Won = o.Won, Ended = o.Ended, PlayerHp = o.PlayerHp, Fuel = o.PlayerFuel, EnemyHp = o.EnemyHp, Round = o.MaxRound };
}
