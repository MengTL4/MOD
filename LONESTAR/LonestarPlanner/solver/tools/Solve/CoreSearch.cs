// Pre-battle energy core optimizer: compares candidate power/colour deck compositions against the
// same enemy and layout by simulating the whole battle for each, then ranks the outcomes.
//
// The core is normally fixed per ship and only changed by run events/rewards; this tool answers
// "if I get to choose a core, which composition is best for this layout and enemy?".

using System.Diagnostics;
using System.Text.Json;

internal static class CoreSearch
{
    public static int Run(string recording, string root, int jobs, string assemblyPath)
    {
        string dotnet = DotnetPath();
        Console.Error.WriteLine($"[core] recording={Path.GetFileName(recording)}");

        JsonElement info = RunWorker(dotnet, assemblyPath, recording, new[] { "--layout-info", "--json", "--quiet" });
        if (info.ValueKind != JsonValueKind.Object)
        {
            Console.Error.WriteLine("[core] could not read layout info");
            return 1;
        }
        int[] currentPowers = info.GetProperty("corePowers").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        int[] currentColors = info.GetProperty("coreColors").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        int powerTotal = currentPowers.Sum();
        int colorTotal = currentColors.Sum();
        Console.Error.WriteLine($"[core] current: powers=[{string.Join(",", currentPowers)}] colors=[{string.Join(",", currentColors)}]");

        var candidates = new List<(string Label, int[] Powers, int[] Colors)>();
        Add(candidates, "current", currentPowers, currentColors);
        foreach ((string label, int[] p) in PowerVariants(powerTotal))
        {
            Add(candidates, label, p, currentColors);
        }
        foreach ((string label, int[] c) in ColorVariants(colorTotal))
        {
            Add(candidates, label, currentPowers, c);
        }
        // Cross the best-looking power and colour variants as well.
        if (powerTotal > 0 && colorTotal > 0)
        {
            Add(candidates, "high + balanced-colors", Skew(powerTotal, high: true), Balanced(colorTotal, 3));
            Add(candidates, "high + white-heavy", Skew(powerTotal, high: true), ColorSpread(colorTotal, 0));
            Add(candidates, "high + blue-heavy", Skew(powerTotal, high: true), ColorSpread(colorTotal, 1));
        }
        var unique = candidates
            .GroupBy(c => string.Join(",", c.Powers) + "|" + string.Join(",", c.Colors))
            .Select(g => g.First())
            .ToList();
        Console.Error.WriteLine($"[core] candidates: {unique.Count}");

        var results = new CoreResult[unique.Count];
        int done = 0;
        Parallel.For(0, unique.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            results[i] = Evaluate(dotnet, assemblyPath, recording, unique[i]);
            int n = Interlocked.Increment(ref done);
            if (n % 8 == 0)
            {
                Console.Error.Write($"\r[core] evaluated {n}/{unique.Count}   ");
            }
        });
        Console.Error.WriteLine();

        var ranked = results.Where(r => r != null && r.Ok).OrderByDescending(r => r.Score).ToList();
        Console.WriteLine($"=== LONESTAR core optimizer, {Path.GetFileName(recording)} ===");
        Console.WriteLine($"current core: powers=[{string.Join(",", currentPowers)}] colors=[{string.Join(",", currentColors)}]");
        Console.WriteLine();
        int rank = 1;
        foreach (CoreResult r in ranked.Take(12))
        {
            string mark = r.Label == "current" ? "  <- current" : "";
            Console.WriteLine($"  #{rank++,2} score={r.Score,8:F1} won={r.Won,-5} turns={r.Turns} hpLeft={r.PlayerHp,2}  {r.Label,-22} powers=[{string.Join(",", r.Powers)}] colors=[{string.Join(",", r.Colors)}]{mark}");
        }
        if (ranked.Count > 0)
        {
            CoreResult best = ranked[0];
            Console.WriteLine();
            Console.WriteLine($"RECOMMENDED CORE: powers=[{string.Join(",", best.Powers)}] colors=[{string.Join(",", best.Colors)}]");
            Console.WriteLine($"  predicted: {(best.Won ? "VICTORY" : "DEFEAT")} in {best.Turns} turns, {best.PlayerHp} HP left  ({best.Score:F1})");
            CoreResult cur = ranked.FirstOrDefault(r => r.Label == "current");
            if (cur != null && cur != best)
            {
                Console.WriteLine($"  current:  {(cur.Won ? "VICTORY" : "DEFEAT")} in {cur.Turns} turns, {cur.PlayerHp} HP left  ({cur.Score:F1})");
            }
        }

