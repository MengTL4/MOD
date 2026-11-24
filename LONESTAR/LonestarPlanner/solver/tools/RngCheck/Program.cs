// Offline verifier for phase 0: replays every random event the SolverLogger mod recorded and checks
// that Lonestar.Rng reproduces it bit-exactly.
//
// Usage: RngCheck [path-to-session.jsonl]   (defaults to the newest log of the deployed mod)

using System.Text.Json;
using Lonestar.Rng;

string path = args.Length > 0 ? args[0] : LatestLog();
Console.WriteLine($"Log: {path}");
List<JsonElement> recs = ReadRecords(path);
Console.WriteLine($"Records: {recs.Count}");
foreach (var g in recs.GroupBy(T).OrderBy(g => g.Key))
{
    Console.WriteLine($"  {g.Key,-20} {g.Count()}");
}

var report = new Report();
CheckHash();
CheckInitState();
CheckUnityRandom();
CheckGameSeed();
CheckStreams();
CheckRandomRange();
CheckShuffles();
report.Print();
return report.Failed ? 1 : 0;

// ---------------------------------------------------------------------------------------------

void CheckHash()
{
    var s = report.Section("Mono string.GetHashCode");
    foreach (var r in Of("selftest_hash"))
    {
        string str = r.GetProperty("s").GetString();
        int expected = r.GetProperty("h").GetInt32();
        s.Check(MonoHash.Hash(str) == expected, $"\"{str}\": game={expected} ours={MonoHash.Hash(str)}");
    }
}

void CheckInitState()
{
    var s = report.Section("Random.InitState words");
    foreach (var r in Of("selftest_state"))
    {
        int seed = r.GetProperty("seed").GetInt32();
        uint[] game = r.GetProperty("s").EnumerateArray().Select(e => unchecked((uint)e.GetInt32())).ToArray();
        var u = new UnityRandom(seed);
        uint[] ours = { u.S0, u.S1, u.S2, u.S3 };
        s.Check(game.SequenceEqual(ours), $"seed {seed}: game=[{string.Join(",", game)}] ours=[{string.Join(",", ours)}]");
    }
}

void CheckUnityRandom()
{
    var tests = Of("selftest_rng").ToList();
    if (tests.Count == 0)
    {
        return;
    }

    // Integer ranges do not depend on the float conversion.
    var si = report.Section("Random.Range(int,int)");
    foreach (var r in tests.Where(r => r.GetProperty("kind").GetString().StartsWith("range_i_")))
    {
        string kind = r.GetProperty("kind").GetString();
        int seed = r.GetProperty("seed").GetInt32();
        string[] parts = kind.Substring("range_i_".Length).Split('_');
        int min = int.Parse(parts[0]), max = int.Parse(parts[1]);
        int[] game = Ints(r.GetProperty("vals"));
        var u = new UnityRandom(seed);
        int[] ours = game.Select(_ => u.Range(min, max)).ToArray();
        si.Check(game.SequenceEqual(ours), $"seed {seed} {kind}: game=[{string.Join(",", game)}] ours=[{string.Join(",", ours)}]");
    }

    // Find the float conversion that explains every float sample.
    var candidates = new List<(FloatMode fm, LerpMode lm, int bad)>();
    foreach (FloatMode fm in Enum.GetValues<FloatMode>())
    {
        foreach (LerpMode lm in Enum.GetValues<LerpMode>())
        {
            int bad = 0;
            foreach (var r in tests)
            {
                if (!FloatSampleMatches(r, fm, lm))
                {
                    bad++;
                }
            }
            candidates.Add((fm, lm, bad));
        }
    }
    var best = candidates.OrderBy(c => c.bad).First();
    var sf = report.Section("Random.value / Range(float,float)");
    foreach (var c in candidates)
    {
        sf.Info($"{c.fm}/{c.lm}: {c.bad} mismatching sample sets");
    }
    sf.Check(best.bad == 0, $"no float mode explains all samples (best {best.fm}/{best.lm} has {best.bad} mismatches)");
    sf.Info($"selected {best.fm}/{best.lm}");
    GameRng.FloatMode = best.fm;
    GameRng.LerpMode = best.lm;
}

bool FloatSampleMatches(JsonElement r, FloatMode fm, LerpMode lm)
{
    string kind = r.GetProperty("kind").GetString();
    var u = new UnityRandom(r.GetProperty("seed").GetInt32()) { FloatMode = fm, LerpMode = lm };
    switch (kind)
    {
        case "value":
            return Ints(r.GetProperty("bits")).All(b => b == Bits(u.Value()));
        case "range_f_0_1":
            return Ints(r.GetProperty("bits")).All(b => b == Bits(u.Range(0f, 1f)));
        case "range_f_-2.5_7.25":
            return Ints(r.GetProperty("bits")).All(b => b == Bits(u.Range(-2.5f, 7.25f)));
        case "mixed_i10_value_f01":
            int[] v = Ints(r.GetProperty("vals"));
            for (int i = 0; i < v.Length; i += 3)
            {
                if (v[i] != u.Range(0, 10) || v[i + 1] != Bits(u.Value()) || v[i + 2] != Bits(u.Range(0f, 1f)))
                {
                    return false;
                }
            }
            return true;
        default:
            return true;
    }
}

