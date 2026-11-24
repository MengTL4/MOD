// Validates WorldSnapshot: restore must reproduce the exact same future as the first run.
//   SnapTest <battle.jsonl> [--turn N] [--rounds K]
using System.Diagnostics;
using Lonestar.Engine;

string rec = args[0];
int turn = 1;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--turn") turn = int.Parse(args[++i]);
}
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string work = Path.Combine(root, "out", "snaptest", Environment.ProcessId.ToString());
Directory.CreateDirectory(work);
var sw = Stopwatch.StartNew();
var session = BattleSession.Open(rec, Path.Combine(root, "data", "export"), Path.Combine(root, "data", "resources_index.tsv"), work, null);
Console.WriteLine($"open {sw.ElapsedMilliseconds}ms problems={session.Problems.Count}");
if (turn > 1 && !session.SeekToTurn(turn))
{
    Console.WriteLine("seek failed");
    return 1;
}
Console.WriteLine($"at round {session.State().Round}  roots={WorldSnapshot.RootCount}");

if (args.Contains("--nocapture"))
{
    for (int k = 0; k < 30 && !session.IsBattleOver(); k++) session.GreedyTurn();
    var st0 = session.State();
    Console.WriteLine($"baseline END player={st0.Player.Hp} enemy={st0.Enemy.Hp} round={st0.Round}");
    return 0;
}
if (args.Contains("--readonly"))
{
    var w0 = System.Diagnostics.Stopwatch.StartNew();
    WorldSnapshot.Capture();
    Console.WriteLine($"captured once {w0.ElapsedMilliseconds}ms; running without restore");
    for (int k = 0; k < 30 && !session.IsBattleOver(); k++) session.GreedyTurn();
    var st1 = session.State();
    Console.WriteLine($"END player={st1.Player.Hp} enemy={st1.Enemy.Hp} round={st1.Round}");
    return 0;
}
if (args.Contains("--profile"))
{
    // Same greedy line at several frame lengths; outcomes must match dt=1/60.
    WorldSnapshot start = WorldSnapshot.Capture();
    foreach (string d in UnityEngine.UnityRuntime.DescribeActiveTweens()) Console.WriteLine("  active tween: " + d);
    {
        var lo = session.LoadOptions();
        if (lo.Count > 0) { int f0 = UnityEngine.UnityRuntime.FrameCount; session.ApplyLoad(lo[0]); Console.WriteLine($"  one load took {UnityEngine.UnityRuntime.FrameCount - f0} frames, blocker: {UnityEngine.UnityRuntime.QuiescenceBlocker()}"); }
        start.Restore();
    }
    UnityEngine.UnityRuntime.Profile = new long[6];
    if (args.Contains("--blockers"))
    {
        UnityEngine.UnityRuntime.BlockerStats = new Dictionary<string, int>();
        UnityEngine.UnityRuntime.MergeStats = new Dictionary<string, int>();
        int f0 = UnityEngine.UnityRuntime.FrameCount;
        while (!session.IsBattleOver()) session.GreedyTurn();
        Console.WriteLine($"frames {UnityEngine.UnityRuntime.FrameCount - f0}, waiting on:");
        foreach (var kv in UnityEngine.UnityRuntime.BlockerStats.OrderByDescending(kv => kv.Value).Take(15))
            Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
        UnityEngine.UnityRuntime.BlockerStats = null;
        Console.WriteLine($"merging (saved {UnityEngine.UnityRuntime.MergedFrames} frames):");
        foreach (var kv in UnityEngine.UnityRuntime.MergeStats.OrderByDescending(kv => kv.Value).Take(12))
            Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
        UnityEngine.UnityRuntime.MergeStats = null;
        start.Restore();
    }
    {
        var w0 = System.Diagnostics.Stopwatch.StartNew();
        int fr = 0;
        while (!session.IsBattleOver()) { fr += session.GreedyTurn().Frames; Console.WriteLine($"  tweens={UnityEngine.UnityRuntime.TweenCount} active={UnityEngine.UnityRuntime.ActiveTweens} coroutines={UnityEngine.UnityRuntime.ActiveCoroutines}"); }
        double tot = w0.Elapsed.TotalMilliseconds;
        string[] names = { "starts", "Update", "coroutines", "tweens", "LateUpdate", "endframe" };
        Console.WriteLine($"profile: total {tot:F0}ms, {fr} frames in EndTurn");
        for (int k = 0; k < 6; k++) Console.WriteLine($"  {names[k],-10} {UnityEngine.UnityRuntime.Profile[k] * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0}ms");
        UnityEngine.UnityRuntime.Profile = null;
    }
    List<string> reference = null;
    foreach (float dt in new[] { 1f / 60f, 1f / 30f, 1f / 15f, 0.1f, 0.2f, 0.25f, 0.5f })
    {
        start.Restore();
        UnityEngine.UnityRuntime.DeltaTime = dt;
        var fps = new List<string>();
        var w = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0;
        while (!session.IsBattleOver() && fps.Count < 30)
        {
            TurnOutcome o = session.GreedyTurn();
            frames += o.Frames;
            fps.Add(BattleSession.Fingerprint());
        }
        var st = session.State();
        fps.Add($"END player={st.Player.Hp} enemy={st.Enemy.Hp} round={st.Round}");
        reference ??= fps;
        int same = reference.Zip(fps).Count(p => p.First == p.Second);
        Console.WriteLine($"dt={dt:F4}: {w.ElapsedMilliseconds}ms frames={frames} turns={fps.Count - 1} {fps[^1]} match={same}/{reference.Count}");
        if (same < reference.Count && args.Contains("--why"))
        {
            int idx = reference.Zip(fps).TakeWhile(p => p.First == p.Second).Count();
            var ra = System.Text.Json.JsonDocument.Parse(reference[idx]).RootElement;
            var rb = System.Text.Json.JsonDocument.Parse(fps[idx]).RootElement;
            JsonDiff("", ra, rb, 8);
        }
    }
    UnityEngine.UnityRuntime.DeltaTime = 1f / 60f;
    return 0;
}
string fp0 = BattleSession.Fingerprint();
WorldSnapshot snap = WorldSnapshot.Capture();
Console.WriteLine($"capture: {snap.ObjectCount} objects in {snap.CaptureMs:F1}ms");
foreach (string v in snap.BrokenLists()) Console.WriteLine("    broken list at capture: " + v);
for (int k = 0; k < 3; k++)
{
    var w = Stopwatch.StartNew();
    var s2 = WorldSnapshot.Capture();
    Console.WriteLine($"  re-capture {s2.ObjectCount} objects {w.Elapsed.TotalMilliseconds:F1}ms");
}

