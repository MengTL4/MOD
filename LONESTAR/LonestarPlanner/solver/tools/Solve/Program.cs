// Headless battle session driver for the LONESTAR solver.
//
// One process = one rollout: the game keeps global singletons, so a fresh decision point comes from
// a fresh process (boot + replay the recorded prefix). tools/Solve --search spawns one of these per
// candidate action sequence in parallel and ranks the results.
//
// Usage examples:
//   Solve <battle.jsonl> --turn 2 --dry-run
//   Solve <battle.jsonl> --turn 2 --actions "move:-1;loadp:3/1@0,1;end" --json
//   Solve <battle.jsonl> --turn 2 --search --jobs 12 --max-plans 256
//   Solve <battle.jsonl> --bench

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Lonestar.Engine;

string root = null;
string exportDir = null;
string resourcesIndex = null;

string recording = null;
int turn = -1;
int seek = -1;
string rootOverride = null;
string actions = null;
string prefix = null;
string layout = null;
string forceUnits = null;
string coreSpec = null;
bool probeSkills = false;
bool dryRun = false;
bool endTurn = false;
bool bench = false;
bool json = false;
bool quiet = false;
bool replayRecorded = false;
bool search = false;
bool greedyPlay = false;
bool layoutInfo = false;
bool optimizeLayout = false;
bool optimizeCore = false;
bool audit = false;
bool dynamicSearch = false;
int beam = 16;
int depth = 6;
bool noCompare = false;
bool playBattle = false;
bool rolloutMode = false;
int refineTop = 5;
int jobs = Math.Max(4, Environment.ProcessorCount);
int maxPlans = 256;
int verify = 8;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--turn": turn = int.Parse(args[++i]); break;
        case "--root": rootOverride = args[++i]; break;
        case "--seek": seek = int.Parse(args[++i]); break;
        case "--actions": actions = args[++i]; break;
        case "--prefix": prefix = args[++i]; break;
        case "--layout": layout = args[++i]; break;
        case "--force-units": forceUnits = args[++i]; break;
        case "--core": coreSpec = args[++i]; break;
        case "--probe-skills": probeSkills = true; break;
        case "--dry-run": dryRun = true; break;
        case "--end-turn": endTurn = true; break;
        case "--bench": bench = true; break;
        case "--json": json = true; break;
        case "--quiet": quiet = true; break;
        case "--replay-recorded": replayRecorded = true; break;
        case "--search": search = true; break;
        case "--greedy-play": greedyPlay = true; break;
        case "--layout-info": layoutInfo = true; break;
        case "--optimize-layout": optimizeLayout = true; break;
        case "--optimize-core": optimizeCore = true; break;
        case "--audit": audit = true; break;
        case "--dynamic": dynamicSearch = true; break;
        case "--beam": beam = int.Parse(args[++i]); break;
        case "--depth": depth = int.Parse(args[++i]); break;
        case "--no-compare": noCompare = true; break;
        case "--play": playBattle = true; break;
        case "--rollout": rolloutMode = true; break;
        case "--refine-top": refineTop = int.Parse(args[++i]); break;
        case "--jobs": jobs = int.Parse(args[++i]); break;
        case "--max-plans": maxPlans = int.Parse(args[++i]); break;
        case "--verify": verify = int.Parse(args[++i]); break;
        case "--help":
        case "-h":
            Console.WriteLine("usage: Solve <battle.jsonl> [--turn N] [--seek K] [--prefix seq] [--layout L] [--actions seq] [--dry-run] [--end-turn] [--greedy-play] [--layout-info] [--replay-recorded] [--bench] [--json] [--search --jobs N --max-plans N --verify N] [--optimize-layout --refine-top N]");
            return 0;
        default:
            if (args[i].StartsWith("-")) { Console.Error.WriteLine("unknown option " + args[i]); return 2; }
            recording = args[i];
            break;
    }
}
if (recording == null)
{
    Console.Error.WriteLine("missing battle recording path");
    return 2;
}
root = rootOverride != null ? Path.GetFullPath(rootOverride) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
exportDir = Path.Combine(root, "data", "export");
resourcesIndex = Path.Combine(root, "data", "resources_index.tsv");
if (search)
{
    return dynamicSearch
        ? SearchMode.RunDynamic(recording, turn, prefix, layout, forceUnits, root, jobs, beam, depth, Assembly.GetEntryAssembly()?.Location ?? args[0])
        : SearchMode.Run(recording, turn, prefix, layout, forceUnits, root, jobs, maxPlans, verify, Assembly.GetEntryAssembly()?.Location ?? args[0]);
}
if (playBattle)
{
    return dynamicSearch
        ? SearchMode.PlayBattleDynamic(recording, layout, forceUnits, root, jobs, beam, depth, Assembly.GetEntryAssembly()?.Location ?? args[0])
        : SearchMode.PlayBattle(recording, layout, forceUnits, root, jobs, maxPlans, verify, Assembly.GetEntryAssembly()?.Location ?? args[0]);
}
if (optimizeLayout)
{
    return LayoutSearch.Run(recording, root, jobs, refineTop, assemblyPath: Assembly.GetEntryAssembly()?.Location ?? args[0]);
}
if (optimizeCore)
{
    return CoreSearch.Run(recording, root, jobs, assemblyPath: Assembly.GetEntryAssembly()?.Location ?? args[0]);
}
if (audit)
{
    return SearchMode.RunAudit(recording, turn, prefix, layout, forceUnits, root, jobs, maxPlans, verify, !noCompare, dynamicSearch, beam, depth, Assembly.GetEntryAssembly()?.Location ?? args[0]);
}

