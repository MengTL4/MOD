// Pre-battle layout optimizer: searches which unit goes on which ship cell by simulating the whole
// battle for each candidate layout with the greedy policy, then ranks the outcomes.
//
// The layout is applied inside each worker process right after the save is restored and before the
// battle starts, which is the same state the game's refit screen leaves behind.

using System.Diagnostics;
using System.Text.Json;

internal static class LayoutSearch
{
    public static int Run(string recording, string root, int jobs, int refineTop, string assemblyPath)
    {
        string dotnet = DotnetPath();
        Console.Error.WriteLine($"[layout] recording={Path.GetFileName(recording)} jobs={jobs} refineTop={refineTop}");

        JsonElement info = RunWorker(dotnet, assemblyPath, recording, new[] { "--layout-info", "--json", "--quiet" });
        if (info.ValueKind != JsonValueKind.Object)
        {
            Console.Error.WriteLine("[layout] could not read layout info");
            return 1;
        }
        var cells = info.GetProperty("layout").EnumerateArray()
            .Select(c => new Cell { X = c.GetProperty("X").GetInt32(), Y = c.GetProperty("Y").GetInt32(), Status = c.GetProperty("Status").GetInt32() })
            .Where(c => c.Status != 0) // 0 == ShipCellStatus.None
            .OrderBy(c => c.Y).ThenBy(c => c.X)
            .ToList();
        var units = info.GetProperty("available").EnumerateArray()
            .Select(u => new Unit
            {
                Index = u.GetProperty("Index").GetInt32(),
                Id = u.GetProperty("Id").GetInt32(),
                Lv = u.GetProperty("Lv").GetInt32(),
                EquipLimit = u.GetProperty("EquipLimit").GetInt32(),
                OnShip = u.GetProperty("OnShip").GetBoolean(),
                X = u.GetProperty("X").GetInt32(),
                Y = u.GetProperty("Y").GetInt32(),
            })
            .ToList();
        int loadLimit = info.GetProperty("loadLimit").GetInt32();
        Console.Error.WriteLine($"[layout] {cells.Count} usable cells, {units.Count} units, loadLimit {loadLimit}");
        Console.Error.WriteLine($"[layout] baseline: {string.Join(" ", units.Select(u => $"[{u.Index}]={u.Id}@{(u.OnShip ? $"{u.X},{u.Y}" : "bag")}"))}");

        // Unit count is bounded by cells and by the load limit (every unit in the pool costs >= 0).
        int k = Math.Min(units.Count, cells.Count);
        int limitSum = 0;
        var chosen = new List<Unit>();
        foreach (Unit u in units)
        {
            if (chosen.Count >= k || limitSum + u.EquipLimit > loadLimit)
            {
                continue;
            }
            chosen.Add(u);
            limitSum += u.EquipLimit;
        }
        Console.Error.WriteLine($"[layout] placing {chosen.Count} units (equip {limitSum}/{loadLimit})");

        var rows = cells.GroupBy(c => c.Y).OrderBy(g => g.Key).Select(g => new Row { Y = g.Key, Cells = g.OrderBy(c => c.X).ToList() }).ToList();
        List<string> phase1 = RowAssignments(chosen, rows, cells);
        var baseline = Baseline(chosen);
        if (baseline != null && !phase1.Contains(baseline))
        {
            phase1.Add(baseline);
        }
        Console.Error.WriteLine($"[layout] phase1 candidates: {phase1.Count}");

        var results = EvaluateAll(dotnet, assemblyPath, recording, phase1, jobs);
        var ranked = results.Where(r => r.Ok).OrderByDescending(r => r.Score).ToList();
        PrintRanking("phase1", ranked, baseline);

        if (refineTop > 0 && ranked.Count > 0)
        {
            var variants = new List<string>();
            foreach (LayoutResult top in ranked.Take(refineTop))
            {
                variants.AddRange(ColumnVariants(top.Layout, chosen, rows));
            }
            variants = variants.Distinct().Where(v => !phase1.Contains(v)).ToList();
            Console.Error.WriteLine($"[layout] phase2 column variants: {variants.Count}");
            var more = EvaluateAll(dotnet, assemblyPath, recording, variants, jobs);
            results.AddRange(more);
            ranked = results.Where(r => r.Ok).OrderByDescending(r => r.Score).ToList();
        }

        List<LayoutResult> final = results.Where(r => r.Ok).OrderByDescending(r => r.Score).ToList();
        Console.WriteLine($"=== LONESTAR layout optimizer, {Path.GetFileName(recording)} ===");
        Console.WriteLine($"units: {string.Join(" ", chosen.Select(u => $"{u.Id}/L{u.Lv}"))}  cells: {cells.Count}  loadLimit: {loadLimit}");
        Console.WriteLine();
        int rank = 1;
        foreach (LayoutResult r in final.Take(15))
        {
            string mark = r.Layout == baseline ? "  <- baseline layout" : "";
            Console.WriteLine($"  #{rank++,2} score={r.Score,8:F1} won={r.Won,-5} turns={r.Turns} hpLeft={r.PlayerHp,2} hpLost={r.PlayerHpLost,2}  {Describe(r.Layout)}{mark}");
        }
        if (final.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"RECOMMENDED LAYOUT: {final[0].Layout}");
            Console.WriteLine($"  predicted: {(final[0].Won ? "VICTORY" : "DEFEAT")} in {final[0].Turns} turns, {final[0].PlayerHp} HP left");
            LayoutResult? baseLine = final.FirstOrDefault(r => r.Layout == baseline);
            if (baseLine != null && final[0].Layout != baseline)
            {
                Console.WriteLine($"  baseline:  {(baseLine.Won ? "VICTORY" : "DEFEAT")} in {baseLine.Turns} turns, {baseLine.PlayerHp} HP left");
            }
        }

