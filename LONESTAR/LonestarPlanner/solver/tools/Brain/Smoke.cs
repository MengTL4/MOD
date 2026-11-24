using System.Diagnostics;
using Lonestar.Engine;
using UnityEngine;

namespace Brain;

/// <summary>
/// Smoke test over every enemy in the game: from one recorded save, each enemy gets a fresh
/// process (one game world per process) that enters the battle and plays it out greedily.
/// Catches simulator gaps before a real battle does: a battle that never reaches the player's
/// turn, a turn flow that stalls, or exceptions thrown inside the game's turn coroutines (the
/// boss 902 bug was one: every layout then scored the same and the search still answered).
/// With --smoke-events the same runs over every bounty battle event instead (against the
/// recorded enemy): the enemy 102 + event 10006 bounty never started before the keyword fix.
/// </summary>
public static class Smoke
{
    public sealed class Result
    {
        public int Enemy;
        /// <summary>Bounty battle event (-1 = the recorded one).</summary>
        public int Event = -1;
        public string Name;
        public bool Started;
        public bool Ended;
        public bool Won;
        public int Rounds;
        public int Turns;
        public int PlayerHp;
        public int EnemyHp;
        public List<string> Problems = new();
        /// <summary>Errors raised after boot (battle entry and play), first line of each.</summary>
        public List<string> Errors = new();
        public int ErrorCount;
        public string Crash;
        public double Seconds;
        public string Status;
        /// <summary>--mergecheck: first state difference between fixed frames and merged idle frames (null = identical).</summary>
        public string MergeDiff;
        public long FramesFixed;
        public long FramesMerged;
    }

    /// <summary>Child process: one enemy, result as one "SMOKE {json}" line on stdout.</summary>
    public static int One(string recording, int enemy, string root, bool mergeCheck = false, int battleEvent = -1)
    {
        var sw = Stopwatch.StartNew();
        var r = new Result { Enemy = enemy, Event = battleEvent };
        try
        {
            string work = Path.Combine(root, "out", "smoke", Environment.ProcessId.ToString());
            Directory.CreateDirectory(work);
            BattleSession s = BattleSession.OpenPreBattle(recording, Path.Combine(root, "data", "export"),
                Path.Combine(root, "data", "resources_index.tsv"), work, _ => { }, enemy, battleEvent);
            int e0 = UnityRuntime.Errors.Count + UnityRuntime.ErrorsDropped;
            int listed = UnityRuntime.Errors.Count;
            r.Started = s.EnterBattleNow();
            r.Problems.AddRange(s.Problems);
            if (r.Started && mergeCheck)
            {
                r.MergeDiff = CompareMerging(s, r);
            }
            if (r.Started)
            {
                BattleOutcome o = s.PlayToEndGreedy(false, 40);
                r.Ended = o.Ended;
                r.Won = o.Won;
                r.Rounds = o.MaxRound;
                r.Turns = o.Turns;
                r.PlayerHp = o.PlayerHp;
                r.EnemyHp = o.EnemyHp;
            }
            r.ErrorCount = UnityRuntime.Errors.Count + UnityRuntime.ErrorsDropped - e0;
            r.Errors = UnityRuntime.Errors.Skip(listed).Select(FirstLines).Distinct().Take(6).ToList();
        }
        catch (Exception ex)
        {
            r.Crash = ex.GetType().Name + ": " + ex.Message;
        }
        r.Seconds = sw.Elapsed.TotalSeconds;
        Console.Out.Flush();
        Console.WriteLine("SMOKE " + Json.Write(r));
        return 0;
    }

    /// <summary>
    /// The same greedy battle with fixed frames and with merged idle frames: the full recorder state
    /// after every turn must be identical (frame counters aside). Returns the first difference.
    /// </summary>
    private static string CompareMerging(BattleSession s, Result r)
    {
        bool before = UnityRuntime.MergeIdleFrames;
        WorldSnapshot start = WorldSnapshot.Capture();
        var runs = new List<string>[2];
        for (int k = 0; k < 2; k++)
        {
            start.Restore();
            UnityRuntime.MergeIdleFrames = k == 1;
            int f0 = UnityRuntime.FrameCount;
            var fps = new List<string>();
            for (int turn = 0; turn < 40 && !s.IsBattleOver(); turn++)
            {
                s.GreedyTurn();
                fps.Add(BattleSession.Fingerprint());
            }
            if (k == 0)
            {
                r.FramesFixed = UnityRuntime.FrameCount - f0;
            }
            else
            {
                r.FramesMerged = UnityRuntime.FrameCount - f0;
            }
            runs[k] = fps;
        }
        start.Restore();
        UnityRuntime.MergeIdleFrames = before;
        if (runs[0].Count != runs[1].Count)
        {
            return $"turns {runs[0].Count} vs {runs[1].Count}";
        }
        for (int i = 0; i < runs[0].Count; i++)
        {
            var diffs = new List<string>();
            using var a = System.Text.Json.JsonDocument.Parse(runs[0][i]);
            using var b = System.Text.Json.JsonDocument.Parse(runs[1][i]);
            StateDiff.Compare(a.RootElement, b.RootElement, "", diffs, 3);
            if (diffs.Count > 0)
            {
                return $"turn {i + 1}: {string.Join("; ", diffs)}";
            }
        }
        return null;
    }