string workDir = Path.Combine(root, "out", "solve", $"{Environment.ProcessId}_{DateTime.Now:HHmmss}");
Directory.CreateDirectory(workDir);
StreamWriter gameLog = quiet ? null : new StreamWriter(Path.Combine(workDir, "game_debug.log"));

string layoutError = null;
List<(int X, int Y, int Unit)> layoutEntries = layout == null ? null : ParseLayout(layout, out layoutError);
if (layout != null && layoutEntries == null)
{
    Console.Error.WriteLine("bad layout: " + layoutError);
    return 2;
}
var sw = Stopwatch.StartNew();
var session = BattleSession.Open(recording, exportDir, resourcesIndex, workDir,
    gameLog == null ? null : new Action<string>(gameLog.WriteLine),
    layoutEntries == null && forceUnits == null && coreSpec == null ? null : new Action<BattleSession>(s =>
    {
        if (layoutEntries != null && !LayoutEditor.Apply(layoutEntries, out string error))
        {
            layoutError = error;
        }
        if (forceUnits != null && !ApplyForcedUnits(forceUnits, out string forceError))
        {
            layoutError = forceError;
        }
        if (coreSpec != null)
        {
            if (!ParseCoreSpec(coreSpec, out int[] powers, out int[] colors, out string coreError))
            {
                layoutError = coreError;
            }
            else
            {
                LayoutEditor.SetCore(powers, colors);
            }
        }
    }));
long openMs = sw.ElapsedMilliseconds;
if (gameLog != null) { gameLog.Flush(); }
if (layoutError != null)
{
    Console.Error.WriteLine("layout rejected: " + layoutError);
    return 1;
}

if (session.Problems.Count > 0 && !json)
{
    foreach (string p in session.Problems) Console.Error.WriteLine("[boot] " + p.Replace('\n', ' '));
}

if (layoutInfo)
{
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            layout = LayoutEditor.Cells().Select(c => new { c.X, c.Y, c.Status, c.UnitId, c.Lv, c.EquipLimit }),
            available = LayoutEditor.AvailableUnits().Select(u => new { u.Index, u.Id, u.Lv, u.EquipLimit, u.OnShip, u.X, u.Y }),
            loadLimit = LayoutEditor.Ship.shipLoadLimit,
            corePowers = LayoutEditor.CorePowers(),
            coreColors = LayoutEditor.CoreColors(),
        }));
    }
    else
    {
        Console.WriteLine(LayoutEditor.Describe());
        Console.WriteLine($"core: powers=[{string.Join(",", LayoutEditor.CorePowers())}] colors=[{string.Join(",", LayoutEditor.CoreColors())}]");
    }
    return 0;
}