void CheckGameSeed()
{
    var s = report.Section("UtilsClass.CreateGameSeedFromInt");
    foreach (var r in Of("selftest_gameseed"))
    {
        int i = r.GetProperty("i").GetInt32();
        string game = r.GetProperty("seed").GetString();
        string ours = GameRng.CreateGameSeedFromInt(i);
        s.Check(game == ours, $"{i}: game={game} ours={ours}");
    }
}

void CheckStreams()
{
    var sb = report.Section("Stream states (in battle)");
    var sn = report.Section("Stream states (out of battle)");
    var battleCount = new Dictionary<string, int>();
    var normalLast = new Dictionary<string, int>();
    foreach (var r in recs)
    {
        string t = T(r);
        if (t == "battle_states_reset")
        {
            battleCount.Clear();
            continue;
        }
        if (t != "state")
        {
            continue;
        }
        string key = r.GetProperty("key").GetString();
        string seed = r.GetProperty("seed").GetString();
        int ret = r.GetProperty("ret").GetInt32();
        bool battle = r.GetProperty("in_battle").GetBoolean() && r.GetProperty("battle_states").GetBoolean();
        if (battle)
        {
            int baseState = r.GetProperty("boss").GetBoolean()
                ? GameRng.BossStreamBase(key, seed, r.GetProperty("boss_idx").GetInt32())
                : GameRng.BattleStreamBase(key, seed, r.GetProperty("small").GetInt32(), r.GetProperty("big").GetInt32());
            int expectedOffset = battleCount.GetValueOrDefault(key);
            battleCount[key] = expectedOffset + 1;
            int offset = unchecked(ret - baseState);
            sb.Check(offset == expectedOffset, $"#{N(r)} key={key}: offset {offset}, expected {expectedOffset}");
        }
        else
        {
            int offset = unchecked(ret - GameRng.NormalStreamBase(key, seed));
            if (normalLast.TryGetValue(key, out int last))
            {
                sn.Check(offset == last + 1, $"#{N(r)} key={key}: offset {offset}, expected {last + 1}");
            }
            else
            {
                // The counter may have been restored from a save, so only its plausibility is checkable.
                sn.Check(offset >= 0 && offset < 1_000_000, $"#{N(r)} key={key}: implausible first offset {offset}");
            }
            normalLast[key] = offset;
        }
    }
}

void CheckRandomRange()
{
    var si = report.Section("UtilsClass.RandomRange(int,int,key)");
    var sf = report.Section("UtilsClass.RandomRange(float,float,key)");
    var lastState = new Dictionary<string, int>();
    int doubleOnly = 0;
    foreach (var r in recs)
    {
        string t = T(r);
        if (t == "state")
        {
            lastState[r.GetProperty("key").GetString()] = r.GetProperty("ret").GetInt32();
            continue;
        }
        if (t != "rand_int" && t != "rand_float")
        {
            continue;
        }
        if (r.GetProperty("key").ValueKind == JsonValueKind.Null)
        {
            continue; // seeded from the clock (seednull); unpredictable by design
        }
        int state = lastState[r.GetProperty("key").GetString()];
        if (t == "rand_int")
        {
            int min = r.GetProperty("min").GetInt32(), max = r.GetProperty("max").GetInt32();
            int game = r.GetProperty("ret").GetInt32();
            int ours = GameRng.RandomRange(min, max, state);
            if (ours != game && RandomRangeDouble(min, max, state) == game)
            {
                doubleOnly++;
            }
            si.Check(ours == game, $"#{N(r)} [{min},{max}) state={state}: game={game} ours={ours}");
        }
        else
        {
            float min = r.GetProperty("min").GetSingle(), max = r.GetProperty("max").GetSingle();
            int game = r.GetProperty("ret_bits").GetInt32();
            int ours = Bits(GameRng.RandomRange(min, max, state));
            sf.Check(ours == game, $"#{N(r)} [{min},{max}] state={state}: game bits={game} ours={ours}");
        }
    }
    if (doubleOnly > 0)
    {
        si.Info($"{doubleOnly} mismatches would match with double-precision arithmetic");
    }
}

int RandomRangeDouble(int min, int max, int state)
{
    double f = GameRng.Seeded(state).Range(0f, 1f);
    int num = (int)Math.Floor(f * (max - min) + min);
    return num >= max && num > min ? max - 1 : num;
}

