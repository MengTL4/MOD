using System.Diagnostics;

namespace Brain;

/// <summary>A pre-battle layout and how it plays out.</summary>
public sealed class LoadoutValue
{
    public string Layout;
    public double Greedy = double.NegativeInfinity;
    public double Full = double.NegativeInfinity;
    public string FullSummary;
    /// <summary>How the full game ended (null until played in full).</summary>
    public Outcome FullOutcome;
    public string Error;
    public int MaxHp;

    /// <summary>The most fuel the game can end with (<see cref="Search.FuelCap"/> at the battle start).</summary>
    public int FuelCap;
    public List<string> Turns = new();

    /// <summary>State hash at the start of the battle (set when a full game starts).</summary>
    public string StartHash;

    public double Best => Math.Max(Greedy, Full);
}

/// <summary>
/// Pre-battle loadout optimisation against the known upcoming enemy: which owned units go on the
/// ship and on which cell (Σ EquipLimit ≤ shipLoadLimit, one unit per cell). Every candidate is
/// played in the real engine — first cheaply (greedy playout from the battle start) during a
/// steepest-ascent local search over move / swap / bring-in / bench moves, then the best few are
/// played turn by turn with the full battle search, which decides. Layouts that differ only by
/// which of two identical units sits where are the same layout (<see cref="Canon"/>). Everything
/// runs against one time budget (<see cref="Seconds"/>); the full searches run side by side on
/// disjoint parts of the worker pool.
/// </summary>
public sealed class LoadoutSearch
{
    private readonly WorkerPool pool;
    private readonly SearchConfig cfg;
    private readonly Action<string> log;
    /// <summary>Canonical layout → its value (the value keeps the first concrete layout seen).</summary>
    private readonly Dictionary<string, LoadoutValue> seen = new();
    private LoadoutInfo info;

    /// <summary>
    /// Time cap (seconds). The search stops by itself once it converges: screening after
    /// <see cref="Patience"/> kicks in a row found nothing better, the full search once every layout
    /// tied for the best screened value (and the current one) has been played, or as soon as one
    /// wins at full HP in round 1 (nothing can beat that).
    /// </summary>
    public double Seconds = 600;

    /// <summary>Most of the cap the screening may take.</summary>
    public double ScreenShare = 0.4;

    /// <summary>Kicks in a row without a better screened value before the screening stops.</summary>
    public int Patience = 8;

    /// <summary>Layouts played in full even when fewer are tied for the best screened value.</summary>
    public int MinFinalists = 3;

    /// <summary>Full games in a row (the current layout aside) that did not beat the best full result before the full search stops.</summary>
    public int FullPatience = 4;

    /// <summary>
    /// Once the best full game matches the best screened value (the full search found nothing the
    /// cheap playouts had not), this many further games (the current layout aside) that do not beat
    /// it are enough to stop. Games still running then are cancelled; the current layout's game
    /// (its result is shown next to the recommendation) finishes with cut-short decisions.
    /// </summary>
    public int TieConfirmations = 2;

    /// <summary>Cancel full games that cannot beat the best finished one (see <see cref="RunningGame"/>).</summary>
    public bool CancelHopeless = true;

    /// <summary>Diagnostics: BRAIN_FULLSTOP=old plays the full games with the pre-2026-10-23 stopping rule (for comparisons).</summary>
    private static readonly bool OldStop = Environment.GetEnvironmentVariable("BRAIN_FULLSTOP") == "old";

    /// <summary>
    /// Every clean decision of the full games (made with time to spare, not cut short), by the
    /// state hash it was made at: the battle that follows starts from one of these states when
    /// the player applies the recommendation, and the service answers from here at once.
    /// </summary>
    public Dictionary<string, (NodeInfo Root, Decision Decision)> Decisions { get; } = new();

    /// <summary>A full game in progress (so that a finished one can cancel it).</summary>
    public sealed class RunningGame
    {
        public LoadoutValue Value;
        public bool IsCurrent;
        public volatile bool Cancelled;
        /// <summary>Cancelled because it cannot beat the best finished game: as good as a finished game that did not.</summary>
        public volatile bool Hopeless;
        public volatile Search Search;
        /// <summary>The state at the start of the turn being decided.</summary>
        public volatile NodeInfo Node;
        /// <summary>Whether a game at this state can no longer beat the best finished one (checked every turn).</summary>
        public Func<NodeInfo, bool> CannotBeatBest;