sw.Restart();
if (prefix != null)
{
    if (!session.SeekPrefix(prefix))
    {
        Console.Error.WriteLine("could not apply prefix");
        return 1;
    }
}
else if (turn > 0)
{
    if (!session.SeekToTurn(turn))
    {
        Console.Error.WriteLine($"could not reach turn {turn}");
        return 1;
    }
}
else if (seek >= 0)
{
    if (!session.SeekToAction(seek))
    {
        Console.Error.WriteLine($"could not reach recorded action {seek}");
        return 1;
    }
}
long seekMs = sw.ElapsedMilliseconds;

BattleState state = session.State();

if (greedyPlay)
{
    BattleOutcome outcome = session.PlayToEndGreedy();
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            layout,
            layoutCells = LayoutEditor.Cells().Select(c => new { c.X, c.Y, c.UnitId, c.Lv }),
            turns = outcome.Turns,
            won = outcome.Won,
            ended = outcome.Ended,
            playerHp = outcome.PlayerHp,
            enemyHp = outcome.EnemyHp,
            playerHpLost = outcome.PlayerHpLost,
            maxRound = outcome.MaxRound,
            seconds = outcome.Seconds,
            details = outcome.Details.Select(t => new { t.Round, t.PlayerHpLost, t.EnemyHpLost, t.BattleEnded }),
        }));
    }
    else
    {
        Console.WriteLine("layout: " + LayoutEditor.Describe());
        Console.WriteLine("greedy play: " + outcome);
    }
    return 0;
}

if (replayRecorded)
{
    string next = string.Join(";", session.RecordedTurnActions());
    Console.WriteLine($"recorded turn {state.Round}: {next}");
    foreach (string token in session.RecordedTurnActions())
    {
        if (!ApplyToken(session, token, out string error))
        {
            Console.Error.WriteLine($"replay token failed: {token} {error}");
            return 1;
        }
    }
    session.SeekToEnd();
    BattleState fin = session.State();
    Console.WriteLine($"recorded battle final: player hp {fin.Player.Hp} dead={fin.Player.Dead} enemy hp {fin.Enemy.Hp} dead={fin.Enemy.Dead} round={fin.Round} ended={fin.Ended}");
    return 0;
}

if (actions != null)
{
    long t0 = sw.ElapsedMilliseconds;
    foreach (string raw in actions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!ApplyToken(session, raw, out string error))
        {
            Console.Error.WriteLine("action failed: " + raw + " " + error);
            return 1;
        }
    }
    long applyMs = sw.ElapsedMilliseconds - t0;
    TurnOutcome outcome = null;
    BattleOutcome rollout = null;
    if (!session.IsBattleOver() && rolloutMode)
    {
        rollout = session.PlayToEndGreedy(endCurrentTurnFirst: true);
    }
    else if (!session.IsBattleOver() && endTurn && !rawWantsEnd(actions))
    {
        outcome = session.EndTurn();
    }
    BattleState after = session.State();
    if (!json)
    {
        Console.WriteLine($"after sequence: round={after.Round} status={(TurnStatus)after.TurnStatus} hp={after.Player.Hp} enemyHp={after.Enemy.Hp} lines={DescribeLines(after.Lines)} ({applyMs}ms)");
        if (outcome != null)
        {
            Console.WriteLine($"turn resolved: {outcome} frames={outcome.Frames} ms={outcome.Seconds * 1000:F0}");
        }
        if (rollout != null)
        {
            Console.WriteLine($"greedy rollout: {rollout}");
        }
    }
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            error = (string)null,
            turn,
            round = after.Round,
            status = after.TurnStatus,
            ended = after.Ended,
            appliedMs = applyMs,
            player = new { after.Player.Hp, after.Player.Shield, after.Player.CenterX, after.Player.CenterY, after.Player.Dead, after.Player.Units.Count },
            enemy = new { after.Enemy.Hp, after.Enemy.Shield, after.Enemy.CenterX, after.Enemy.CenterY, after.Enemy.Dead, after.Enemy.Units.Count },
            hand = after.Hand.Select(h => new object[] { h.Power, h.Colors }),
            lines = after.Lines.Select(l => new { l.Y, l.PlayerTotal, l.EnemyTotal, l.Result }),
            units = after.Player.Units.Select(u => new { u.UnitIndex, u.Id, u.Lv, u.X, u.Y, u.Active, u.Destroyed, u.PowerTotal, slots = u.Slots.Select(x => new { x.Rare, x.Empty, x.Disturbed, x.Power }) }),
            outcome = outcome == null ? null : new
            {
                outcome.Round,
                outcome.PlayerHpBefore,
                outcome.PlayerHp,
                outcome.EnemyHpBefore,
                outcome.EnemyHp,
                outcome.PlayerHpLost,
                outcome.EnemyHpLost,
                outcome.BattleEnded,
                outcome.PlayerWon,
                outcome.PlayerDead,
                outcome.EnemyDead,
                outcome.Frames,
            },
            rollout = rollout == null ? null : new
            {
                rollout.Turns,
                rollout.Won,
                rollout.Ended,
                rollout.PlayerHp,
                rollout.EnemyHp,
                rollout.PlayerHpLost,
                rollout.MaxRound,
                rollout.Seconds,
            },
        }));
    }
    return 0;
}

