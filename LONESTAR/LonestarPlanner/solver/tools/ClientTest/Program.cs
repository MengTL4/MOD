using System;
using System.IO;
using System.Threading;
using LitJson;
using LonestarPlanner.Mod;

// usage:
//   ClientTest <file with one reply JSON per line>                    parse replies like the mod does
//   ClientTest live <modDir> <dataRoot> <battle.jsonl> <prebattle.jsonl>
//        the mod's client against the solver bundled in <modDir>/Brain: start (copy to the cache,
//        version check), a solve, a hurried loadout on the same save, the same solve again (answered
//        from the loadout study's decisions); progress shown as in the panels
if (args.Length > 0 && args[0] == "live")
{
    return Live(args[1], args[2], args[3], args[4]);
}

int bad = 0;
foreach (string line in File.ReadAllLines(args[0]))
{
    if (!line.StartsWith("{")) continue;
    try
    {
        JsonData j = JsonMapper.ToObject(line);
        if (BrainPlan.Has(j, "progress")) continue;
        Print(j);
    }
    catch (Exception ex)
    {
        bad++;
        Console.WriteLine("FAIL: " + ex.GetType().Name + ": " + ex.Message);
    }
}
Console.WriteLine(bad == 0 ? "PARSE OK" : "PARSE FAILED");
return bad;

static void Print(JsonData j)
{
    bool ok = BrainPlan.Bool(j, "Ok");
    if (BrainPlan.Has(j, "Plan"))
    {
        BrainPlan p = BrainPlan.FromJson(j);
        Console.WriteLine($"solve ok={ok} plan={string.Join(";", p.Plan)} value={p.Value} win={p.Win} hp={p.PredictHp} fuel={p.PredictFuel} round={p.PredictRound} lines={p.Lines.Count} alts={p.Alternatives.Count} sec={p.Seconds:F1}");
    }
    if (BrainPlan.Has(j, "Entries"))
    {
        int n = 0;
        foreach (JsonData e in j["Entries"]) { int x = (int)e[0], y = (int)e[1], b = (int)e[7]; n++; }
        Console.WriteLine($"loadout ok={ok} entries={n} outcome={Outcome(j, "Outcome")} current={Outcome(j, "CurrentOutcome")} isCurrent={BrainPlan.Bool(j, "IsCurrent")} evaluated={BrainPlan.Int(j, "Evaluated")} sec={BrainPlan.Dbl(j, "Seconds"):F1}");
    }
}

static string Outcome(JsonData j, string key)
{
    if (!BrainPlan.Has(j, key)) return "-";
    JsonData o = j[key];
    return $"{BrainPlan.Str(o, "Kind")}(round {BrainPlan.Int(o, "Round")}, hp {BrainPlan.Int(o, "Hp")}, fuel {(BrainPlan.Has(o, "Fuel") ? BrainPlan.Int(o, "Fuel").ToString() : "-")}, dmg {BrainPlan.Int(o, "Damage")})";
}

static int Live(string modDir, string dataRoot, string battle, string prebattle)
{
    BrainService.ModPath = modDir;
    BrainService.DataRoot = dataRoot;
    Console.WriteLine("installed: " + BrainService.Installed + "  " + BrainService.ExpectedPath);
    int fails = 0;

    var c = new BrainClient();
    c.Solve(battle, 0);
    fails += Wait(c, "solve (no limit)", hurryAfter: 0) ? 0 : 1;
    string fresh = c.Result != null ? string.Join(";", c.Result.Plan) : null;

    c.Loadout(prebattle, 0, 60);
    fails += Wait(c, "loadout (60 s cap, hurried after 25 s)", hurryAfter: 25) ? 0 : 1;

    // the loadout study played this battle with the same layout: its first decision is reused
    c.Solve(battle, 0);
    fails += Wait(c, "solve after the loadout study (same state)", hurryAfter: 0) ? 0 : 1;
    string reused = c.Result != null ? string.Join(";", c.Result.Plan) : null;
    bool hit = c.Elapsed < 3 && reused == fresh;
    Console.WriteLine(hit ? $"  REUSED: same plan in {c.Elapsed:F1}s" : $"  NOT REUSED ({c.Elapsed:F1}s, plan {(reused == fresh ? "same" : "different: " + reused)})");
    fails += hit ? 0 : 1;

    Console.WriteLine(fails == 0 ? "LIVE OK" : "LIVE FAILED");
    return fails;
}

static bool Wait(BrainClient c, string label, double hurryAfter)
{
    Console.WriteLine("== " + label);
    string last = null;
    bool hurried = false;
    while (c.Busy)
    {
        string now = $"{c.State} {c.ProgressText} frac={c.Fraction:F2}";
        if (now != last)
        {
            Console.WriteLine($"  {c.Elapsed,5:F1}s  {now}");
            last = now;
        }
        if (hurryAfter > 0 && !hurried && c.Elapsed > hurryAfter && c.State == BrainClient.ClientState.Solving)
        {
            hurried = true;
            c.Hurry();
        }
        Thread.Sleep(100);
    }
    if (c.State != BrainClient.ClientState.Ready)
    {
        Console.WriteLine($"  FAILED: {c.ErrorText}");
        return false;
    }
    Console.WriteLine($"  done in {c.Elapsed:F1}s");
    Print(c.Reply);
    return true;
}
