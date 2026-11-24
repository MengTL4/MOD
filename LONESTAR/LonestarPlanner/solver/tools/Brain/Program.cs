// LONESTAR battle brain.
//
//   Brain --worker                                  one game world, JSON lines on stdio (spawned by the pool)
//   Brain <battle.jsonl> --decide --turn N [--jobs J] [--seconds S]
//   Brain <battle.jsonl> --play [--jobs J] [--seconds S]   play the whole battle from turn 1 (S per turn)
//   Brain <pre-battle.jsonl> --loadout [--jobs J] [--seconds S]
//   Brain --serve [--port P] [--jobs J]             TCP service for the in-game planner mod
//   Brain <pre-battle.jsonl> --ask-loadout [--seconds S]   one loadout request to a running service (tests)
using System.Diagnostics;
using System.Reflection;
using Brain;

// the planner mod ships Brain with the game data beside it (data/export); a dev build finds the repo above bin/
string root = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "data", "export"))
    ? AppContext.BaseDirectory
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
if (Environment.GetEnvironmentVariable("LONESTAR_SOLVER_ROOT") is { Length: > 0 } envRoot)
{
    root = envRoot;
}
string dll = Assembly.GetEntryAssembly()!.Location;
// Each worker's stdout read blocks a pool thread (anonymous pipes are not truly async on Windows);
// without enough threads up front, replies queue behind thread-pool injection (~100ms each).
ThreadPool.SetMinThreads(Math.Max(64, Environment.ProcessorCount * 4), 64);

string recording = null;
int turn = 1;
int jobs = Math.Max(2, Environment.ProcessorCount); // every core (workers run below normal priority)
double seconds = 0;
var only = new List<int>();
bool mergeCheck = false;
int port = 47321;
int parent = 0;
int enemy = 0;
int battleEvent = -1;
bool smokeEvents = false;
string force = null;
int repeat = 1;
string mode = null;
var cfg = new SearchConfig();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--worker": return Worker.Run(root);
        case "--root": root = Path.GetFullPath(args[++i]); break;
        case "--decide": mode = "decide"; break;
        case "--play": mode = "play"; break;
        case "--serve": mode = "serve"; break;
        case "--ask": mode = "ask"; break;
        case "--ask-loadout": mode = "ask-loadout"; break;
        case "--turn": turn = int.Parse(args[++i]); break;
        case "--enemy": enemy = int.Parse(args[++i]); break;
        case "--event": battleEvent = int.Parse(args[++i]); break;
        case "--smoke-events": mode = "smoke"; smokeEvents = true; break;
        case "--force": force = args[++i]; break;
        case "--list-enemies": mode = "enemies"; break;
        case "--greedy": mode = "greedy"; break;
        case "--loadout": mode = "loadout"; break;
        case "--snapstats": mode = "snapstats"; break;
        case "--layoutplay": mode = "layoutplay"; break;
        case "--smoke": mode = "smoke"; break;
        case "--smoke-one": mode = "smoke-one"; break;
        case "--smoke-list": mode = "smoke-list"; break;
        case "--mergecheck": mergeCheck = true; break;
        case "--only": only.AddRange(args[++i].Split(',').Select(int.Parse)); break;
        case "--bench": mode = "bench"; break;
        case "--pingtest": break;
        case "--repeat": repeat = int.Parse(args[++i]); break;
        case "--jobs": jobs = int.Parse(args[++i]); break;
        case "--port": port = int.Parse(args[++i]); break;
        case "--parent": parent = int.Parse(args[++i]); break;
        case "--seconds": seconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--no-swaps": cfg.Swaps = false; break;
        case "--no-skills": cfg.Skills = false; break;
        case "--beam": cfg.Beam = int.Parse(args[++i]); break;
        case "--depth": cfg.Depth = int.Parse(args[++i]); break;
        case "--cands": cfg.Candidates = int.Parse(args[++i]); break;
        case "--look": cfg.Lookahead = int.Parse(args[++i]); break;
        default:
            if (args[i].StartsWith("-")) { Console.Error.WriteLine("unknown option " + args[i]); return 2; }
            recording = Path.GetFullPath(args[i]);
            break;
    }
}