void CheckShuffles()
{
    var s = report.Section("Energy deck shuffles");
    var pending = new Dictionary<string, (string kind, List<string> list, int? state)>();
    foreach (var r in recs)
    {
        string t = T(r);
        if (t == "state" && r.GetProperty("key").GetString() == "Shuffle_Deck")
        {
            // A shuffle consumes exactly one Shuffle_Deck state; attach it to the open request.
            foreach (string which in pending.Keys.ToList())
            {
                if (pending[which].state == null)
                {
                    pending[which] = pending[which] with { state = r.GetProperty("ret").GetInt32() };
                    break;
                }
            }
            continue;
        }
        if (t == "deck_shuffle_pre" || t == "deck_reshuffle_pre")
        {
            string which = r.GetProperty("which").GetString();
            var list = Items(r.GetProperty(t == "deck_shuffle_pre" ? "list" : "cd"));
            pending[which] = (t, list, null);
            continue;
        }
        if (t == "deck_shuffle_post" || t == "draw")
        {
            string which = r.GetProperty("which").GetString();
            if (!pending.TryGetValue(which, out var p))
            {
                continue;
            }
            List<string> actual;
            if (t == "deck_shuffle_post" && p.kind == "deck_shuffle_pre")
            {
                actual = Items(r.GetProperty("list"));
            }
            else if (t == "draw" && p.kind == "deck_reshuffle_pre")
            {
                actual = new List<string> { r.GetProperty("val").GetRawText() };
                actual.AddRange(Items(r.GetProperty("deck")));
            }
            else
            {
                continue;
            }
            pending.Remove(which);
            if (p.state == null)
            {
                s.Check(false, $"#{N(r)} {which}: no Shuffle_Deck state was logged for this shuffle");
                continue;
            }
            var expected = new List<string>(p.list);
            GameRng.ShuffleListPowerSystem(expected, p.state.Value);
            s.Check(expected.SequenceEqual(actual),
                $"#{N(r)} {which} ({p.kind}): game=[{string.Join(" ", actual)}] ours=[{string.Join(" ", expected)}]");
        }
    }
}

// ---- helpers --------------------------------------------------------------------------------

// Reads consecutive top-level JSON values, so records that span several lines (early recorder
// builds embedded pretty-printed JSON) still parse. The game keeps the log open while running,
// hence the shared access.
static List<JsonElement> ReadRecords(string file)
{
    byte[] bytes;
    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    using (var ms = new MemoryStream())
    {
        fs.CopyTo(ms);
        bytes = ms.ToArray();
    }
    var list = new List<JsonElement>();
    var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowMultipleValues = true });
    while (reader.Read())
    {
        list.Add(JsonElement.ParseValue(ref reader));
    }
    return list;
}

IEnumerable<JsonElement> Of(string type) => recs.Where(r => T(r) == type);

static string T(JsonElement r) => r.GetProperty("t").GetString();

static long N(JsonElement r) => r.GetProperty("n").GetInt64();

static int[] Ints(JsonElement a) => a.EnumerateArray().Select(e => e.GetInt32()).ToArray();

static List<string> Items(JsonElement a) => a.EnumerateArray().Select(e => e.GetRawText()).ToList();

static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

static string LatestLog()
{
    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Shuxi", "LONESTAR", "Mods", "Dev", "SolverLogger", "logs");
    return new DirectoryInfo(dir).GetFiles("session_*.jsonl").OrderByDescending(f => f.Name).First().FullName;
}

sealed class Report
{
    private readonly List<Section> sections = new();

    public bool Failed => sections.Any(s => s.Fail > 0);

    public Section Section(string name)
    {
        var s = new Section(name);
        sections.Add(s);
        return s;
    }

    public void Print()
    {
        Console.WriteLine();
        foreach (var s in sections)
        {
            string status = s.Fail > 0 ? "FAIL" : s.Pass > 0 ? "PASS" : "----";
            Console.WriteLine($"[{status}] {s.Name}: {s.Pass} ok, {s.Fail} bad");
            foreach (string line in s.Notes)
            {
                Console.WriteLine($"         {line}");
            }
        }
        Console.WriteLine();
        Console.WriteLine(Failed ? "RESULT: MISMATCHES FOUND" : "RESULT: ALL CHECKS PASSED");
    }
}

sealed class Section(string name)
{
    public string Name { get; } = name;
    public int Pass { get; private set; }
    public int Fail { get; private set; }
    public List<string> Notes { get; } = new();

    public void Check(bool ok, string failMessage)
    {
        if (ok)
        {
            Pass++;
            return;
        }
        Fail++;
        if (Fail <= 5)
        {
            Notes.Add("x " + failMessage);
        }
    }

    public void Info(string message) => Notes.Add("i " + message);
}