    /// <summary>Message plus the first game frame of the stack trace (enough to tell errors apart).</summary>
    private static string FirstLines(string e)
    {
        string[] lines = e.Split('\n');
        string at = lines.Skip(1).FirstOrDefault(l => l.Contains(" at ") && !l.Contains("UnityEngine.") && !l.Contains("DG.Tweening")) ?? "";
        at = at.Trim();
        int inAt = at.IndexOf(" in ", StringComparison.Ordinal);
        return (lines[0].Trim() + " | " + (inAt > 0 ? at.Substring(0, inAt) : at)).Trim(' ', '|');
    }

    /// <summary>
    /// Errors that are not a problem: the game's own UI code failing headless outside the battle
    /// logic (no Steam, empty treasure bar, missing music clip).
    /// </summary>
    private static readonly string[] Benign = { "Steamworks", "TreasurePanel.SetScaleRate", "AudioManager" };

    private static string Classify(Result r)
    {
        if (r.Crash != null)
        {
            return "FAIL crash";
        }
        if (!r.Started)
        {
            return "FAIL never reached the player's turn";
        }
        if (r.MergeDiff != null)
        {
            return "FAIL merged frames change the battle";
        }
        List<string> bad = r.Errors.Where(e => !Benign.Any(e.Contains)).ToList();
        if (bad.Any(e => e.StartsWith("coroutine", StringComparison.Ordinal)))
        {
            return "FAIL exception in a game coroutine";
        }
        if (!r.Ended && r.Turns < 40)
        {
            return "FAIL battle stalled";
        }
        if (bad.Count > 0)
        {
            return "WARN errors";
        }
        if (!r.Ended)
        {
            return "WARN unfinished after 40 turns";
        }
        return "OK";
    }