// No actions: either a state query (--dry-run/--json) or a bench run.
List<LoadOption> loads = session.LoadOptions();
List<int> moves = session.MoveOptions();
List<SkillOption> skills = session.SkillOptions();
var skillData = new List<object>();
foreach (SkillOption skill in skills)
{
    List<TargetOption> targets = null;
    if (probeSkills && skill.TwoStep)
    {
        if (session.ApplySkillClick(skill.UnitIndex))
        {
            targets = session.PendingTargets();
        }
        session.CancelSkillTarget();
    }
    skillData.Add(new
    {
        skill.UnitIndex,
        skill.X,
        skill.Y,
        skill.UnitId,
        skill.Skill,
        skill.TwoStep,
        targets = targets?.Select(t => new { t.Index, t.IsCell, t.X, t.Y, t.UnitId }).ToList(),
    });
}
if (!json)
{
    PrintState(state, quiet);
    Console.WriteLine($"decisions: loads={loads.Count} moves={moves.Count} skills={skills.Count} hand={state.Hand.Count} recorded=[{string.Join(";", session.RecordedTurnActions())}]");
    foreach (LoadOption l in loads)
    {
        Console.WriteLine($"  option {l}");
    }
    foreach (SkillOption skill in skills)
    {
        Console.WriteLine($"  skill {skill.Skill} on unit {skill.UnitId}@({skill.X},{skill.Y}) twoStep={skill.TwoStep}");
    }
}
if (bench)
{
    if (!json)
    {
        Console.WriteLine($"timing: open={openMs}ms seek={seekMs}ms state={sw.ElapsedMilliseconds - seekMs}ms");
        if (loads.Count > 0)
        {
            var t = Stopwatch.StartNew();
            session.ApplyLoad(loads[0]);
            Console.WriteLine($"first load: {t.ElapsedMilliseconds}ms");
        }
    }
}
if (json)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        error = (string)null,
        turn,
        round = state.Round,
        status = state.TurnStatus,
        ended = state.Ended,
        openMs,
        seekMs,
        player = new { state.Player.Hp, state.Player.Shield, state.Player.CenterX, state.Player.CenterY, state.Player.Dead, state.Player.Move, state.MovePoints, state.MoveActionsLeft },
        enemy = new { state.Enemy.Hp, state.Enemy.Shield, state.Enemy.CenterX, state.Enemy.CenterY, state.Enemy.Dead },
        hand = state.Hand.Select(h => new object[] { h.Power, h.Colors }),
        lines = state.Lines.Select(l => new { l.Y, l.PlayerTotal, l.EnemyTotal, l.Result }),
        units = state.Player.Units.Select(u => new { u.UnitIndex, u.Id, u.Lv, u.X, u.Y, u.Active, u.Destroyed, u.PowerTotal, slots = u.Slots.Select(x => new { x.Rare, x.Empty, x.Disturbed, x.Power }) }),
        enemyUnits = state.Enemy.Units.Select(u => new { u.UnitIndex, u.Id, u.Lv, u.X, u.Y, u.PowerTotal, slots = u.Slots.Select(x => new { x.Rare, x.Empty, x.Power }) }),
        moves,
        loadOptions = loads.Select(l => new { l.HandIndex, l.UnitIndex, l.UnitId, l.X, l.Y, l.Power, l.Colors }),
        skills = skillData,
        recorded = prefix == null ? session.RecordedTurnActions() : new List<string>(),
    }));
}
return 0;