List<string> Run()
{
    var fps = new List<string>();
    var s = Stopwatch.StartNew();
    while (!session.IsBattleOver() && fps.Count < 30)
    {
        session.GreedyTurn();
        fps.Add(BattleSession.Fingerprint());
    }
    var st = session.State();
    fps.Add($"END player={st.Player.Hp} enemy={st.Enemy.Hp} round={st.Round}");
    Console.WriteLine($"  run: {fps.Count - 1} turns, {fps[^1]}, {s.ElapsedMilliseconds}ms");
    return fps;
}

List<string> a = Run();
int failures = 0;
for (int trial = 0; trial < 3; trial++)
{
    var r = Stopwatch.StartNew();
    snap.Restore();
    double restoreMs = r.Elapsed.TotalMilliseconds;
    foreach (string v in snap.BrokenLists()) Console.WriteLine("    broken list: " + v);
    string fp = BattleSession.Fingerprint();
    Console.WriteLine($"restore #{trial}: {restoreMs:F1}ms fingerprint {(fp == fp0 ? "MATCH" : "DIFF")}");
    foreach (string v in snap.Verify(6)) Console.WriteLine("    changed by fingerprint: " + v);
    if (fp != fp0) { failures++; Diff(fp0, fp); }
    if (trial == 1)
    {
        // dirty branch: a different line of play, then restore again
        var opts = session.LoadOptions();
        if (opts.Count > 0) session.ApplyLoad(opts[^1]);
        session.EndTurn();
        snap.Restore();
        fp = BattleSession.Fingerprint();
        Console.WriteLine($"  after dirty branch: fingerprint {(fp == fp0 ? "MATCH" : "DIFF")}");
        if (fp != fp0) { failures++; Diff(fp0, fp); }
    }
    List<string> b = Run();
    int same = a.Zip(b).Count(p => p.First == p.Second);
    bool ok = a.Count == b.Count && same == a.Count;
    Console.WriteLine($"  future: {same}/{Math.Max(a.Count, b.Count)} turn fingerprints identical {(ok ? "OK" : "MISMATCH")}");
    if (!ok)
    {
        failures++;
        int idx = a.Zip(b).TakeWhile(p => p.First == p.Second).Count();
        if (idx < a.Count && idx < b.Count) Diff(a[idx], b[idx]);
    }
}
Console.WriteLine(failures == 0 ? "PASS" : $"FAIL ({failures})");
return failures == 0 ? 0 : 1;

static void JsonDiff(string path, System.Text.Json.JsonElement a, System.Text.Json.JsonElement b, int max)
{
    if (a.ValueKind != b.ValueKind) { Console.WriteLine($"    {path}: {a} | {b}"); return; }
    switch (a.ValueKind)
    {
        case System.Text.Json.JsonValueKind.Object:
            foreach (var p in a.EnumerateObject())
                if (b.TryGetProperty(p.Name, out var q)) JsonDiff(path + "." + p.Name, p.Value, q, max);
                else Console.WriteLine($"    {path}.{p.Name}: missing");
            break;
        case System.Text.Json.JsonValueKind.Array:
            var xa = a.EnumerateArray().ToList(); var xb = b.EnumerateArray().ToList();
            if (xa.Count != xb.Count) { Console.WriteLine($"    {path}: len {xa.Count} | {xb.Count}"); }
            for (int i = 0; i < Math.Min(xa.Count, xb.Count); i++) JsonDiff($"{path}[{i}]", xa[i], xb[i], max);
            break;
        default:
            if (a.GetRawText() != b.GetRawText()) Console.WriteLine($"    {path}: {a.GetRawText()} | {b.GetRawText()}");
            break;
    }
}

static void Diff(string x, string y)
{
    int i = 0;
    while (i < x.Length && i < y.Length && x[i] == y[i]) i++;
    int from = Math.Max(0, i - 200);
    Console.WriteLine("    first diff at " + i);
    Console.WriteLine("    A: " + x.Substring(from, Math.Min(400, x.Length - from)));
    Console.WriteLine("    B: " + y.Substring(from, Math.Min(400, y.Length - from)));
}
