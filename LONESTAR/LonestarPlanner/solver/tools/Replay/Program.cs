// Replays a recorded battle in the headless engine and diffs the result against the recording.
//
// Usage: Replay <battle.jsonl> [--max-diffs N]
// Writes the headless recording under out/replay/<name>/ and prints per-snapshot differences.

using System.Text.Json;
using Lonestar.Engine;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

if (args[0] == "--asset")
{
    // Diagnostic: Replay --asset <Resources path> — load one exported asset and print its fields.
    var store = new AssetStore(Path.Combine(root, "data", "export"), Path.Combine(root, "data", "resources_index.tsv"));
    UnityEngine.Resources.Provider = store.Load;
    UnityEngine.Object asset = store.Load(args[1], typeof(UnityEngine.Object));
    Console.WriteLine(asset == null ? "null" : asset.GetType().FullName + " " + asset.name);
    if (asset != null)
    {
        for (Type t = asset.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            {
                object v = f.GetValue(asset);
                string s = v is System.Collections.ICollection c ? $"{v.GetType().Name}[{c.Count}]" : v?.ToString() ?? "null";
                Console.WriteLine($"  {t.Name}.{f.Name} = {s}");
            }
        }
    }
    return 0;
}

string recordingPath = args[0];
int maxDiffs = args.Contains("--max-diffs") ? int.Parse(args[Array.IndexOf(args, "--max-diffs") + 1]) : 12;

var original = new Recording(recordingPath);
string workDir = Path.Combine(root, "out", "replay", Path.GetFileNameWithoutExtension(recordingPath));
Directory.CreateDirectory(workDir);
var gameLog = new StreamWriter(Path.Combine(workDir, "game_debug.log"));

var sw = System.Diagnostics.Stopwatch.StartNew();
var driver = new ReplayDriver();
driver.Run(original, Path.Combine(root, "data", "export"), Path.Combine(root, "data", "resources_index.tsv"), workDir, gameLog.WriteLine);
gameLog.Flush();
Console.WriteLine($"Replayed in {sw.Elapsed.TotalSeconds:F1}s, {UnityEngine.UnityRuntime.FrameCount} frames, virtual {UnityEngine.UnityRuntime.Time:F0}s");

File.WriteAllLines(Path.Combine(workDir, "errors.txt"),
    HeadlessGame.BootErrors.Concat(driver.Problems).Concat(UnityEngine.UnityRuntime.Errors).Select(e => e + Environment.NewLine + "----"));
Report("Boot errors", HeadlessGame.BootErrors);
Report("Replay problems", driver.Problems);
Report("Runtime errors (swallowed like Unity would)", UnityEngine.UnityRuntime.Errors);

if (driver.OutputLog == null || !File.Exists(driver.OutputLog))
{
    Console.WriteLine("No headless battle recording was produced.");
    return 1;
}
var replay = new Recording(driver.OutputLog);
Console.WriteLine($"Headless recording: {driver.OutputLog}");

var a = original.Snaps.ToList();
var b = replay.Snaps.ToList();
Console.WriteLine($"Snapshots: recorded {a.Count}, headless {b.Count}");
int identical = 0;
for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
{
    string ta = a[i].GetProperty("tag").GetString(), tb = b[i].GetProperty("tag").GetString();
    var diffs = new List<string>();
    // After-action snapshots catch the per-frame line recalculation (BattleManager.LateUpdate) mid-way:
    // in the game the mouse hover preview already ran it, headless it runs one frame later.
    JsonDiff.Compare(a[i], b[i], "", diffs, maxDiffs, ta.StartsWith("after_") && ta != "after_damage" ? JsonDiff.TransientKeys : null);
    if (ta != tb)
    {
        Console.WriteLine($"[{i}] TAG MISMATCH recorded={ta} headless={tb}");
        break;
    }
    if (diffs.Count == 0)
    {
        identical++;
        continue;
    }
    Console.WriteLine($"[{i}] {ta} (round {a[i].GetProperty("round")}): {diffs.Count}{(diffs.Count >= maxDiffs ? "+" : "")} differences");
    foreach (string d in diffs)
    {
        Console.WriteLine("      " + d);
    }
}
Console.WriteLine($"Identical snapshots: {identical}/{Math.Min(a.Count, b.Count)}");
return 0;

static void Report(string title, List<string> items)
{
    if (items.Count == 0)
    {
        return;
    }
    Console.WriteLine($"{title}: {items.Count}");
    foreach (string s in items.Take(8))
    {
        Console.WriteLine("  - " + s.Split('\n')[0]);
    }
}

static class JsonDiff
{
    /// <summary>
    /// Never comparable: record numbering, frame counters (headless frames are not the game's) and
    /// activeSprite (a skill's sprite reference, null in the game's recordings).
    /// </summary>
    private static readonly HashSet<string> Ignored = new() { "n", "lastFrameExecuted", "activeSprite" };

    public static readonly HashSet<string> TransientKeys = new() { "lines", "previewing", "container" };

    public static void Compare(JsonElement x, JsonElement y, string path, List<string> diffs, int max, HashSet<string> alsoIgnored = null)
    {
        if (diffs.Count >= max)
        {
            return;
        }
        if (x.ValueKind != y.ValueKind)
        {
            diffs.Add($"{path}: {Short(x)} != {Short(y)}");
            return;
        }
        switch (x.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty p in x.EnumerateObject())
                {
                    if (Ignored.Contains(p.Name) || alsoIgnored?.Contains(p.Name) == true)
                    {
                        continue;
                    }
                    if (!y.TryGetProperty(p.Name, out JsonElement yv))
                    {
                        diffs.Add($"{path}.{p.Name}: missing in headless");
                        continue;
                    }
                    Compare(p.Value, yv, $"{path}.{p.Name}", diffs, max, alsoIgnored);
                }
                foreach (JsonProperty p in y.EnumerateObject())
                {
                    if (!Ignored.Contains(p.Name) && alsoIgnored?.Contains(p.Name) != true && !x.TryGetProperty(p.Name, out _))
                    {
                        diffs.Add($"{path}.{p.Name}: only in headless");
                    }
                }
                return;
            case JsonValueKind.Array:
                int lx = x.GetArrayLength(), ly = y.GetArrayLength();
                if (lx != ly)
                {
                    diffs.Add($"{path}: length {lx} != {ly}");
                }
                for (int i = 0; i < Math.Min(lx, ly); i++)
                {
                    Compare(x[i], y[i], $"{path}[{i}]", diffs, max, alsoIgnored);
                }
                return;
            default:
                if (x.GetRawText() != y.GetRawText())
                {
                    diffs.Add($"{path}: {Short(x)} != {Short(y)}");
                }
                return;
        }
    }

    private static string Short(JsonElement e)
    {
        string s = e.GetRawText();
        return s.Length > 80 ? s.Substring(0, 77) + "..." : s;
    }
}