        string outDir = Path.Combine(root, "out", "solve");
        Directory.CreateDirectory(outDir);
        string outFile = Path.Combine(outDir, $"core_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(outFile, JsonSerializer.Serialize(new
        {
            recording,
            currentPowers,
            currentColors,
            top = ranked.Take(30).Select(r => new
            {
                label = r.Label,
                powers = r.Powers,
                colors = r.Colors,
                score = r.Score,
                won = r.Won,
                turns = r.Turns,
                playerHp = r.PlayerHp,
                enemyHp = r.EnemyHp,
            }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.Error.WriteLine($"[core] wrote {outFile}");
        return 0;
    }

    private static void Add(List<(string, int[], int[])> list, string label, int[] powers, int[] colors) =>
        list.Add((label, powers.ToArray(), colors.ToArray()));

    private static IEnumerable<(string, int[])> PowerVariants(int total)
    {
        if (total <= 0)
        {
            yield break;
        }
        yield return ("low-power", Skew(total, high: false));
        yield return ("high-power", Skew(total, high: true));
        var all3 = new int[5];
        all3[2] = total;
        yield return ("all-3", all3);
        var uniform = new int[5];
        for (int i = 0; i < total; i++) uniform[i % 5]++;
        yield return ("uniform", uniform);
    }

    private static IEnumerable<(string, int[])> ColorVariants(int total)
    {
        if (total <= 0)
        {
            yield break;
        }
        yield return ("white-heavy", ColorSpread(total, 0));
        yield return ("blue-heavy", ColorSpread(total, 1));
        yield return ("purple-heavy", ColorSpread(total, 2));
        yield return ("balanced-colors", Balanced(total, 3));
    }

    private static int[] Skew(int total, bool high)
    {
        var c = new int[5];
        int strong = (int)Math.Ceiling(total * 0.6);
        int rest = total - strong;
        if (high)
        {
            c[4] = strong / 2;
            c[3] = strong - strong / 2;
            c[2] = rest;
        }
        else
        {
            c[0] = strong / 2;
            c[1] = strong - strong / 2;
            c[2] = rest;
        }
        return c;
    }
    private static int[] ColorSpread(int total, int heavy)
    {
        var c = new int[3];
        c[heavy] = total;
        return c;
    }

    private static int[] Balanced(int total, int buckets)
    {
        var c = new int[buckets];
        for (int i = 0; i < total; i++)
        {
            c[i % buckets]++;
        }
        return c;
    }

    private static int[] Fill(int size, int perSlot, int remainder, bool skewLow = false, bool exact = false, int value = 0)
    {
        var c = new int[size];
        for (int i = 0; i < size; i++)
        {
            c[i] = perSlot;
        }
        for (int i = 0; i < remainder; i++)
        {
            c[skewLow ? i : size - 1 - i]++;
        }
        return c;
    }

    private static CoreResult Evaluate(string dotnet, string assemblyPath, string recording, (string Label, int[] Powers, int[] Colors) candidate)
    {
        var result = new CoreResult
        {
            Label = candidate.Label,
            Powers = candidate.Powers,
            Colors = candidate.Colors,
        };
        string core = string.Join(",", candidate.Powers) + ";" + string.Join(",", candidate.Colors);
        JsonElement r = RunWorker(dotnet, assemblyPath, recording, new[] { "--core", core, "--greedy-play", "--json", "--quiet" });
        if (r.ValueKind != JsonValueKind.Object)
        {
            return result;
        }
        result.Ok = true;
        result.Won = r.GetProperty("won").GetBoolean();
        result.Ended = r.GetProperty("ended").GetBoolean();
        result.Turns = r.GetProperty("turns").GetInt32();
        result.PlayerHp = r.GetProperty("playerHp").GetInt32();
        result.EnemyHp = r.GetProperty("enemyHp").GetInt32();
        result.Score = (result.Won ? 1000.0 : 0) + result.PlayerHp * 10.0 - result.Turns * 0.5;
        return result;
    }

    private static JsonElement RunWorker(string dotnet, string assemblyPath, string recording, string[] extra, int timeoutMs = 180000)
    {
        var psi = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(assemblyPath);
        psi.ArgumentList.Add(recording);
        foreach (string e in extra)
        {
            psi.ArgumentList.Add(e);
        }
        using var p = Process.Start(psi);
        Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        Task<string> errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return default;
        }
        string stdout = outTask.Result;
        _ = errTask.Result;
        foreach (string line in stdout.Split('\n').Reverse())
        {
            string t = line.Trim();
            if (t.StartsWith("{"))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(t);
                    return doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                }
            }
        }
        return default;
    }

    private static string DotnetPath()
    {
        string p = Environment.ProcessPath;
        if (p != null && Path.GetFileName(p).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return p;
        }
        return "dotnet";
    }

    private sealed class CoreResult
    {
        public string Label;
        public int[] Powers;
        public int[] Colors;
        public bool Ok;
        public bool Won;
        public bool Ended;
        public int Turns;
        public int PlayerHp;
        public int EnemyHp;
        public double Score;
    }
}