    /// <summary>
    /// Coordinator: every in-game enemy (or <paramref name="only"/>) in parallel child processes;
    /// with <paramref name="events"/> every in-game bounty battle event against the recorded enemy.
    /// </summary>
    public static async Task<int> All(string recording, string dll, int jobs, string root, IReadOnlyCollection<int> only, bool mergeCheck = false, bool events = false)
    {
        var total = Stopwatch.StartNew();
        // the lists come from a child too: booting the game here would install its frame
        // synchronization context on this thread and break the coordinator's awaits
        string kind = events ? "EVENT\t" : "ENEMY\t";
        List<(int Id, string Name)> listed = (await RunText(dll, $"\"{recording}\" --smoke-list", root))
            .Split('\n')
            .Where(l => l.StartsWith(kind, StringComparison.Ordinal))
            .Select(l => l.TrimEnd('\r').Split('\t'))
            .Select(f => (int.Parse(f[1]), f[2]))
            .Where(e => only == null || only.Count == 0 || only.Contains(e.Item1))
            .ToList();
        Dictionary<string, string> names = ChineseNames(root, events ? "battleevent" : "enemyship");
        // (enemy, event, name): an enemy with no event, or an event against the recorded enemy
        var enemies = listed.Select(e => (Id: events ? 0 : e.Id, Event: events ? e.Id : -1, Name: names.GetValueOrDefault(e.Name, e.Name))).ToList();
        Console.WriteLine($"smoke: {enemies.Count} {(events ? "battle events" : "enemies")}, {jobs} at a time");
        var gate = new SemaphoreSlim(jobs);
        var results = new List<Result>();
        await Task.WhenAll(enemies.Select(async e =>
        {
            await gate.WaitAsync();
            try
            {
                Result r = await RunChild(dll, recording, e.Id, e.Event, root, mergeCheck) ?? new Result { Enemy = e.Id, Event = e.Event, Crash = "no result (timeout or crash)" };
                r.Name = e.Name;
                r.Status = Classify(r);
                lock (results)
                {
                    results.Add(r);
                }
            }
            finally
            {
                gate.Release();
            }
        }));
        foreach (Result r in results.OrderBy(r => r.Status == "OK").ThenBy(r => r.Enemy))
        {
            if (r.Status == "OK")
            {
                continue;
            }
            Console.WriteLine($"  {r.Status,-38} #{(r.Event >= 0 ? "event " + r.Event : r.Enemy.ToString())} {r.Name}: rounds {r.Rounds}, player {r.PlayerHp}, enemy {r.EnemyHp}, {r.ErrorCount} errors{(r.Crash != null ? ", " + r.Crash : "")}");
            foreach (string p in r.Problems)
            {
                Console.WriteLine("      problem: " + p);
            }
            if (r.MergeDiff != null)
            {
                Console.WriteLine("      merged frames: " + r.MergeDiff);
            }
            foreach (string err in r.Errors.Where(x => !Benign.Any(x.Contains)))
            {
                Console.WriteLine("      error: " + err);
            }
        }
        int ok = results.Count(r => r.Status == "OK"), warn = results.Count(r => r.Status.StartsWith("WARN")), failed = results.Count(r => r.Status.StartsWith("FAIL"));
        int won = results.Count(r => r.Won);
        Console.WriteLine($"{results.Count} {(events ? "battle events" : "enemies")}: {ok} ok, {warn} warnings, {failed} failed; greedy wins {won}/{results.Count}; {total.Elapsed.TotalSeconds:F0}s");
        if (mergeCheck)
        {
            long fixedFrames = results.Sum(r => r.FramesFixed), merged = results.Sum(r => r.FramesMerged);
            Console.WriteLine($"merged idle frames: {results.Count(r => r.Started && r.MergeDiff == null)}/{results.Count} battles identical, frames {fixedFrames} -> {merged}");
        }
        Console.WriteLine(failed == 0 ? "SMOKE OK" : "SMOKE FAILED");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Child process: the in-game enemies and bounty battle events, one "ENEMY|EVENT<tab>id<tab>name" line each.</summary>
    public static int List(string recording, string root)
    {
        string work = Path.Combine(root, "out", "smoke", Environment.ProcessId.ToString());
        Directory.CreateDirectory(work);
        BattleSession.OpenPreBattle(recording, Path.Combine(root, "data", "export"), Path.Combine(root, "data", "resources_index.tsv"), work, _ => { });
        foreach (var e in Tool.Database.DataEnemyShipManager.Instance().GetDataList().Where(e => e.InGame).OrderBy(e => e.ID))
        {
            Console.WriteLine($"ENEMY\t{e.ID}\t{Translate(e.Name)}");
        }
        foreach (var e in Tool.Database.DataBattleEventManager.Instance().GetDataList().Where(e => e.InGame).OrderBy(e => e.ID))
        {
            Console.WriteLine($"EVENT\t{e.ID}\t{e.Name}");
        }
        return 0;
    }

    private static async Task<string> RunText(string dll, string args, string root)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["LONESTAR_SOLVER_ROOT"] = root;
        using Process p = Process.Start(psi);
        Task<string> err = p.StandardError.ReadToEndAsync();
        string text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return text;
    }

    /// <summary>EnemyName/902 → 蝉翼号 (or a battle event's name), from the exported Chinese text map.</summary>
    private static Dictionary<string, string> ChineseNames(string root, string table)
    {
        var map = new Dictionary<string, string>();
        string file = Path.Combine(root, "data", "export", "textmap", "chinesetextmap_" + table + ".txt");
        if (!File.Exists(file))
        {
            return map;
        }
        foreach (string line in File.ReadLines(file))
        {
            int semi = line.IndexOf(';');
            if (semi > 0)
            {
                map[line.Substring(0, semi)] = line.Substring(semi + 1).Trim().Trim('"');
            }
        }
        return map;
    }

    private static string Translate(string key)
    {
        try
        {
            string t = Lean.Localization.LeanLocalization.GetTranslationText(key, key, true);
            return string.IsNullOrEmpty(t) ? key : t;
        }
        catch
        {
            return key;
        }
    }

    private static async Task<Result> RunChild(string dll, string recording, int enemy, int battleEvent, string root, bool mergeCheck)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" \"{recording}\" --smoke-one --enemy {enemy}{(battleEvent >= 0 ? " --event " + battleEvent : "")}{(mergeCheck ? " --mergecheck" : "")}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["LONESTAR_SOLVER_ROOT"] = root;
        using Process p = Process.Start(psi);
        try
        {
            p.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // normal priority then
        }
        Task<string> err = p.StandardError.ReadToEndAsync();
        Task<string> outp = p.StandardOutput.ReadToEndAsync();
        Task exit = p.WaitForExitAsync();
        if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(180))) != exit)
        {
            try
            {
                p.Kill(true);
            }
            catch
            {
                // already gone
            }
            return new Result { Enemy = enemy, Event = battleEvent, Crash = "timeout (180 s)" };
        }
        string line = (await outp).Split('\n').LastOrDefault(l => l.StartsWith("SMOKE ", StringComparison.Ordinal));
        return line == null ? null : Json.Read<Result>(line.Substring(6));
    }
}