static bool ParseCoreSpec(string text, out int[] powers, out int[] colors, out string error)
{
    powers = null;
    colors = null;
    error = null;
    string[] parts = text.Split(';');
    if (parts.Length != 2)
    {
        error = "expected <powerCounts>;<colourCounts>";
        return false;
    }
    try
    {
        powers = parts[0].Length == 0 ? new int[5] : parts[0].Split(',').Select(int.Parse).ToArray();
        colors = parts[1].Length == 0 ? new int[3] : parts[1].Split(',').Select(int.Parse).ToArray();
    }
    catch (FormatException)
    {
        error = "core counts must be integers";
        return false;
    }
    if (powers.Length != 5 || colors.Length != 3)
    {
        error = "core needs 5 power counts (1..5) and 3 colour counts (white,blue,purple)";
        return false;
    }
    return true;
}

static List<(int X, int Y, int Unit)> ParseLayout(string text, out string error)
{
    error = null;
    var list = new List<(int X, int Y, int Unit)>();
    foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string[] fields = part.Split(',');
        if (fields.Length != 3
            || !int.TryParse(fields[0], out int x)
            || !int.TryParse(fields[1], out int y)
            || !int.TryParse(fields[2], out int unit))
        {
            error = "expected x,y,unitIndex; got " + part;
            return null;
        }
        list.Add((x, y, unit));
    }
    return list;
}

static bool rawWantsEnd(string actions) =>
    actions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(t => t == "end");

static void PrintState(BattleState s, bool quiet)
{
    Console.WriteLine($"round={s.Round} status={(TurnStatus)s.TurnStatus} inBattle={s.InBattle} over={s.BattleOver} ended={s.Ended} moveActions={s.MoveActionsLeft} movePoints={s.MovePoints}");
    Console.WriteLine($"hand: {string.Join(" ", s.Hand.Select(h => $"[{h.Power}/{string.Join(",", h.Colors)}]"))}");
    Console.WriteLine($"player: hp={s.Player.Hp}/{s.Player.MaxHp} shield={s.Player.Shield} center=({s.Player.CenterX},{s.Player.CenterY})");
    foreach (UnitSnapshot u in s.Player.Units)
    {
        Console.WriteLine($"  unit {u.Id} lv{u.Lv} @({u.X},{u.Y}) active={u.Active} destroyed={u.Destroyed} power={u.PowerTotal} skill={u.Skill} slots=[{string.Join(" ", u.Slots.Select(x => x.Empty ? $"{x.Rare}:." : $"{x.Rare}:{x.Power}"))}]");
    }
    Console.WriteLine($"enemy: hp={s.Enemy.Hp}/{s.Enemy.MaxHp} shield={s.Enemy.Shield} center=({s.Enemy.CenterX},{s.Enemy.CenterY})");
    if (!quiet)
    {
        foreach (UnitSnapshot u in s.Enemy.Units)
        {
            Console.WriteLine($"  enemy unit {u.Id} lv{u.Lv} @({u.X},{u.Y}) power={u.PowerTotal} slots=[{string.Join(" ", u.Slots.Select(x => x.Empty ? $"{x.Rare}:." : $"{x.Rare}:{x.Power}"))}]");
        }
    }
    Console.WriteLine($"lines: {DescribeLines(s.Lines)}");
}