        /// <summary>Only its result is still wanted: the rest of the game is played with cut-short decisions.</summary>
        public volatile bool Rushed;

        public void Cancel()
        {
            Cancelled = true;
            Search?.Abort();
        }

        public void Rush()
        {
            Rushed = true;
            Search?.Hurry();
        }
    }

    /// <summary>Progress for a user interface: "screen" (layouts tried so far) or "full" (games played of planned), fraction done (estimate).</summary>
    public Action<string, double, int, int> Stage;

    private void Report(string stage, double frac, int n = 0, int of = 0) => Stage?.Invoke(stage, Math.Clamp(frac, 0, 1), n, of);

    private readonly Stopwatch runClock = new();

    private volatile bool hurried;
    private readonly List<Search> playing = new();

    /// <summary>"Answer now": no new work starts, running games finish with cut-short decisions.</summary>
    public void Hurry()
    {
        hurried = true;
        lock (playing)
        {
            foreach (Search s in playing)
            {
                s.Hurry();
            }
        }
    }

    /// <summary>Workers per full-search game; the pool plays Count / this many games side by side.</summary>
    public int WorkersPerGame = 4;

    public int MaxFinalists = 64;
    public int MaxRounds = 10;

    public LoadoutSearch(WorkerPool pool, SearchConfig cfg, Action<string> log)
    {
        this.pool = pool;
        this.cfg = cfg;
        this.log = log ?? (_ => { });
    }

    public LoadoutInfo Info => info;

    /// <summary>The study ended by itself (converged, or nothing could beat its best): not hurried, not stopped by the time cap.</summary>
    public bool Complete { get; private set; }

    private string prepared;