        string outDir = Path.Combine(root, "out", "solve");
        Directory.CreateDirectory(outDir);
        string outFile = Path.Combine(outDir, $"layout_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(outFile, JsonSerializer.Serialize(new
        {
            recording,
            baseline,
            units = chosen.Select(u => new { u.Index, u.Id, u.Lv, u.EquipLimit }),
            cells = cells.Select(c => new { c.X, c.Y }),
            candidates = results.Count,
            top = final.Take(30).Select(r => new
            {
                layout = r.Layout,
                score = r.Score,
                won = r.Won,
                turns = r.Turns,
                playerHp = r.PlayerHp,
                playerHpLost = r.PlayerHpLost,
                enemyHp = r.EnemyHp,
                seconds = r.Seconds,
            }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.Error.WriteLine($"[layout] wrote {outFile}");
        return 0;
    }

    private static void PrintRanking(string phase, List<LayoutResult> ranked, string baseline)
    {
        Console.Error.WriteLine($"[layout] {phase}: {ranked.Count} evaluated");
        int i = 1;
        foreach (LayoutResult r in ranked.Take(8))
        {
            Console.Error.WriteLine($"  #{i++} {r.Score,8:F1} won={r.Won} turns={r.Turns} hp={r.PlayerHp} {r.Layout}{(r.Layout == baseline ? " (baseline)" : "")}");
        }
    }

    // ---- candidate generation -------------------------------------------------------------------

    private static List<string> RowAssignments(List<Unit> units, List<Row> rows, List<Cell> cells)
    {
        var result = new List<string>();
        var rowOf = new int[units.Count];
        var counts = new int[rows.Count];

        void Dfs(int unit)
        {
            if (unit == units.Count)
            {
                result.Add(BuildFromRows(units, rows, rowOf));
                return;
            }
            for (int r = 0; r < rows.Count; r++)
            {
                if (counts[r] >= rows[r].Cells.Count)
                {
                    continue;
                }
                counts[r]++;
                rowOf[unit] = r;
                Dfs(unit + 1);
                counts[r]--;
            }
        }
        Dfs(0);
        return result.Distinct().ToList();
    }

    private static string BuildFromRows(List<Unit> units, List<Row> rows, int[] rowOf)
    {
        var used = rows.Select(r => 0).ToArray();
        var entries = new List<string>();
        for (int i = 0; i < units.Count; i++)
        {
            Row row = rows[rowOf[i]];
            Cell cell = row.Cells[used[rowOf[i]]];
            used[rowOf[i]]++;
            entries.Add($"{cell.X},{cell.Y},{units[i].Index}");
        }
        return string.Join(";", entries);
    }

    private static string Baseline(List<Unit> units)
    {
        var placed = units.Where(u => u.OnShip).OrderBy(u => u.Y).ThenBy(u => u.X).ToList();
        if (placed.Count == 0)
        {
            return null;
        }
        return string.Join(";", placed.Select(u => $"{u.X},{u.Y},{u.Index}"));
    }

    /// <summary>Permute the units of each row across that row's cells, keeping the same row split.</summary>
    private static List<string> ColumnVariants(string layout, List<Unit> units, List<Row> rows)
    {
        var byRow = new Dictionary<int, List<Unit>>();
        foreach (string part in layout.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = part.Split(',');
            int x = int.Parse(f[0]), y = int.Parse(f[1]), u = int.Parse(f[2]);
            if (!byRow.TryGetValue(y, out List<Unit> list))
            {
                byRow[y] = list = new List<Unit>();
            }
            list.Add(units.First(m => m.Index == u));
        }
        var rowOrders = new List<List<int[]>>();
        var rowKeys = byRow.Keys.OrderBy(y => y).ToList();
        foreach (int y in rowKeys)
        {
            Row row = rows.First(r => r.Y == y);
            rowOrders.Add(Permutations(byRow[y].Count, row.Cells.Count));
        }
        var variants = new List<string>();
        var chosen = new int[rowKeys.Count];
        void Dfs(int row)
        {
            if (variants.Count >= 24)
            {
                return;
            }
            if (row == rowKeys.Count)
            {
                var entries = new List<string>();
                for (int r = 0; r < rowKeys.Count; r++)
                {
                    List<Unit> list = byRow[rowKeys[r]];
                    int[] perm = rowOrders[r][chosen[r]];
                    for (int i = 0; i < list.Count; i++)
                    {
                        Cell cell = rows.First(x => x.Y == rowKeys[r]).Cells[perm[i]];
                        entries.Add($"{cell.X},{cell.Y},{list[i].Index}");
                    }
                }
                variants.Add(string.Join(";", entries));
                return;
            }
            for (int p = 0; p < rowOrders[row].Count; p++)
            {
                chosen[row] = p;
                Dfs(row + 1);
            }
        }
        Dfs(0);
        variants.Remove(layout);
        return variants;
    }

    private static List<int[]> Permutations(int count, int slots)
    {
        var result = new List<int[]>();
        var current = new List<int>();
        void Dfs()
        {
            if (result.Count >= 6)
            {
                return;
            }
            if (current.Count == count)
            {
                result.Add(current.ToArray());
                return;
            }
            for (int i = 0; i < slots; i++)
            {
                if (current.Contains(i))
                {
                    continue;
                }
                current.Add(i);
                Dfs();
                current.RemoveAt(current.Count - 1);
            }
        }
        Dfs();
        return result;
    }

    // ---- evaluation -----------------------------------------------------------------------------

    private static List<LayoutResult> EvaluateAll(string dotnet, string assemblyPath, string recording, List<string> layouts, int jobs)
    {
        var results = new LayoutResult[layouts.Count];
        int done = 0;
        Parallel.For(0, layouts.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            results[i] = Evaluate(dotnet, assemblyPath, recording, layouts[i]);
            int n = Interlocked.Increment(ref done);
            if (n % 16 == 0)
            {
                Console.Error.Write($"\r[layout] evaluated {n}/{layouts.Count}   ");
            }
        });
        Console.Error.WriteLine();
        return results.Where(r => r != null).ToList();
    }

    private static LayoutResult Evaluate(string dotnet, string assemblyPath, string recording, string layout)
    {
        JsonElement r = RunWorker(dotnet, assemblyPath, recording, new[] { "--layout", layout, "--greedy-play", "--json", "--quiet" });
        var result = new LayoutResult { Layout = layout };
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
        result.PlayerHpLost = r.GetProperty("playerHpLost").GetInt32();
        result.Seconds = r.GetProperty("seconds").GetDouble();
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

    private static string Describe(string layout)
    {
        var parts = layout.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split(','))
            .Select(f => $"({f[0]},{f[1]})u{f[2]}")
            .OrderBy(s => s);
        return string.Join(" ", parts);
    }

    private sealed class Cell
    {
        public int X;
        public int Y;
        public int Status;
    }

    private sealed class Row
    {
        public int Y;
        public List<Cell> Cells = new List<Cell>();
    }

    private sealed class Unit
    {
        public int Index;
        public int Id;
        public int Lv;
        public int EquipLimit;
        public bool OnShip;
        public int X;
        public int Y;
    }

    private sealed class LayoutResult
    {
        public string Layout;
        public bool Ok;
        public bool Won;
        public bool Ended;
        public int Turns;
        public int PlayerHp;
        public int EnemyHp;
        public int PlayerHpLost;
        public double Seconds;
        public double Score;
    }
}