static string DescribeLines(List<LineSnapshot> lines) =>
    lines.Count == 0 ? "(none)" : string.Join(" ", lines.Select(l => $"y{l.Y}:{l.PlayerTotal}v{l.EnemyTotal}({l.Result})"));

static bool ApplyToken(BattleSession session, string token, out string error)
{
    error = null;
    if (token == "end")
    {
        session.EndTurn();
        return true;
    }
    string[] parts = token.Split(':', 2);
    if (parts.Length != 2)
    {
        error = "expected verb:payload";
        return false;
    }
    switch (parts[0])
    {
        case "load":
        {
            string[] at = parts[1].Split('@');
            if (at.Length != 2)
            {
                error = "load expects <handIndex>@<x>,<y>";
                return false;
            }
            return LoadByIndex(session, int.Parse(at[0]), at[1], out error);
        }
        case "loadp":
        {
            // loadp:<power>/<colors>@<x>,<y> — identity based, order independent.
            string[] at = parts[1].Split('@');
            if (at.Length != 2)
            {
                error = "loadp expects <power>/<colors>@<x>,<y>";
                return false;
            }
            string[] pc = at[0].Split('/');
            int power = int.Parse(pc[0]);
            int[] colors = pc[1].Length == 0 ? Array.Empty<int>() : pc[1].Split(',').Select(int.Parse).ToArray();
            BattleState state = session.State();
            int index = IndexOfCard(state, power, colors);
            if (index < 0)
            {
                error = $"no hand card with power {power} colors [{string.Join(",", colors)}]";
                return false;
            }
            return LoadByIndex(session, index, at[1], out error);
        }
        case "move":
        {
            int y = int.Parse(parts[1]);
            bool ok = session.ApplyMove(y);
            if (!ok) error = "move rejected by game";
            return ok;
        }
        case "click":
        {
            string[] coor = parts[1].Split(',');
            int x = int.Parse(coor[0]), y = int.Parse(coor[1]);
            bool ok = session.ApplySkillClick(x, y);
            if (!ok) error = $"skill click on {x},{y} rejected";
            return ok;
        }
        case "target":
        {
            int index = int.Parse(parts[1]);
            bool ok = session.ApplyTarget(index);
            if (!ok) error = $"target {index} rejected";
            return ok;
        }
        default:
            error = "unknown verb " + parts[0];
            return false;
    }
}

static bool ApplyForcedUnits(string text, out string error)
{
    error = null;
    foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string[] fields = part.Split(',');
        if (fields.Length != 4
            || !int.TryParse(fields[0], out int x)
            || !int.TryParse(fields[1], out int y)
            || !int.TryParse(fields[2], out int id)
            || !int.TryParse(fields[3], out int lv))
        {
            error = "expected x,y,unitId,lv; got " + part;
            return false;
        }
        ShipCell cell = LayoutEditor.Cell(x, y);
        if (cell == null || cell.shipCellStatus == ShipCellStatus.None)
        {
            error = $"cell {x},{y} is not usable";
            return false;
        }
        cell.unitData = new UnitData(id, lv);
        cell.shipCellStatus = ShipCellStatus.Equipped;
    }
    return true;
}

static int IndexOfCard(BattleState state, int power, int[] colors)
{
    for (int i = 0; i < state.Hand.Count; i++)
    {
        EnergySnapshot h = state.Hand[i];
        if (h.Power == power && h.Colors.SequenceEqual(colors))
        {
            return i;
        }
    }
    return -1;
}

static bool LoadByIndex(BattleSession session, int handIndex, string coorText, out string error)
{
    error = null;
    string[] coor = coorText.Split(',');
    int x = int.Parse(coor[0]), y = int.Parse(coor[1]);
    BattleState state = session.State();
    UnitSnapshot unit = state.Player.Units.FirstOrDefault(u => u.X == x && u.Y == y);
    if (unit == null)
    {
        error = $"no player unit at {x},{y}";
        return false;
    }
    bool ok = session.ApplyLoad(handIndex, unit.UnitIndex);
    if (!ok) error = "load rejected by game";
    return ok;
}













