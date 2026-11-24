using System.Text.Json;
using System.Text.Json.Serialization;
using Lonestar.Engine;

namespace Brain;

/// <summary>Compact view of one decision state, enough for the master to rank and to display.</summary>
public sealed class NodeInfo
{
    public string Tok;
    public bool Ok = true;
    public string Hash;
    public int Round;
    public bool InTurn;
    public bool Over;
    public bool Won;
    public int PHp, PMaxHp, PSh, EHp, EMaxHp, ESh, EBreak, EBreakLimit;
    public int Hand, HandPower, Exchange, Move, PMaxMove, MoveActs;
    public int Dealt, Taken, Wins, Losses;
    public bool Choice;
    /// <summary>Per clash line: y, player total, enemy total, result, both sides present.</summary>
    public List<int[]> Lines = new();

    public static NodeInfo From(BattleState s, string tok, string hash)
    {
        var n = new NodeInfo
        {
            Tok = tok,
            Hash = hash,
            Round = s.Round,
            InTurn = s.TurnStatus == (int)TurnStatus.PlayerTurn && s.InBattle && !s.BattleOver,
            Over = !s.InBattle || s.BattleOver,
            PHp = s.Player?.Hp ?? 0,
            PMaxHp = s.Player?.MaxHp ?? 0,
            PSh = s.Player?.Shield ?? 0,
            EHp = s.Enemy?.Hp ?? 0,
            EMaxHp = s.Enemy?.MaxHp ?? 0,
            ESh = s.Enemy?.Shield ?? 0,
            EBreak = s.Enemy?.BreakCount ?? -1,
            EBreakLimit = s.Enemy?.BreakLimit ?? -1,
            Hand = s.Hand.Count,
            HandPower = s.Hand.Sum(h => h.Power),
            Exchange = s.Player?.Exchange ?? 0,
            Move = s.MovePoints,
            PMaxMove = s.Player?.MaxMove ?? 0,
            MoveActs = s.MoveActionsLeft,
            Choice = s.ChoicePending,
        };
        n.Won = n.Over && (s.Enemy?.Dead ?? false) && !(s.Player?.Dead ?? true);
        foreach (LineSnapshot l in s.Lines)
        {
            bool both = l.HasPlayer && l.HasEnemy;
            n.Lines.Add(new[] { l.Y, l.PlayerTotal, l.EnemyTotal, l.Result, both ? 1 : 0, l.PlayerLine, l.EnemyLine });
            if (!both)
            {
                continue;
            }
            if (l.Result > 0) { n.Dealt += l.Result; n.Wins++; }
            else if (l.Result < 0) { n.Taken += -l.Result; n.Losses++; }
        }
        return n;
    }
}

/// <summary>Result of ending the turn at a node and (optionally) playing the battle out.</summary>
public sealed class EvalResult
{
    public string Prefix;
    public bool Ok;
    public string Error;
    public TurnOutcome Turn;
    public NodeInfo Next;
    public BattleOutcome Rollout;
    public string Policy;
}

/// <summary>Pre-battle ship: usable cells, the units that can be placed, and the load limit.</summary>
public sealed class LoadoutInfo
{
    public int LoadLimit;
    public List<int[]> Cells = new();
    public List<LoadoutUnit> Units = new();
    public string Current;
}

public sealed class LoadoutUnit
{
    public int Index;
    public int Id;
    public int Lv;
    public int EquipLimit;
    public bool OnShip;
    public int X = -1;
    public int Y = -1;
    /// <summary>Position in ItemManager.itemUnits for bag units (-1 when on the ship).</summary>
    public int Bag = -1;
    public bool Cannon;
    /// <summary>Everything the battle can see of the unit (id, lv, mod, refits, args): equal signatures are interchangeable.</summary>
    public string Sig;
}

public sealed class Request
{
    public int Id;
    public string Op;
    public string Recording;
    public int SeekTurn = -1;
    public string Tokens;
    public string Prefix;
    public List<string> Prefixes;
    public bool Rollout = true;
    public int MaxTurns = 30;
    public bool Swaps = true;
    /// <summary>Exchanges allowed in one turn's search path (counted in the prefix since the last "end").</summary>
    public int MaxSwaps = 99;
    public bool Skills = true;
    public string Layout;
    public int EnemyId;
    /// <summary>Sandbox: "x,y,unitId,lv;..." placed on the ship before the battle (bypasses the bag).</summary>
    public string Force;
    /// <summary>Playout policies (RolloutPolicy names); the best playout counts.</summary>
    public List<string> Policies;
}

public sealed class Response
{
    public int Id;
    public bool Ok = true;
    public string Error;
    public NodeInfo Node;
    public List<NodeInfo> Children;
    public List<EvalResult> Evals;
    public List<string> Recorded;
    public List<List<string>> Plans;
    public List<string> Sync;
    public LoadoutInfo Loadout;
    public int Applied;
    public double Ms;
    /// <summary>BRAIN_TRACE=1: where the worker spent the time (phase=ms ...).</summary>
    public string Profile;
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string text) => JsonSerializer.Deserialize<T>(text, Options);
}
