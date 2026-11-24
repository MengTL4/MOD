using System.Diagnostics;
using Lonestar.Engine;

namespace Brain;

/// <summary>
/// One game world in this process, driven over stdin/stdout (one JSON request/response per line).
/// The master keeps a pool of these; each holds the same root decision point and reaches any
/// search node by restoring snapshots and replaying tokens (<see cref="SearchWorld"/>).
/// </summary>
public static class Worker
{
    public static int Run(string root)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(TextWriter.Null); // stdout is the protocol channel; nothing else may write to it
        var stdin = new StreamReader(Console.OpenStandardInput());
        SearchWorld world = null;
        string line;
        while ((line = stdin.ReadLine()) != null)
        {
            if (line.Length == 0)
            {
                continue;
            }
            Request req;
            try
            {
                req = Json.Read<Request>(line);
            }
            catch (Exception ex)
            {
                stdout.WriteLine(Json.Write(new Response { Ok = false, Error = "bad request: " + ex.Message }));
                continue;
            }
            var sw = Stopwatch.StartNew();
            Response resp;
            try
            {
                resp = Handle(req, root, ref world);
            }
            catch (Exception ex)
            {
                resp = new Response { Ok = false, Error = ex.GetType().Name + ": " + ex.Message };
            }
            resp.Id = req.Id;
            resp.Ms = sw.Elapsed.TotalMilliseconds;
            stdout.WriteLine(Json.Write(resp));
            if (req.Op == "quit")
            {
                break;
            }
        }
        try
        {
            // only this world's save files; the service purges folders of workers that were killed
            string work = WorkPath(root);
            if (Directory.Exists(work))
            {
                Directory.Delete(work, true);
            }
        }
        catch
        {
            // still in use: purged later
        }
        return 0;
    }

    public static Response Handle(Request req, string root, ref SearchWorld world)
    {
        switch (req.Op)
        {
            case "init":
            {
                world = Open(req, root);
                return new Response { Node = Info(world.Session, null), Recorded = world.Session.RecordedTurnTokens(), Applied = world.Session.AppliedActions };
            }
            case "sync":
            {
                Need(world);
                int n = world.SyncRecording();
                return new Response { Applied = n, Node = Info(world.Session, null), Sync = world.Session.CheckSync() };
            }
            case "advance":
            {
                Need(world);
                if (!world.Advance(req.Tokens))
                {
                    return new Response { Ok = false, Error = "advance failed: " + string.Join(" | ", world.Session.Problems.TakeLast(3)) };
                }
                return new Response { Node = Info(world.Session, null), Recorded = world.Session.RecordedTurnTokens() };
            }
            case "state":
            {
                Need(world);
                if (!world.GoTo(req.Prefix))
                {
                    return new Response { Ok = false, Error = "illegal prefix" };
                }
                return new Response { Node = Info(world.Session, null) };
            }
            case "expand":
                Need(world);
                return Expand(world, req);
            case "greedy":
            {
                Need(world);
                if (!world.GoTo(req.Prefix))
                {
                    return new Response { Ok = false, Error = "illegal prefix" };
                }
                var plans = new List<List<string>>();
                var nodes = new List<NodeInfo>();
                foreach (string name in req.Policies ?? new List<string> { "default" })
                {
                    if (plans.Count > 0 && !world.GoTo(req.Prefix))
                    {
                        break;
                    }
                    var tokens = new List<string>();
                    world.Session.GreedyTokens(tokens, RolloutPolicy.ByName(name));
                    plans.Add(tokens);
                    nodes.Add(Info(world.Session, name));
                }
                return new Response { Plans = plans, Children = nodes, Recorded = plans.FirstOrDefault(), Node = nodes.FirstOrDefault() };
            }
            case "eval":
                Need(world);
            {
                SearchWorld w = world;
                return new Response { Evals = (req.Prefixes ?? new List<string> { req.Prefix }).Select(p => Eval(w, p, req)).ToList() };
            }
            case "prepare":
            {
                world = null;
                pre = null;
                session = BattleSession.OpenPreBattle(req.Recording, Path.Combine(root, "data", "export"),
                    Path.Combine(root, "data", "resources_index.tsv"), WorkDir(root), null, req.EnemyId);
                pre = WorldSnapshot.Capture();
                return new Response { Loadout = ReadLoadout() };
            }
            case "layout":
            {
                if (pre == null)
                {
                    throw new InvalidOperationException("prepare first");
                }
                var lsw = Stopwatch.StartNew();
                pre.Restore();
                double lRestore = lsw.Elapsed.TotalMilliseconds;
                if (!LayoutEditor.Apply(ParseLayout(req.Layout), out string error))
                {
                    return new Response { Ok = false, Error = "layout rejected: " + error };
                }
                double lApply = lsw.Elapsed.TotalMilliseconds;
                if (!session.EnterBattleNow())
                {
                    // never score a battle that did not start (every layout would look the same)
                    world = null;
                    return new Response { Ok = false, Error = "battle did not start: " + string.Join("; ", session.Problems) };
                }
                double lEnter = lsw.Elapsed.TotalMilliseconds;
                if (req.Rollout)
                {
                    var e = new EvalResult { Prefix = req.Layout, Ok = true, Next = Info(session, null) };
                    e.Rollout = session.PlayToEndGreedy(false, req.MaxTurns);
                    e.Rollout.Details = null;
                    world = null;
                    double lEnd = lsw.Elapsed.TotalMilliseconds;
                    return new Response
                    {
                        Evals = new List<EvalResult> { e },
                        Profile = Trace ? $"restore={lRestore:F1} layout={lApply - lRestore:F1} enter={lEnter - lApply:F1} playout={lEnd - lEnter:F1} turns={e.Rollout.Turns}" : null,
                    };
                }
                world = new SearchWorld(session);
                return new Response { Node = Info(session, null) };
            }
            case "quit":
                return new Response();
            default:
                return new Response { Ok = false, Error = "unknown op " + req.Op };
        }
    }

    private static BattleSession session;
    private static WorldSnapshot pre;

    private static string WorkPath(string root) => Path.Combine(root, "out", "brain", Environment.ProcessId.ToString());

    private static string WorkDir(string root)
    {
        string work = WorkPath(root);
        Directory.CreateDirectory(work);
        return work;
    }

    public static List<(int X, int Y, int Unit)> ParseLayout(string text) =>
        string.IsNullOrEmpty(text)
            ? new List<(int, int, int)>()
            : text.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split(',').Select(int.Parse).ToArray())
                .Select(a => (a[0], a[1], a[2])).ToList();

    public static string FormatLayout(IEnumerable<(int X, int Y, int Unit)> layout) =>
        string.Join(";", layout.OrderBy(e => e.Y).ThenBy(e => e.X).Select(e => $"{e.X},{e.Y},{e.Unit}"));

    private static LoadoutInfo ReadLoadout()
    {
        var info = new LoadoutInfo { LoadLimit = LayoutEditor.Ship.shipLoadLimit };
        foreach (CellInfo c in LayoutEditor.Cells())
        {
            info.Cells.Add(new[] { c.X, c.Y, c.Status });
        }
        var items = Singleton<ItemManager>.Instance().itemUnits;
        foreach (AvailableUnit u in LayoutEditor.AvailableUnits())
        {
            info.Units.Add(new LoadoutUnit
            {
                Index = u.Index,
                Id = u.Id,
                Lv = u.Lv,
                EquipLimit = u.EquipLimit,
                OnShip = u.OnShip,
                X = u.OnShip ? u.X : -1,
                Y = u.OnShip ? u.Y : -1,
                Bag = u.OnShip ? -1 : items.FindIndex(i => i?.unitData == u.Data),
                Cannon = IsCannon(u.Data),
                Sig = Signature(u),
            });
        }
        info.Current = FormatLayout(info.Units.Where(u => u.OnShip).Select(u => (u.X, u.Y, u.Index)));
        return info;
    }

    private static string Signature(AvailableUnit u)
    {
        UnitData d = u.Data;
        if (d == null)
        {
            return $"{u.Id}/{u.Lv}/#{u.Index}";
        }
        return $"{d.id}/{d.lv}/{d.modID}/{d.nameInMod}/{string.Join(",", d.slotRefitTypes ?? new List<SlotRefitType>())}/{string.Join(",", d.args ?? new List<int>())}";
    }

    private static bool IsCannon(UnitData data)
    {
        try
        {
            return data?.GetData()?.PowerSlot?.Length > 0;
        }
        catch
        {
            return true;
        }
    }

    private static void Need(SearchWorld world)
    {
        if (world == null)
        {
            throw new InvalidOperationException("worker not initialised");
        }
    }

    public static SearchWorld Open(Request req, string root)
    {
        string work = Path.Combine(root, "out", "brain", Environment.ProcessId.ToString());
        Directory.CreateDirectory(work);
        List<(int X, int Y, int Unit)> layout = null;
        if (!string.IsNullOrEmpty(req.Layout))
        {
            layout = req.Layout.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split(',').Select(int.Parse).ToArray())
                .Select(a => (a[0], a[1], a[2])).ToList();
        }
        BattleSession session = BattleSession.Open(req.Recording, Path.Combine(root, "data", "export"),
            Path.Combine(root, "data", "resources_index.tsv"), work, null,
            layout == null && string.IsNullOrEmpty(req.Force) ? null : s =>
            {
                if (layout != null && !LayoutEditor.Apply(layout, out string error))
                {
                    throw new InvalidOperationException("layout rejected: " + error);
                }
                if (!string.IsNullOrEmpty(req.Force))
                {
                    foreach (string part in req.Force.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        int[] f = part.Split(',').Select(int.Parse).ToArray();
                        ShipCell cell = LayoutEditor.Cell(f[0], f[1]);
                        if (cell == null || cell.shipCellStatus == ShipCellStatus.None)
                        {
                            throw new InvalidOperationException($"cell {f[0]},{f[1]} is not usable");
                        }
                        cell.unitData = new UnitData(f[2], f[3]);
                        cell.shipCellStatus = ShipCellStatus.Equipped;
                    }
                }
            }, req.EnemyId);
        if (req.SeekTurn >= 1)
        {
            session.SeekToTurn(req.SeekTurn);
        }
        else if (req.SeekTurn == 0)
        {
            session.SyncRecording(); // live: everything recorded so far
        }
        var world = new SearchWorld(session);
        if (!string.IsNullOrEmpty(req.Tokens) && !world.Advance(req.Tokens))
        {
            throw new InvalidOperationException("init tokens failed");
        }
        return world;
    }

    public static NodeInfo Info(BattleSession s, string tok) =>
        NodeInfo.From(s.State(), tok, SearchWorld.StateHash());

    /// <summary>Children of a node: every legal non-end action applied once from the node's state.</summary>
    private static readonly bool Trace = Environment.GetEnvironmentVariable("BRAIN_TRACE") == "1";

    public static Response Expand(SearchWorld world, Request req)
    {
        var psw = Stopwatch.StartNew();
        double tGoTo, tCapture, tRestore = 0, tApply = 0, tInfo = 0;
        int replayed = world.TokensApplied;
        if (!world.GoTo(req.Prefix))
        {
            return new Response { Ok = false, Error = "illegal prefix " + req.Prefix };
        }
        replayed = world.TokensApplied - replayed;
        tGoTo = psw.Elapsed.TotalMilliseconds;
        WorldSnapshot node = world.Current ?? WorldSnapshot.Capture();
        tCapture = psw.Elapsed.TotalMilliseconds - tGoTo;
        NodeInfo self = Info(world.Session, null);
        var children = new List<NodeInfo>();
        if (self.InTurn)
        {
            string[] toks = SearchWorld.Split(req.Prefix);
            int lastEnd = Array.LastIndexOf(toks, "end");
            int swapsThisTurn = toks.Skip(lastEnd + 1).Count(t => t.StartsWith("swap:", StringComparison.Ordinal));
            List<string> actions = world.Session.LegalActions(req.Swaps && swapsThisTurn < req.MaxSwaps, req.Skills);
            foreach (string a in actions)
            {
                if (a == "end")
                {
                    continue;
                }
                double t0 = psw.Elapsed.TotalMilliseconds;
                node.Restore();
                double t1 = psw.Elapsed.TotalMilliseconds;
                bool ok;
                try
                {
                    ok = world.Session.ApplyToken(a);
                }
                catch (Exception)
                {
                    ok = false;
                }
                double t2 = psw.Elapsed.TotalMilliseconds;
                children.Add(ok ? Info(world.Session, a) : new NodeInfo { Tok = a, Ok = false });
                tRestore += t1 - t0;
                tApply += t2 - t1;
                tInfo += psw.Elapsed.TotalMilliseconds - t2;
            }
            node.Restore();
        }
        return new Response
        {
            Node = self,
            Children = children,
            Profile = Trace ? $"goto={tGoTo:F1}({replayed} tok) capture={tCapture:F1} restore={tRestore:F1} apply={tApply:F1} info={tInfo:F1} n={children.Count}" : null,
        };
    }

    public static EvalResult Eval(SearchWorld world, string prefix, Request req)
    {
        var r = new EvalResult { Prefix = prefix };
        if (!world.GoTo(prefix, cacheResult: false))
        {
            r.Error = "illegal prefix";
            return r;
        }
        BattleSession s = world.Session;
        if (!s.IsBattleOver())
        {
            r.Turn = s.EndTurn();
            if (!s.IsBattleOver())
            {
                // the lookahead continues from here; the pool routes it back to this worker
                world.Put(SearchWorld.Join(prefix, "end"), WorldSnapshot.Capture());
            }
        }
        r.Next = Info(world.Session, null);
        r.Ok = true;
        if (req.Rollout && !r.Next.Over)
        {
            List<string> policies = req.Policies?.Count > 0 ? req.Policies : new List<string> { "default" };
            WorldSnapshot after = policies.Count > 1 ? WorldSnapshot.Capture() : null;
            double best = double.NegativeInfinity;
            foreach (string name in policies)
            {
                after?.Restore();
                BattleOutcome o = s.PlayToEndGreedy(false, req.MaxTurns, RolloutPolicy.ByName(name));
                o.Details = null;
                double v = Search.OutcomeScore(new EvalResult { Ok = true, Next = r.Next, Rollout = o });
                if (v > best)
                {
                    best = v;
                    r.Rollout = o;
                    r.Policy = name;
                }
            }
        }
        return r;
    }
}