if (mode == "serve")
{
    return await Service.Run(root, dll, jobs, port, parent);
}
if (mode == "ask-loadout")
{
    using var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", port);
    var stream = tcp.GetStream();
    var w = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
    var r = new StreamReader(stream);
    await w.WriteLineAsync(Json.Write(new { op = "loadout", file = recording, enemy, seconds }));
    string reply;
    while ((reply = await r.ReadLineAsync()) != null && reply.StartsWith("{\"progress\""))
    {
    }
    Console.WriteLine(reply);
    return 0;
}
if (mode == "ask")
{
    using var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", port);
    var stream = tcp.GetStream();
    var w = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
    var r = new StreamReader(stream);
    await w.WriteLineAsync(Json.Write(new { op = "solve", battle = recording, mode = "normal" }));
    string reply;
    while ((reply = await r.ReadLineAsync()) != null)
    {
        Console.WriteLine(reply);
        if (!reply.StartsWith("{\"progress\"")) break;
    }
    return 0;
}
if (recording == null || mode == null)
{
    Console.Error.WriteLine("usage: Brain <battle.jsonl> --decide --turn N | --play   [--jobs J] [--fast|--deep]   or   Brain --serve");
    return 2;
}

if (mode == "enemies")
{
    var world = Worker.Open(new Request { Recording = recording, SeekTurn = 1 }, root);
    foreach (var e in Tool.Database.DataEnemyShipManager.Instance().GetDataList().OrderBy(e => e.EnemyPhaseType).ThenBy(e => e.HP))
    {
        Console.WriteLine($"{e.ID,6}  hp={e.HP,4}/{e.HP_1,-4} break={e.BreakCount}/{e.BreakCount_1}  type={e.EnemyType} phase={e.EnemyPhaseType} lv={e.UnlockLV} ingame={e.InGame}  {e.Name}");
    }
    return 0;
}

if (mode == "greedy")
{
    var gw = Worker.Open(new Request { Recording = recording, SeekTurn = 1, EnemyId = enemy, Force = force }, root);
    var o = gw.Session.PlayToEndGreedy(false, cfg.MaxTurns);
    Console.WriteLine(o.Won ? $"RESULT: VICTORY round {o.MaxRound}, player hp {o.PlayerHp}" : o.Ended ? $"RESULT: DEFEAT round {o.MaxRound}, enemy hp {o.EnemyHp}" : $"RESULT: unfinished, enemy hp {o.EnemyHp}");
    return 0;
}

if (mode == "bench")
{
    var bw = Worker.Open(new Request { Recording = recording, SeekTurn = turn }, root);
    var snap = Lonestar.Engine.WorldSnapshot.Capture();
    List<string> acts = bw.Session.LegalActions();
    double tr = 0, ta = 0, ts = 0, th = 0;
    var sw2 = new Stopwatch();
    for (int rep = 0; rep < Math.Max(3, repeat); rep++)
    {
        foreach (string a in acts.Where(a => a != "end"))
        {
            sw2.Restart(); snap.Restore(); tr += sw2.Elapsed.TotalMilliseconds;
            sw2.Restart(); bw.Session.ApplyToken(a); ta += sw2.Elapsed.TotalMilliseconds;
            sw2.Restart(); bw.Session.State(); ts += sw2.Elapsed.TotalMilliseconds;
            sw2.Restart(); Lonestar.Engine.SearchWorld.StateHash(); th += sw2.Elapsed.TotalMilliseconds;
        }
        int n = acts.Count - 1;
        if (rep < 3 || rep % 50 == 0) Console.WriteLine($"rep {rep}: {n} actions  restore {tr / n:F2}ms  apply {ta / n:F2}ms  state {ts / n:F2}ms  hash {th / n:F2}ms per child");
        tr = ta = ts = th = 0;
    }
    if (Environment.GetEnvironmentVariable("BRAIN_CHANGED") == "1")
    {
        // how much of the world one action changes (what a restore after it has to undo)
        var changedTotal = new Dictionary<string, int>();
        var perChild = new List<int>();
        foreach (string a in acts.Where(a => a != "end"))
        {
            snap.Restore();
            bw.Session.ApplyToken(a);
            var ch = snap.ChangedByType();
            perChild.Add(ch.Values.Sum());
            foreach (var kv in ch) { changedTotal.TryGetValue(kv.Key, out int c); changedTotal[kv.Key] = c + kv.Value; }
        }
        snap.Restore();
        perChild.Sort();
        Console.WriteLine($"objects changed by one action: median {perChild[perChild.Count / 2]}, max {perChild[^1]} of {snap.ObjectCount}");
        foreach (var kv in changedTotal.OrderByDescending(kv => kv.Value).Take(20)) Console.WriteLine($"  {kv.Value / (double)perChild.Count,8:F1}  {kv.Key}");
    }
    foreach (string kind in new[] { "loadp", "move", "swap" })
    {
        var k = acts.Where(a => a.StartsWith(kind)).ToList();
        if (k.Count == 0) continue;
        sw2.Restart();
        foreach (string a in k) { snap.Restore(); bw.Session.ApplyToken(a); }
        Console.WriteLine($"  {kind}: {k.Count} x {sw2.Elapsed.TotalMilliseconds / k.Count:F2}ms (restore+apply), frames/apply ~{UnityEngine.UnityRuntime.FrameCount}");
    }
    return 0;
}