    /// <summary>Loads the pre-battle save into every worker and reads the dock (<see cref="Info"/>).</summary>
    public async Task<LoadoutInfo> Prepare(string recording, int enemyId)
    {
        List<Response> prep = await pool.Broadcast(() => new Request { Op = "prepare", Recording = recording, EnemyId = enemyId });
        Response bad = prep.FirstOrDefault(r => !r.Ok);
        if (bad != null)
        {
            throw new InvalidOperationException("prepare failed: " + bad.Error);
        }
        info = prep[0].Loadout;
        kinds = info.Units.GroupBy(u => SigOf(u.Index))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(u => u.OnShip).ThenBy(u => u.Index).Select(u => u.Index).ToList());
        prepared = recording + "|" + enemyId;
        return info;
    }

    /// <summary>State hash at the start of the battle with <paramref name="layout"/> (one worker).</summary>
    public async Task<string> StartHash(string layout)
    {
        Response r = await pool.One(new Request { Op = "layout", Layout = layout, Rollout = false });
        return r?.Ok == true ? r.Node?.Hash : null;
    }

    public async Task<List<LoadoutValue>> Run(string recording, int enemyId)
    {
        var sw = Stopwatch.StartNew();
        runClock.Restart();
        Report("screen", 0);
        if (prepared != recording + "|" + enemyId)
        {
            await Prepare(recording, enemyId);
        }
        log($"loadout: {info.Units.Count} units ({kinds.Count} kinds), {info.Cells.Count(c => c[2] != 0)} usable cells, load limit {info.LoadLimit}; current {Describe(info.Current)}");

        var starts = new List<string> { info.Current };
        starts.AddRange(Seeds());
        await Greedy(starts);
        if (Value(info.Current).Error is { } err)
        {
            // the simulator cannot play this battle: every other layout would be scored the same way
            throw new InvalidOperationException("模拟器打不了这场战斗: " + err);
        }
        log($"  current layout plays {Search.Fmt(Value(info.Current).Greedy)} (greedy)");

        // 1) steepest ascent from the best few starting points, 2) then kicks: a random 2-3 step
        // change of one of the best layouts climbed again (finds improvements that need two changes
        // at once), until the screening share of the time budget is used
        DateTime screenEnd = DateTime.UtcNow.AddSeconds(Math.Max(5, Seconds * ScreenShare - sw.Elapsed.TotalSeconds));
        int startNo = 0;
        foreach (string s0 in seen.Values.OrderByDescending(v => v.Greedy).Take(3).Select(v => v.Layout).ToList())
        {
            await Climb(s0, $"start {++startNo}/3", screenEnd, sw);
        }
        var rng = new Random(20261022);
        int kicks = 0, sinceBetter = 0;
        double bestScreened = seen.Values.Max(v => v.Greedy);
        while (DateTime.UtcNow < screenEnd && !hurried && sinceBetter < Patience)
        {
            List<LoadoutValue> elite = seen.Values.Where(v => v.Error == null).OrderByDescending(v => v.Greedy).Take(6).ToList();
            string k = elite[kicks % elite.Count].Layout;
            for (int step = 2 + rng.Next(2); step > 0; step--)
            {
                List<string> n = Neighbours(k);
                if (n.Count == 0)
                {
                    break;
                }
                k = n[rng.Next(n.Count)];
            }
            kicks++;
            await Greedy(new[] { k });
            await Climb(k, $"kick {kicks}", screenEnd, sw);
            double now = seen.Values.Max(v => v.Greedy);
            sinceBetter = now > bestScreened ? 0 : sinceBetter + 1;
            bestScreened = Math.Max(bestScreened, now);
        }
        string why = hurried ? "stopped early" : sinceBetter >= Patience ? "converged" : "time share used";
        bool screenedAll = sinceBetter >= Patience;
        log($"screening ({why}): {seen.Count} layouts, {kicks} kicks in {sw.Elapsed.TotalSeconds:F0}s; best {Search.Fmt(bestScreened)}");

        // 3) the full battle search decides: best screened layouts first (the current layout right
        // after the best), several games side by side, new ones only while they fit in the budget
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, Seconds - sw.Elapsed.TotalSeconds));
        // every layout tied for the best screened value (the full search may still tell them apart,
        // e.g. one wins a round earlier), at least the best few
        List<LoadoutValue> ranked = seen.Values.Where(v => v.Error == null).OrderByDescending(v => v.Greedy).ToList();
        var order = ranked.Where(v => v.Greedy >= bestScreened).Take(MaxFinalists).ToList();
        foreach (LoadoutValue v in ranked)
        {
            if (order.Count >= MinFinalists)
            {
                break;
            }
            if (!order.Contains(v))
            {
                order.Add(v);
            }
        }
        order.Remove(Value(info.Current));
        order.Insert(Math.Min(1, order.Count), Value(info.Current));
        // a game on more workers finishes sooner at about the same total throughput: with little
        // time left play fewer games side by side (the best and the current one must fit), with
        // plenty of time more (about one game per minute of remaining budget)
        double left = (deadline - DateTime.UtcNow).TotalSeconds;
        int groups = Math.Clamp((int)(left / 60), 2, Math.Max(2, pool.Count / WorkersPerGame));
        groups = Math.Clamp(groups, 1, Math.Min(order.Count, pool.Count));
        List<WorkerPool> parts = groups > 1 ? pool.Split(groups) : new List<WorkerPool> { pool };
        Report("full", 0.5, 0, order.Count);
        log($"full search: {order.Count} layouts, {groups} games at a time, {parts[0].Count}+ workers each, at most {(deadline - DateTime.UtcNow).TotalSeconds:F0}s");
        if (OldStop)
        {
            TieConfirmations = int.MaxValue;
            CancelHopeless = false;
        }
        int next = 0, finished = 0, cancelled = 0;
        double longest = 0, bestFull = double.NegativeInfinity;
        bool perfect = false, converged = false;
        int sinceBetterFull = 0;
        var running = new List<RunningGame>();
        // even winning this very round at full HP, with as much fuel as can still be held, would not
        // beat the best finished game (HP can be healed, so only the maximum bounds it): ties
        // included, they cannot change the ranking
        bool CannotBeat(NodeInfo at) =>
            CancelHopeless && at != null && !at.Over && Volatile.Read(ref bestFull) >= 1e9 - 1e6
            && Search.Won(at.PMaxHp, Search.FuelCap(at), at.Round) <= Volatile.Read(ref bestFull);
        await Task.WhenAll(parts.Select(async part =>
        {
            while (true)
            {
                RunningGame g;
                lock (order)
                {
                    // the best layout and the current one always; others while one more game fits
                    bool fits = next < Math.Max(2, groups) || longest > 0 && DateTime.UtcNow.AddSeconds(longest) <= deadline;
                    if (next >= order.Count || !fits || hurried || perfect || converged)
                    {
                        return;
                    }
                    LoadoutValue v = order[next++];
                    g = new RunningGame { Value = v, IsCurrent = v.Layout == info.Current, CannotBeatBest = CannotBeat };
                    running.Add(g);
                }
                var gsw = Stopwatch.StartNew();
                await PlayFull(g.Value, part, deadline, g);
                LoadoutValue f = g.Value;
                lock (order)
                {
                    running.Remove(g);
                    if (g.Cancelled)
                    {
                        cancelled++;
                        log($"  full play {(g.Hopeless ? "stopped (cannot beat the best)" : "cancelled")}: {Describe(f.Layout)} ({sw.Elapsed.TotalSeconds:F0}s)");
                        if (!g.Hopeless || g.IsCurrent)
                        {
                            continue;
                        }
                        sinceBetterFull++; // provably no better: counts like a finished game that was not
                    }
                    else
                    {
                        longest = Math.Max(longest, gsw.Elapsed.TotalSeconds);
                        finished++;
                        perfect |= f.MaxHp > 0 && f.Full >= Search.Won(f.MaxHp, f.FuelCap, 1);
                        if (!g.IsCurrent)
                        {
                            sinceBetterFull = f.Full > bestFull ? 0 : sinceBetterFull + 1;
                            bestFull = Math.Max(bestFull, f.Full);
                        }
                    }
                    converged |= sinceBetterFull >= FullPatience
                                 || bestFull >= bestScreened && sinceBetterFull >= TieConfirmations;
                    // what can no longer matter: everything once converged (the current layout's
                    // game aside), and games that cannot beat the new best (they also check that
                    // themselves at every turn)
                    foreach (RunningGame r in running)
                    {
                        if (OldStop || r.Cancelled)
                        {
                            continue;
                        }
                        if (r.IsCurrent)
                        {
                            if ((converged || perfect) && !r.Rushed)
                            {
                                r.Rush();
                                log($"  the current layout's game finishes with quick decisions ({sw.Elapsed.TotalSeconds:F0}s)");
                            }
                            continue;
                        }
                        if (CannotBeat(r.Node))
                        {
                            r.Hopeless = true;
                            r.Cancel();
                        }
                        else if (converged || perfect)
                        {
                            r.Cancel();
                        }
                    }
                }
                if (g.Cancelled)
                {
                    continue;
                }
                Report("full", 0.5 + 0.5 * finished / order.Count, finished, order.Count);
                log($"  full play {finished}/{order.Count}: {Search.Fmt(f.Full)}{(g.IsCurrent ? " [current]" : "")}  {Describe(f.Layout)} ({sw.Elapsed.TotalSeconds:F0}s)");
            }
        }));
        string end = (perfect ? " (one wins at full HP in round 1)" : hurried ? " (stopped early)" : converged ? " (converged)" : next < order.Count ? " (time cap)" : "")
                     + (cancelled > 0 ? $", {cancelled} cancelled" : "");
        log($"done: {finished} layouts played in full{end}, {seen.Count} screened, {sw.Elapsed.TotalSeconds:F0}s");
        Complete = !hurried && (perfect || screenedAll && (converged || next >= order.Count));
        // a greedy playout is a real playthrough too: a layout is worth the best result shown for it
        return seen.Values.OrderByDescending(v => v.Best).ThenByDescending(v => v.Full).ToList();
    }

    /// <summary>Steepest ascent over single changes from <paramref name="from"/> (greedy playout values).</summary>
    private async Task Climb(string from, string label, DateTime until, Stopwatch sw)
    {
        string cur = from;
        for (int round = 0; round < MaxRounds && DateTime.UtcNow < until && !hurried; round++)
        {
            List<string> nbrs = Neighbours(cur);
            if (nbrs.Count == 0)
            {
                break;
            }
            await Greedy(nbrs);
            string best = nbrs.OrderByDescending(n => Value(n).Greedy).First();
            if (Value(best).Greedy <= Value(cur).Greedy)
            {
                break;
            }
            cur = best;
            log($"  {label} climb {round + 1}: {Search.Fmt(Value(cur).Greedy)}  ({seen.Count} layouts, {sw.Elapsed.TotalSeconds:F0}s)");
        }
    }

    private LoadoutValue Value(string layout) => seen[Canon(layout)];

    /// <summary>
    /// Identical units (same <see cref="LoadoutUnit.Sig"/>) are interchangeable, so a layout is
    /// identified by which kind of unit sits on each cell: members of a kind are assigned to its
    /// cells in a fixed order.
    /// </summary>
    public string Canon(string layoutText)
    {
        var next = new Dictionary<string, int>();
        var result = new List<(int X, int Y, int Unit)>();
        foreach (var e in Worker.ParseLayout(layoutText).OrderBy(e => e.Y).ThenBy(e => e.X))
        {
            string sig = SigOf(e.Unit);
            next.TryGetValue(sig, out int k);
            next[sig] = k + 1;
            result.Add((e.X, e.Y, kinds[sig][k]));
        }
        return Key(result);
    }

    private Dictionary<string, List<int>> kinds;

    /// <summary>
    /// A layout as which kind of unit sits on each cell: unlike unit indices this means the same
    /// in another save of the same dock (e.g. after the units were moved around).
    /// </summary>
    public string SigKey(string layoutText) =>
        string.Join("\n", Worker.ParseLayout(layoutText).OrderBy(e => e.Y).ThenBy(e => e.X).Select(e => $"{e.X}\t{e.Y}\t{SigOf(e.Unit)}"));

    /// <summary>The layout a <see cref="SigKey"/> describes, made of this save's units (null when some kind is missing).</summary>
    public string FromSigKey(string key)
    {
        var next = new Dictionary<string, int>();
        var result = new List<(int X, int Y, int Unit)>();
        foreach (string line in key.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = line.Split('\t');
            if (f.Length != 3 || !kinds.TryGetValue(f[2], out List<int> members))
            {
                return null;
            }
            next.TryGetValue(f[2], out int k);
            if (k >= members.Count)
            {
                return null;
            }
            next[f[2]] = k + 1;
            result.Add((int.Parse(f[0]), int.Parse(f[1]), members[k]));
        }
        return Tidy(Worker.FormatLayout(result));
    }

    /// <summary>What layouts can be made of: the units (by kind, wherever they are now), the cells and the load limit.</summary>
    public string SetupKey() =>
        string.Join("\n", info.Units.Select(u => SigOf(u.Index)).OrderBy(x => x, StringComparer.Ordinal))
        + "\ncells " + string.Join(";", info.Cells.Select(c => string.Join(",", c)))
        + "\nlimit " + info.LoadLimit;

    private string SigOf(int unit) => info.Units[unit].Sig ?? "#" + unit;

    /// <summary>
    /// The same layout with identical units chosen to move as little as possible from where they
    /// are now (a unit already on a cell its kind ends up on stays there).
    /// </summary>
    public string Tidy(string layoutText)
    {
        List<(int X, int Y, int Unit)> layout = Worker.ParseLayout(layoutText);
        var result = new List<(int X, int Y, int Unit)>();
        foreach (var group in layout.GroupBy(e => SigOf(e.Unit)))
        {
            var free = kinds[group.Key].ToList();
            var open = new List<(int X, int Y)>();
            foreach (var e in group)
            {
                LoadoutUnit stay = info.Units.FirstOrDefault(u => free.Contains(u.Index) && u.OnShip && u.X == e.X && u.Y == e.Y);
                if (stay != null)
                {
                    result.Add((e.X, e.Y, stay.Index));
                    free.Remove(stay.Index);
                }
                else
                {
                    open.Add((e.X, e.Y));
                }
            }
            // prefer units already on the ship (a move instead of a swap-in), then the ones the layout named
            var order = free.Where(u => info.Units[u].OnShip)
                .Concat(group.Select(e => e.Unit).Where(free.Contains))
                .Concat(free)
                .Distinct().ToList();
            for (int i = 0; i < open.Count; i++)
            {
                result.Add((open[i].X, open[i].Y, order[i]));
            }
        }
        return Key(result);
    }

    private async Task Greedy(IEnumerable<string> layouts)
    {
        var todo = new List<string>();
        var queued = new HashSet<string>();
        foreach (string l in layouts)
        {
            string c = Canon(l);
            if (!seen.ContainsKey(c) && queued.Add(c))
            {
                todo.Add(l);
            }
        }
        if (todo.Count == 0)
        {
            return;
        }
        List<Response> res = await pool.RunAll(todo.Select(l => new Request { Op = "layout", Layout = l, Rollout = true, MaxTurns = cfg.MaxTurns }).ToList());
        for (int i = 0; i < todo.Count; i++)
        {
            var v = new LoadoutValue { Layout = todo[i] };
            if (res[i]?.Ok == true && res[i].Evals?.Count > 0)
            {
                v.Greedy = Search.OutcomeScore(res[i].Evals[0]);
            }
            else
            {
                v.Error = res[i]?.Error ?? "no reply";
            }
            seen[Canon(todo[i])] = v;
        }
        // the screening runs until it converges or its share of the cap is used: report the time share
        Report("screen", 0.5 * Math.Min(1, runClock.Elapsed.TotalSeconds / Math.Max(1, Seconds * ScreenShare)), seen.Count);
    }

    /// <summary>Plays the battle with the full search from the start with this layout.</summary>
    public async Task PlayFull(LoadoutValue v, WorkerPool pool, DateTime deadline, RunningGame game = null)
    {
        List<Response> init = await pool.Broadcast(() => new Request { Op = "layout", Layout = v.Layout, Rollout = false });
        if (init.Any(r => !r.Ok))
        {
            v.Full = double.NegativeInfinity;
            v.FullSummary = init.First(r => !r.Ok).Error;
            v.FullOutcome = new Outcome { Kind = "none" };
            return;
        }
        NodeInfo node = init[0].Node;
        v.MaxHp = node.PMaxHp;
        v.FuelCap = Search.FuelCap(node);
        v.StartHash = node.Hash;
        // past the deadline each decision is cut short (no lookahead), so a started game still ends soon
        var search = new Search(pool, cfg, null) { Deadline = hurried ? DateTime.UtcNow : deadline };
        lock (playing)
        {
            playing.Add(search);
        }
        if (game != null)
        {
            game.Search = search;
            // cancelled or rushed before the search existed
            if (game.Cancelled)
            {
                search.Abort();
            }
            else if (game.Rushed)
            {
                search.Hurry();
            }
        }
        bool cutShort = false;
        string expectHash = null, hint = null;
        for (int t = 0; t < cfg.MaxTurns && !node.Over; t++)
        {
            if (game != null)
            {
                game.Node = node;
                if (!game.IsCurrent && !game.Cancelled && game.CannotBeatBest?.Invoke(node) == true)
                {
                    game.Hopeless = true;
                    game.Cancelled = true;
                }
                if (game.Cancelled)
                {
                    break;
                }
            }
            search.Hint = node.Hash == expectHash ? hint : null;
            Decision d = await search.Decide();
            if (game?.Cancelled == true)
            {
                break;
            }
            // past its deadline (the overall one, "answer now", or rushed) the decision was cut short
            bool clean = !hurried && DateTime.UtcNow < search.Deadline;
            cutShort |= !clean;
            if (clean && node.Hash != null)
            {
                lock (Decisions)
                {
                    Decisions[node.Hash] = (node, d);
                }
            }
            expectHash = d.NextHash;
            hint = d.NextPlan;
            string tokens = Search.Join(d.Plan, "end");
            List<Response> adv = await pool.Broadcast(() => new Request { Op = "advance", Tokens = tokens });
            if (adv.Any(r => !r.Ok))
            {
                break;
            }
            v.Turns.Add(tokens);
            node = adv[0].Node;
        }
        if (game?.Cancelled == true)
        {
            // not played to the end: the layout keeps its screening value
            v.Full = double.NegativeInfinity;
            v.FullSummary = null;
            v.FullOutcome = null;
            v.Turns.Clear();
            lock (playing)
            {
                playing.Remove(search);
            }
            return;
        }
        v.Full = !node.Over ? -5e8 + (node.EMaxHp - node.EHp) * 1000.0
            : node.Won ? Search.Won(node.PHp, node.Move, node.Round)
            : Search.Lost(node.EMaxHp - node.EHp, node.Round);
        // damage dealt, not enemy HP left: a multi-phase boss shows 0 HP between phases
        v.FullSummary = node.Over ? (node.Won ? $"胜利 第{node.Round}回合 剩{node.PHp}HP {node.Move}燃料" : $"失败 第{node.Round}回合 共造成{node.EMaxHp - node.EHp}伤害") : "未分胜负";
        v.FullOutcome = new Outcome
        {
            Kind = !node.Over ? "open" : node.Won ? "win" : "loss",
            Round = node.Round,
            Hp = node.Won ? node.PHp : 0,
            Fuel = node.Won ? node.Move : 0,
            Damage = node.EMaxHp - node.EHp,
            // a win in round 1 at full HP cannot be bettered, however the game was played
            AtLeast = cutShort && v.Full < Search.Won(v.MaxHp, v.FuelCap, 1),
        };
        lock (playing)
        {
            playing.Remove(search);
        }
    }

    // ---- candidate generation --------------------------------------------------------------------

    private List<(int X, int Y)> UsableCells() => info.Cells.Where(c => c[2] != 0).Select(c => (c[0], c[1])).ToList();

    private int Load(IEnumerable<(int X, int Y, int Unit)> layout) => layout.Sum(e => info.Units[e.Unit].EquipLimit);

    private string Key(IEnumerable<(int X, int Y, int Unit)> layout) => Worker.FormatLayout(layout);

    /// <summary>Single-step changes: move to an empty cell, swap two cells, bring a bag unit in, swap with a bag unit, bench.</summary>
    public List<string> Neighbours(string layoutText)
    {
        List<(int X, int Y, int Unit)> layout = Worker.ParseLayout(layoutText);
        var cells = UsableCells();
        var occupied = layout.ToDictionary(e => (e.X, e.Y), e => e.Unit);
        var onShip = layout.Select(e => e.Unit).ToHashSet();
        var bag = info.Units.Where(u => !onShip.Contains(u.Index)).Select(u => u.Index).ToList();
        var empty = cells.Where(c => !occupied.ContainsKey(c)).ToList();
        int load = Load(layout);
        var result = new HashSet<string>();
        void Add(List<(int, int, int)> l)
        {
            if (Load(l) <= info.LoadLimit)
            {
                result.Add(Key(l));
            }
        }
        for (int i = 0; i < layout.Count; i++)
        {
            var e = layout[i];
            foreach (var c in empty)
            {
                var l = layout.ToList();
                l[i] = (c.X, c.Y, e.Unit);
                Add(l);
            }
            for (int j = i + 1; j < layout.Count; j++)
            {
                var f = layout[j];
                var l = layout.ToList();
                l[i] = (f.X, f.Y, e.Unit);
                l[j] = (e.X, e.Y, f.Unit);
                Add(l);
            }
            foreach (int b in bag)
            {
                var l = layout.ToList();
                l[i] = (e.X, e.Y, b);
                Add(l);
            }
            var benched = layout.ToList();
            benched.RemoveAt(i);
            Add(benched);
        }
        foreach (int b in bag)
        {
            if (load + info.Units[b].EquipLimit > info.LoadLimit)
            {
                continue;
            }
            foreach (var c in empty)
            {
                var l = layout.ToList();
                l.Add((c.X, c.Y, b));
                Add(l);
            }
        }
        string self = Canon(Key(layout));
        return result.Where(n => Canon(n) != self).ToList();
    }

    /// <summary>A few structurally different starting layouts: fill the ship greedily row-first and column-first.</summary>
    private IEnumerable<string> Seeds()
    {
        var cells = UsableCells();
        var byPower = info.Units.OrderByDescending(u => u.Cannon).ThenByDescending(u => u.Lv).ThenBy(u => u.EquipLimit).ToList();
        foreach (var order in new[]
                 {
                     cells.OrderBy(c => c.Y).ThenBy(c => c.X).ToList(),
                     cells.OrderBy(c => c.X).ThenBy(c => c.Y).ToList(),
                     cells.OrderByDescending(c => c.X).ThenBy(c => c.Y).ToList(),
                 })
        {
            var l = new List<(int, int, int)>();
            int load = 0, k = 0;
            foreach (LoadoutUnit u in byPower)
            {
                if (k >= order.Count || load + u.EquipLimit > info.LoadLimit)
                {
                    continue;
                }
                l.Add((order[k].X, order[k].Y, u.Index));
                load += u.EquipLimit;
                k++;
            }
            yield return Key(l);
        }
    }

    public string Describe(string layout)
    {
        if (info == null || string.IsNullOrEmpty(layout))
        {
            return layout;
        }
        return string.Join(" ", Worker.ParseLayout(layout).Select(e => $"#{info.Units[e.Unit].Id}@{e.X},{e.Y}"));
    }
}