if (mode == "smoke-list")
{
    return Smoke.List(recording, root);
}
if (mode == "smoke-one")
{
    return Smoke.One(recording, enemy, root, mergeCheck, battleEvent);
}
if (mode == "smoke")
{
    return await Smoke.All(recording, dll, jobs, root, only, mergeCheck, smokeEvents);
}

if (mode == "layoutplay")
{
    // greedy playout of the current layout, turn by turn
    Lonestar.Engine.SearchWorld lw0 = null;
    LoadoutInfo li0 = Worker.Handle(new Request { Op = "prepare", Recording = recording, EnemyId = enemy }, root, ref lw0).Loadout;
    Response lr0 = Worker.Handle(new Request { Op = "layout", Layout = li0.Current, Rollout = false }, root, ref lw0);
    if (!lr0.Ok || lw0 == null)
    {
        Console.WriteLine("layout failed: " + lr0.Error);
        Console.WriteLine("blocker: " + UnityEngine.UnityRuntime.QuiescenceBlocker());
        foreach (string e in UnityEngine.UnityRuntime.Errors.Skip(3))
        {
            Console.WriteLine("  error: " + string.Join(" | ", e.Split('\n').Take(8)));
        }
        return 1;
    }
    var ses = lw0.Session;
    UnityEngine.UnityRuntime.MergeStats = new Dictionary<string, int>();
    Console.WriteLine("problems: " + string.Join(" | ", ses.Problems));
    Console.WriteLine("boot errors: " + string.Join(" | ", Lonestar.Engine.HeadlessGame.BootErrors.Select(e => e.Split('\n')[0])));
    Console.WriteLine("blocker: " + UnityEngine.UnityRuntime.QuiescenceBlocker());
    foreach (string e in UnityEngine.UnityRuntime.Errors.Skip(3))
    {
        Console.WriteLine("  error: " + string.Join(" | ", e.Split('\n').Take(6)));
    }
    for (int t = 0; t < 40 && !ses.IsBattleOver(); t++)
    {
        var st = ses.State();
        Console.WriteLine($"turn {t}: round {st.Round} status {st.TurnStatus} choice {st.ChoicePending} inBattle {st.InBattle} player {st.Player.Hp}+{st.Player.Shield} enemy {st.Enemy.Hp}+{st.Enemy.Shield} legal {string.Join(",", ses.LegalActions().Take(8))}");
        var o = ses.GreedyTurn();
        Console.WriteLine($"   -> dealt {o.EnemyHpLost} taken {o.PlayerHpLost} ended {o.BattleEnded}");
    }
    var fin = ses.State();
    Console.WriteLine("blocker at the end: " + UnityEngine.UnityRuntime.QuiescenceBlocker());
    Console.WriteLine($"merging saved {UnityEngine.UnityRuntime.MergedFrames} frames:");
    foreach (var kv in UnityEngine.UnityRuntime.MergeStats.OrderByDescending(kv => kv.Value).Take(10)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
    Console.WriteLine($"final: round {fin.Round} status {fin.TurnStatus} choice {fin.ChoicePending} over {ses.IsBattleOver()} player {fin.Player.Hp} enemy {fin.Enemy.Hp}");
    return 0;
}

if (mode == "snapstats")
{
    // what a world snapshot holds, at the start of the battle and a few greedy turns in
    Lonestar.Engine.SearchWorld sworld = null;
    LoadoutInfo li = Worker.Handle(new Request { Op = "prepare", Recording = recording, EnemyId = enemy }, root, ref sworld).Loadout;
    Worker.Handle(new Request { Op = "layout", Layout = li.Current, Rollout = false }, root, ref sworld);
    {
        // which scene trees the world is made of (GameObjects per root, with their descendants)
        var gos = (System.Collections.IList)typeof(UnityEngine.UnityRuntime).GetField("AllGameObjects", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
        var perRoot = new Dictionary<string, int>();
        foreach (UnityEngine.GameObject g in gos)
        {
            UnityEngine.Transform tr = g.transform;
            var path = new List<string>();
            while (tr != null) { path.Insert(0, tr.gameObject.name); tr = tr.parent; }
            string key = string.Join("/", path.Take(3));
            perRoot.TryGetValue(key, out int c);
            perRoot[key] = c + 1;
        }
        Console.WriteLine($"gameobjects {gos.Count}");
        foreach (var kv in perRoot.OrderByDescending(kv => kv.Value).Take(30)) Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
    }
    bool roots = Environment.GetEnvironmentVariable("BRAIN_ROOTS") == "1";
    Dictionary<string, long> reach0 = roots ? Diag.ReachPerRoot() : null;
    for (int t = 0; t <= 6; t += 3)
    {
        long before = GC.GetTotalMemory(true);
        var snap = Lonestar.Engine.WorldSnapshot.Capture();
        long after = GC.GetTotalMemory(true);
        Console.WriteLine($"turn +{t}: {snap.ObjectCount} objects, capture {snap.CaptureMs:F1}ms, +{(after - before) / 1048576.0:F1}MB heap, total heap {after / 1048576.0:F0}MB");
        if (t == 0)
        {
            foreach (var g in snap.SizeByType().GroupBy(x => x.Type.Contains('.') ? x.Type.Substring(0, x.Type.IndexOf('.')) : x.Type.Contains('[') ? "(arrays/generic)" : "(game: " + x.Type + ")")
                         .Select(g => (g.Key, N: g.Sum(x => x.Count))).OrderByDescending(g => g.N).Take(25))
            {
                Console.WriteLine($"    group {g.N,7}  {g.Key}");
            }
        }
        foreach (var (type, count, bytes) in snap.SizeByType().OrderByDescending(x => x.Count).Take(t == 0 ? 30 : 0))
        {
            Console.WriteLine($"    {bytes / 1048576.0,8:F2}MB {count,7}  {type}");
        }
        GC.KeepAlive(snap);
        for (int k = 0; k < 3 && !sworld.Session.IsBattleOver(); k++)
        {
            sworld.Session.GreedyTurn();
        }
    }
    {
        var errs = UnityEngine.UnityRuntime.Errors;
        Console.WriteLine($"errors: {errs.Count}");
        foreach (var g in errs.GroupBy(e => e.Length > 300 ? e.Substring(0, 300) : e).OrderByDescending(g => g.Count()).Take(6))
        {
            Console.WriteLine($"  x{g.Count()}: {g.Key.Replace((char)13, (char)32).Replace((char)10, (char)124)}");
        }
    }
    if (roots)
    {
        Dictionary<string, long> reach1 = Diag.ReachPerRoot();
        foreach (var kv in reach1.Select(kv => (kv.Key, Grew: kv.Value - reach0.GetValueOrDefault(kv.Key), kv.Value)).OrderByDescending(x => x.Grew).Take(25))
        {
            Console.WriteLine($"  root +{kv.Grew,9} = {kv.Value,9}  {kv.Key}");
        }
    }
    if (Environment.GetEnvironmentVariable("BRAIN_PAUSE") == "1")
    {
        GC.Collect();
        Console.WriteLine($"paused, pid {Environment.ProcessId}");
        Thread.Sleep(180_000);
    }
    return 0;
}

if (mode == "loadout")
{
    var lw = Stopwatch.StartNew();
    using var lpool = new WorkerPool(jobs, dll);
    var ls = new LoadoutSearch(lpool, cfg, Console.WriteLine) { Seconds = seconds > 0 ? seconds : Service.DefaultLoadoutSeconds };
    List<LoadoutValue> ranked = await ls.Run(recording, enemy);
    Console.WriteLine();
    foreach (LoadoutValue v in ranked.Take(6))
    {
        Console.WriteLine($"  full={Search.Fmt(v.Full),-22} greedy={Search.Fmt(v.Greedy),-22} {(v.Layout == ls.Info.Current ? "[当前] " : "")}{ls.Describe(v.Layout)}");
    }
    Console.WriteLine($"total {lw.Elapsed.TotalSeconds:F0}s");
    return 0;
}

var total = Stopwatch.StartNew();
using var pool = new WorkerPool(jobs, dll);
List<Response> init = await pool.Broadcast(() => new Request { Op = "init", Recording = recording, SeekTurn = mode == "play" || enemy > 0 || force != null ? 1 : turn, EnemyId = enemy, Force = force });
if (init.Any(r => !r.Ok))
{
    Console.Error.WriteLine("init failed: " + init.First(r => !r.Ok).Error);
    return 1;
}
NodeInfo start = init[0].Node;
Console.WriteLine($"{jobs} workers ready in {total.ElapsedMilliseconds}ms: round {start.Round}, player {start.PHp}/{start.PMaxHp}hp {start.PSh}sh, enemy {start.EHp}/{start.EMaxHp}hp break {start.EBreak}/{start.EBreakLimit}, hand {start.Hand}");
if (init[0].Recorded?.Count > 0)
{
    Console.WriteLine("human this turn: " + string.Join(";", init[0].Recorded));
}

if (args.Contains("--pingtest"))
{
    for (int k = 0; k < 5; k++)
    {
        var pw = Stopwatch.StartNew();
        var rs = await pool.RunAll(Enumerable.Range(0, jobs).Select(_ => new Request { Op = "state", Prefix = "" }).ToList());
        Console.WriteLine($"ping x{jobs}: wall {pw.ElapsedMilliseconds}ms, worker avg {rs.Average(r => r.Ms):F1}ms");
    }
    Console.WriteLine($"cpus {Environment.ProcessorCount}");
    return 0;
}
var search = new Search(pool, cfg, Console.WriteLine);
if (mode == "decide")
{
    for (int k = 1; k < repeat; k++)
    {
        Decision dk = await search.Decide();
        Console.WriteLine($"### repeat {k}: {dk.Seconds:F1}s");
        await pool.Broadcast(() => new Request { Op = "advance", Tokens = "" });
    }
    if (seconds > 0) { search.Deadline = DateTime.UtcNow.AddSeconds(seconds); search.UseSpareTime = true; }
    Decision d = await search.Decide();
    Console.WriteLine();
    Console.WriteLine("top plans:");
    foreach (PlanValue p in d.Ranked.Take(8))
    {
        Console.WriteLine($"  {Search.Fmt(p.Best),-22} h={p.Heuristic,9:F0}  {p.Prefix}{(p.LookPlan != null ? "   || next: " + p.LookPlan : "")}");
    }
    // fuel (move points) carries over between battles: show what keeping it this turn would cost
    PlanValue still = d.Ranked.FirstOrDefault(p => !Search.Short(p.Prefix, "").Contains("move:"));
    if (still != null && d.Best != null && d.Best.Prefix.Contains("move:"))
    {
        Console.WriteLine($"best without a move: {Search.Fmt(still.Best),-22} h={still.Heuristic,9:F0}  {still.Prefix}");
    }
    return 0;
}

// play the battle out, one decision per turn
NodeInfo node = start;
var lines = new List<string>();
string expectHash = null, hint = null;
for (int t = 0; t < 40 && !node.Over; t++)
{
    search.Hint = node.Hash == expectHash ? hint : null;
    if (seconds > 0) { search.Deadline = DateTime.UtcNow.AddSeconds(seconds); search.UseSpareTime = true; }
    Decision d = await search.Decide();
    expectHash = d.NextHash;
    hint = d.NextPlan;
    string tokens = Search.Join(d.Plan, "end");
    List<Response> adv = await pool.Broadcast(() => new Request { Op = "advance", Tokens = tokens });
    if (adv.Any(r => !r.Ok))
    {
        Console.WriteLine("advance failed: " + adv.First(r => !r.Ok).Error);
        return 1;
    }
    NodeInfo after = adv[0].Node;
    string line = $"round {node.Round}: {tokens}   => player {after.PHp}hp{after.PSh}sh enemy {after.EHp}hp  ({d.Seconds:F1}s, {d.Jobs} jobs)";
    lines.Add(line);
    Console.WriteLine("### " + line);
    node = after;
}
Console.WriteLine();
foreach (string l in lines) Console.WriteLine(l);
Console.WriteLine(node.Over ? (node.Won ? $"RESULT: VICTORY round {node.Round}, player hp {node.PHp}" : $"RESULT: DEFEAT round {node.Round}, enemy hp {node.EHp}") : $"RESULT: unfinished, enemy hp {node.EHp}");
Console.WriteLine($"total {total.Elapsed.TotalSeconds:F0}s");
return 0;
