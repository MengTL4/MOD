using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using UnityEngine;

namespace Lonestar.Engine
{
    /// <summary>One energy card in the player's hand, as the solver sees it.</summary>
    public sealed class EnergySnapshot
    {
        public int Power;
        public int[] Colors = Array.Empty<int>();
        public int HighestColor;
    }

    /// <summary>One unit slot: what it demands and what is currently loaded into it.</summary>
    public sealed class SlotSnapshot
    {
        public int Rare;
        public bool Empty;
        public bool Disturbed;
        public int Power;
        public int PowerType;
        public int[] EmptyPowers = Array.Empty<int>();
    }

    /// <summary>One ship unit at its grid coordinate.</summary>
    public sealed class UnitSnapshot
    {
        public int UnitIndex;
        public int Id;
        public int Lv;
        public int X;
        public int Y;
        public bool Active;
        public bool Destroyed;
        public int PowerTotal;
        public List<SlotSnapshot> Slots = new List<SlotSnapshot>();
        public List<string> Buffs = new List<string>();
        public string Skill;
    }

    /// <summary>One side of the battle: ship resources plus its units.</summary>
    public sealed class ShipSnapshot
    {
        public int DataId;
        public int Hp;
        public int MaxHp;
        public int Shield;
        public int MaxShield;
        public int Move;
        public int MaxMove;
        public int Exchange;
        public int MaxExchange;
        public int BreakCount = -1;
        public int BreakLimit = -1;
        public int CenterX;
        public int CenterY;
        public bool Dead;
        public List<UnitSnapshot> Units = new List<UnitSnapshot>();
    }

    /// <summary>One clash line as the game currently previews it.</summary>
    public sealed class LineSnapshot
    {
        public int Y;
        public int PlayerLine;
        public int EnemyLine;
        public int PlayerTotal;
        public int EnemyTotal;
        public int Result;
        public bool HasPlayer;
        public bool HasEnemy;
    }

    /// <summary>Everything a decision needs: whose turn it is and what they can do.</summary>
    public sealed class BattleState
    {
        public int Round;
        public int TurnStatus;
        public bool InBattle;
        public bool BattleOver;
        public bool Ended;
        public int MoveActionsLeft;
        public int MovePoints;
        public ShipSnapshot Player;
        public ShipSnapshot Enemy;
        public List<EnergySnapshot> Hand = new List<EnergySnapshot>();
        public List<LineSnapshot> Lines = new List<LineSnapshot>();
        public bool ChoicePending;
    }

    /// <summary>A candidate placement: the <c>handIndex</c>-th card loaded onto the unit at (X,Y).</summary>
    public sealed class LoadOption
    {
        public int HandIndex;
        public int UnitIndex;
        public int UnitId;
        public int X;
        public int Y;
        public int Power;
        public int[] Colors = Array.Empty<int>();

        public override string ToString() =>
            $"load:{HandIndex}@{X},{Y} (power {Power}, colors [{string.Join(",", Colors)}])";
    }

    /// <summary>Outcome of resolving one player turn (placements + end turn) down to the next decision.</summary>
    public sealed class TurnOutcome
    {
        public int Round;
        public int PlayerHpBefore;
        public int EnemyHpBefore;
        public int PlayerHp;
        public int PlayerShield;
        public int EnemyHp;
        public int EnemyShield;
        public int PlayerHpLost;
        public int EnemyHpLost;
        public bool BattleEnded;
        public bool PlayerWon;
        public bool TimedOut;
        public int Frames;
        public double Seconds;

        public bool PlayerDead;
        public bool EnemyDead;

        public override string ToString() =>
            $"player {PlayerHp}hp{PlayerShield}sh (-{PlayerHpLost}) vs enemy {EnemyHp}hp{EnemyShield}sh (-{EnemyHpLost})" +
            (BattleEnded ? (PlayerWon ? " => VICTORY" : " => DEFEAT") : "");
    }

    /// <summary>Full-battle result produced by a headless policy run.</summary>
    public sealed class BattleOutcome
    {
        public int Turns;
        public int MaxRound;
        public int PlayerHp;
        /// <summary>Fuel (shipData.currentMove) left after the battle; it carries over to the next one.</summary>
        public int PlayerFuel;
        public int EnemyHp;
        public int PlayerHpLost;
        public bool Ended;
        public bool Won;
        public double Seconds;
        public List<TurnOutcome> Details = new List<TurnOutcome>();

        public override string ToString() =>
            $"{(Won ? "VICTORY" : Ended ? "DEFEAT" : "UNFINISHED")} turns={Turns} player {PlayerHp}hp enemy {EnemyHp}hp ({Seconds:F1}s)";
    }

    /// <summary>
    /// Drives the real game logic headlessly for one battle, exposing decision points to a solver.
    /// A session is single-use per process: the game keeps global singletons, so callers that need
    /// independent rollouts run one session per process (tools/Solve is designed that way).
    /// The recording that is opened supplies both the pre-battle save and the prefix of player
    /// decisions that leads to a chosen decision point.
    /// </summary>
    public sealed partial class BattleSession
    {
        public const int MaxTurnFrames = 60 * 600;

        private readonly Recording recording;
        private readonly string exportDir;
        private readonly string resourcesIndex;
        private readonly string workDir;
        private readonly Action<string> log;
        private readonly List<JsonElement> recordedActions;
        private int applied;

        public List<string> Problems { get; } = new List<string>();

        public Recording Recording => recording;

        private BattleSession(Recording recording, string exportDir, string resourcesIndex, string workDir, Action<string> log)
        {
            this.recording = recording;
            this.exportDir = exportDir;
            this.resourcesIndex = resourcesIndex;
            this.workDir = workDir;
            this.log = log ?? (_ => { });
            recordedActions = recording.Actions.ToList();
        }

        /// <summary>
        /// Boots the game and restores the pre-battle save. When <paramref name="beforeBattle"/> is
        /// given it runs after the save is restored and before the battle starts (the layout editor
        /// uses this hook to re-arrange the ship).
        /// </summary>
        public static BattleSession Open(string recordingPath, string exportDir, string resourcesIndex, string workDir,
            Action<string> log, Action<BattleSession> beforeBattle = null, int enemyOverride = 0)
        {
            var session = new BattleSession(new Recording(recordingPath), exportDir, resourcesIndex, workDir, log);
            session.enemyOverride = enemyOverride;
            session.Boot();
            beforeBattle?.Invoke(session);
            session.EnterBattle();
            return session;
        }

        /// <summary>
        /// Boots and restores the pre-battle save without entering the battle. The loadout search
        /// snapshots this state once and, per candidate layout, restores it, rearranges the ship
        /// and calls <see cref="EnterBattleNow"/>.
        /// </summary>
        public static BattleSession OpenPreBattle(string recordingPath, string exportDir, string resourcesIndex, string workDir,
            Action<string> log, int enemyOverride = 0, int battleEventOverride = -1)
        {
            var session = new BattleSession(new Recording(recordingPath), exportDir, resourcesIndex, workDir, log);
            session.enemyOverride = enemyOverride;
            session.battleEventOverride = battleEventOverride;
            session.Boot();
            return session;
        }

        public bool EnterBattleNow()
        {
            Problems.Clear();
            applied = 0;
            EnterBattle();
            return Problems.Count == 0;
        }

        /// <summary>Sandbox: fight this enemy instead of the recorded one (the recorded actions then no longer apply).</summary>
        private int enemyOverride;

        /// <summary>Sandbox: this bounty battle event instead of the recorded one (-1 = recorded; none with an enemy override).</summary>
        private int battleEventOverride = -1;

        /// <summary>A session over an already-running battle (no boot): ReplayDriver uses it for the extra action types.</summary>
        internal static BattleSession Attach(Recording recording, Action<string> log) =>
            new BattleSession(recording, null, null, null, log);

        internal bool ApplyRecordedAction(JsonElement action) => ApplyRecordedExtra(action);

        private static BattleManager Bm => Singleton<BattleManager>.Instance();

        private static List<EnergyData> HandPowers => SolverLogger.Snap.HandPowers(Singleton<PowerManager>.Instance());

        public int AppliedActions => applied;

        public int RecordedActionCount => recordedActions.Count;

        /// <summary>The run-state ship data (layout lives on its grid cells).</summary>
        public PlayerShipData CurrentShip => Singleton<ShipDatasManager>.Instance().currentPlayerShipData;

        private void Boot()
        {
            var sw = Stopwatch.StartNew();
            HeadlessGame.Boot(exportDir, resourcesIndex, System.IO.Path.Combine(workDir, "persistent"), recording.SaveFiles, log);
            log($"boot {sw.ElapsedMilliseconds}ms");
        }

        private void EnterBattle()
        {
            var sw = Stopwatch.StartNew();
            HeadlessGame.EnterBattle(enemyOverride > 0 ? enemyOverride : recording.EnemyId,
                battleEventOverride >= 0 ? battleEventOverride : enemyOverride > 0 ? 0 : recording.BattleEventId);
            // battleOver starts out true and inBattle false until the grid is built, so (unlike
            // WaitForNextPlayerTurn) only trust them once the battle is set up; the enemy moves first.
            bool reached = UnityRuntime.RunUntil(
                () => Bm.inBattle && (Bm.battleOver || Bm.turnStatus == TurnStatus.PlayerTurn),
                MaxTurnFrames) >= 0;
            HeadlessGame.Settle(300);
            log($"battle reached first player turn: {reached} (enter {sw.ElapsedMilliseconds}ms)");
            if (!reached)
            {
                Problems.Add("battle never reached the first player turn");
            }
        }

        // ---- replay to a decision point --------------------------------------------------------

        /// <summary>Applies recorded actions up to the start of <paramref name="round"/>'s player turn.</summary>
        public bool SeekToTurn(int round)
        {
            while (applied < recordedActions.Count)
            {
                BattleManager bm = Bm;
                if (!bm.inBattle || bm.battleOver)
                {
                    return false;
                }
                if (bm.round == round && bm.turnStatus == TurnStatus.PlayerTurn)
                {
                    return true;
                }
                ApplyRecorded(recordedActions[applied++]);
            }
            return Bm.round == round && Bm.turnStatus == TurnStatus.PlayerTurn;
        }

        /// <summary>Applies the first <paramref name="count"/> recorded actions verbatim.</summary>
        public bool SeekToAction(int count)
        {
            count = Math.Min(count, recordedActions.Count);
            while (applied < count)
            {
                if (!Bm.inBattle || Bm.battleOver)
                {
                    return false;
                }
                ApplyRecorded(recordedActions[applied++]);
            }
            return true;
        }

        /// <summary>
        /// Re-reads the (live, still growing) battle file and applies the actions recorded since the
        /// last call. Returns how many were applied. <see cref="LastRecordedSnap"/> is then the newest
        /// in-game snapshot, for <see cref="CheckSync"/>.
        /// </summary>
        public int SyncRecording()
        {
            List<JsonElement> records = Recording.ReadRecords(recording.Path);
            List<JsonElement> actions = records.Where(r => Recording.Type(r).StartsWith("act_") && Recording.Type(r) != "act_result").ToList();
            int before = applied;
            recordedActions.Clear();
            recordedActions.AddRange(actions);
            while (applied < recordedActions.Count)
            {
                if (!Bm.inBattle || Bm.battleOver)
                {
                    break;
                }
                ApplyRecorded(recordedActions[applied++]);
            }
            LastRecordedSnap = records.LastOrDefault(r => Recording.Type(r) == "snap");
            return applied - before;
        }

        /// <summary>The newest in-game "snap" record seen by <see cref="SyncRecording"/>.</summary>
        public JsonElement LastRecordedSnap { get; private set; }

        /// <summary>
        /// Differences between the headless state and the newest in-game snapshot (empty = in sync).
        /// Only meaningful when that snapshot was taken at the current point (after the last action).
        /// </summary>
        public List<string> CheckSync(int max = 8)
        {
            var diffs = new List<string>();
            if (LastRecordedSnap.ValueKind != JsonValueKind.Object)
            {
                return diffs;
            }
            string tag = LastRecordedSnap.GetProperty("tag").GetString();
            using JsonDocument mine = JsonDocument.Parse(Fingerprint());
            StateDiff.Compare(LastRecordedSnap, mine.RootElement, "", diffs, max,
                tag.StartsWith("after_", StringComparison.Ordinal) && tag != "after_damage" ? StateDiff.TransientKeys : null);
            diffs.RemoveAll(d => d.Contains("only in headless", StringComparison.Ordinal) || d.Contains("missing in headless", StringComparison.Ordinal));
            return diffs;
        }

        /// <summary>Applies every remaining recorded action (reproduces the whole battle).</summary>
        public void SeekToEnd()
        {
            while (applied < recordedActions.Count)
            {
                if (!Bm.inBattle || Bm.battleOver)
                {
                    break;
                }
                ApplyRecorded(recordedActions[applied++]);
            }
            HeadlessGame.Settle(60 * 30);
        }

        /// <summary>
        /// The recorded human decisions for the turn the session currently sits at, as compact
        /// tokens ("move:-1", "load:2@0,1", "end"). Used as a baseline and for validation.
        /// </summary>
        public List<string> RecordedTurnActions()
        {
            var tokens = new List<string>();
            for (int i = applied; i < recordedActions.Count; i++)
            {
                JsonElement action = recordedActions[i];
                switch (Recording.Type(action))
                {
                    case "act_load":
                    {
                        int handIndex = action.GetProperty("hand_index").GetInt32();
                        JsonElement coor = action.GetProperty("target").GetProperty("coor");
                        tokens.Add($"load:{handIndex}@{coor[0].GetInt32()},{coor[1].GetInt32()}");
                        break;
                    }
                    case "act_move":
                        tokens.Add($"move:{action.GetProperty("y").GetInt32()}");
                        break;
                    case "act_end_turn":
                        tokens.Add("end");
                        return tokens;
                    default:
                        break;
                }
            }
            return tokens;
        }

        private void ApplyRecorded(JsonElement action)        {
            BattleManager bm = Bm;
            switch (Recording.Type(action))
            {
                case "act_load":
                {
                    int handIndex = action.GetProperty("hand_index").GetInt32();
                    JsonElement target = action.GetProperty("target");
                    ShipUnitController unit = FindUnit(target.GetProperty("player").GetBoolean(), target.GetProperty("coor"));
                    if (unit == null)
                    {
                        Problems.Add($"#{action.GetProperty("n").GetInt64()} act_load: target unit not found");
                        return;
                    }
                    if (!ApplyLoad(handIndex, unit))
                    {
                        Problems.Add($"#{action.GetProperty("n").GetInt64()} act_load: load failed");
                    }
                    return;
                }
                case "act_move":
                {
                    if (action.TryGetProperty("ship_is_player", out JsonElement isPlayer) && !isPlayer.GetBoolean())
                    {
                        return; // enemy moves are part of the enemy turn the engine runs itself
                    }
                    int y = action.GetProperty("y").GetInt32();
                    if (!ApplyMove(y))
                    {
                        Problems.Add($"#{action.GetProperty("n").GetInt64()} act_move: move {y} failed");
                    }
                    return;
                }
                case "act_end_turn":
                {
                    if (!IsPlayerCaller(action, "BattlePanel.EndTurn"))
                    {
                        return;
                    }
                    int round = bm.round;
                    bm.EndTurn();
                    WaitForNextPlayerTurn(round);
                    return;
                }
                default:
                    if (!ApplyRecordedExtra(action))
                    {
                        Problems.Add($"#{action.GetProperty("n").GetInt64()} {Recording.Type(action)}: replay not implemented");
                    }
                    return;
            }
        }

        private static bool IsPlayerCaller(JsonElement action, string frame) =>
            action.TryGetProperty("by", out JsonElement by) && by.EnumerateArray().Any(f => f.GetString()?.Contains(frame) == true);

        private static ShipUnitController FindUnit(bool player, JsonElement coor)
        {
            BattleManager bm = Bm;
            ShipController ship = player ? bm.playerShip : bm.enemyShip;
            int x = coor[0].GetInt32(), y = coor[1].GetInt32();
            return ship.shipUnitControllers.FirstOrDefault(u => u != null && u.shipCell != null && u.coor.x == x && u.coor.y == y);
        }

        private bool WaitForNextPlayerTurn(int previousRound)
        {
            int frames = UnityRuntime.RunUntil(
                () => !Bm.inBattle || Bm.battleOver ||
                      (Bm.turnStatus == TurnStatus.PlayerTurn && Bm.round != previousRound),
                MaxTurnFrames);
            HeadlessGame.Settle(300);
            return frames >= 0;
        }

        // ---- state -----------------------------------------------------------------------------

        /// <summary>Current decision state, after forcing the game to refresh its preview lines.</summary>
        public BattleState State()
        {
            BattleManager bm = Bm;
            bm.UpdateInfoForce();
            var state = new BattleState
            {
                Round = bm.round,
                TurnStatus = (int)bm.turnStatus,
                InBattle = bm.inBattle,
                BattleOver = bm.battleOver,
                Ended = !bm.inBattle,
                Player = Ship(bm.playerShip),
                Enemy = Ship(bm.enemyShip),
                Hand = HandPowers.Select(e => new EnergySnapshot
                {
                    Power = e.power,
                    Colors = Colors(e.powerColors),
                    HighestColor = Highest(e.powerColors),
                }).ToList(),
                Lines = bm.battleResultInfos.OrderBy(kv => kv.Key).Select(kv => new LineSnapshot
                {
                    Y = kv.Key,
                    PlayerLine = kv.Value.playerLine,
                    EnemyLine = kv.Value.enemyLine,
                    PlayerTotal = kv.Value.playerTotal,
                    EnemyTotal = kv.Value.enemyTotal,
                    Result = kv.Value.result,
                    HasPlayer = kv.Value.playerShipController != null,
                    HasEnemy = kv.Value.enemyShipController != null,
                }).ToList(),
                ChoicePending = ChoicePending(),
            };
            if (bm.playerShip?.shipVisual?.barPanel?.shipMoveCell != null)
            {
                state.MoveActionsLeft = bm.playerShip.shipVisual.barPanel.shipMoveCell.moveCount;
            }
            state.MovePoints = bm.playerShip?.shipData?.currentMove ?? 0;
            return state;
        }

        private static ShipSnapshot Ship(ShipBaseController ship)
        {
            if (ship == null)
            {
                return null;
            }
            ShipData d = ship.shipData;
            var snap = new ShipSnapshot
            {
                DataId = d.dataID,
                Hp = d.currentHP,
                MaxHp = d.maxHP,
                Shield = d.currentShield,
                MaxShield = d.maxShield,
                Move = d.currentMove,
                MaxMove = d.maxMove,
                Exchange = d.currentExchange,
                MaxExchange = d.maxExchange,
                CenterX = d.GetShipCenter().x,
                CenterY = d.GetShipCenter().y,
                Dead = ship.isDead,
            };
            if (ship is EnemyShipController enemy && enemy.breakSystem != null)
            {
                snap.BreakCount = enemy.breakSystem.breakCount.Value;
                snap.BreakLimit = enemy.breakSystem.breakLimit;
            }
            for (int i = 0; i < ship.shipUnitControllers.Count; i++)
            {
                ShipUnitController u = ship.shipUnitControllers[i];
                if (u == null)
                {
                    continue;
                }
                var unit = new UnitSnapshot
                {
                    UnitIndex = i,
                    Id = u.unitData?.id ?? 0,
                    Lv = u.unitData?.lv ?? 0,
                    X = u.shipCell == null ? -1 : u.coor.x,
                    Y = u.shipCell == null ? -1 : u.coor.y,
                    Active = u.shipUnitActive,
                    Destroyed = u.destroyed,
                    Skill = u.skill?.GetType().Name,
                };
                foreach (ShipUnitSlot slot in u.unitSlots)
                {
                    unit.Slots.Add(new SlotSnapshot
                    {
                        Rare = slot.rare,
                        Empty = slot.powerData == null,
                        Disturbed = slot.isDisturbed,
                        Power = slot.powerData?.power ?? 0,
                        PowerType = slot.powerData == null ? -1 : (int)slot.powerData.powerDataType,
                        EmptyPowers = slot.emptyDatas.Select(p => p.power).ToArray(),
                    });
                }
                unit.PowerTotal = u.powerDataContainer?.powerDatas.Sum(p => p.power) ?? 0;
                foreach (Buff buff in u.buffs)
                {
                    if (buff != null)
                    {
                        unit.Buffs.Add(buff.GetType().Name);
                    }
                }
                snap.Units.Add(unit);
            }
            return snap;
        }

        private static int[] Colors(PowerColors colors) =>
            colors?.colorList.Select(c => (int)c).ToArray() ?? Array.Empty<int>();

        private static int Highest(PowerColors colors) =>
            colors == null || colors.colorList.Count == 0 ? 0 : (int)colors.GetHighestColor();

        // ---- actions ---------------------------------------------------------------------------

        /// <summary>
        /// Candidate placements: every hand card against every live unit that has one slot it could
        /// fit by the static rules (empty, not disturbed, color high enough). Some units veto loads
        /// in event handlers, so an apply can still fail; callers treat false as "skip this branch".
        /// </summary>
        public List<LoadOption> LoadOptions()
        {
            var options = new List<LoadOption>();
            BattleManager bm = Bm;
            if (!bm.inBattle || bm.battleOver || bm.turnStatus != TurnStatus.PlayerTurn)
            {
                return options;
            }
            List<EnergyData> hand = HandPowers;
            ShipController player = bm.playerShip;
            for (int h = 0; h < hand.Count; h++)
            {
                EnergyData energy = hand[h];
                if (energy == null)
                {
                    continue;
                }
                int highest = Highest(energy.powerColors);
                for (int ui = 0; ui < player.shipUnitControllers.Count; ui++)
                {
                    ShipUnitController unit = player.shipUnitControllers[ui];
                    if (unit == null || unit.destroyed || !unit.shipUnitActive || unit.shipCell == null)
                    {
                        continue;
                    }
                    if (!HasFittingSlot(unit, highest))
                    {
                        continue;
                    }
                    options.Add(new LoadOption
                    {
                        HandIndex = h,
                        UnitIndex = ui,
                        UnitId = unit.unitData?.id ?? 0,
                        X = unit.coor.x,
                        Y = unit.coor.y,
                        Power = energy.power,
                        Colors = Colors(energy.powerColors),
                    });
                }
            }
            return options;
        }

        private static bool HasFittingSlot(ShipUnitController unit, int highestColor)
        {
            foreach (ShipUnitSlot slot in unit.unitSlots)
            {
                if (slot.powerData == null && !slot.isDisturbed && highestColor >= slot.rare)
                {
                    return true;
                }
            }
            return false;
        }

        public bool ApplyLoad(int handIndex, int unitIndex)
        {
            BattleManager bm = Bm;
            if (unitIndex < 0 || unitIndex >= bm.playerShip.shipUnitControllers.Count)
            {
                return false;
            }
            return ApplyLoad(handIndex, bm.playerShip.shipUnitControllers[unitIndex]);
        }

        public bool ApplyLoad(int handIndex, ShipUnitController unit)
        {
            BattleManager bm = Bm;
            List<EnergyData> hand = HandPowers;
            if (handIndex < 0 || handIndex >= hand.Count || unit == null)
            {
                return false;
            }
            bool ok = hand[handIndex].powerController.PowerLoad(unit);
            HeadlessGame.Settle(300);
            if (ok)
            {
                bm.UpdateInfoForce();
            }
            return ok;
        }

        public bool ApplyLoad(LoadOption option) => ApplyLoad(option.HandIndex, option.UnitIndex);

        /// <summary>Moves the player ship one cell up (+1) or down (-1), spending a move point.</summary>
        public bool ApplyMove(int y)
        {
            BattleManager bm = Bm;
            ShipMoveCell cell = bm.playerShip?.shipVisual?.barPanel?.shipMoveCell;
            if (cell == null || bm.turnStatus != TurnStatus.PlayerTurn)
            {
                return false;
            }
            int before = bm.battlePlayerGridOffest;
            cell.OnMove(y);
            HeadlessGame.Settle(300);
            bm.UpdateInfoForce();
            return bm.battlePlayerGridOffest != before;
        }

        public List<int> MoveOptions()
        {
            var result = new List<int>();
            BattleManager bm = Bm;
            if (!bm.inBattle || bm.battleOver || bm.turnStatus != TurnStatus.PlayerTurn)
            {
                return result;
            }
            ShipMoveCell cell = bm.playerShip?.shipVisual?.barPanel?.shipMoveCell;
            if (cell == null || cell.moveCount <= 0)
            {
                return result;
            }
            bool upBlocked = cell.signBlockListUp.Count > 0 && !ShipMoveCell.IsFreeMove(1);
            bool downBlocked = cell.signBlockListDown.Count > 0 && !ShipMoveCell.IsFreeMove(-1);
            if (!upBlocked && (bm.playerShip.shipData.currentMove > 0 || ShipMoveCell.IsFreeMove(1)))
            {
                result.Add(1);
            }
            if (!downBlocked && (bm.playerShip.shipData.currentMove > 0 || ShipMoveCell.IsFreeMove(-1)))
            {
                result.Add(-1);
            }
            return result;
        }

        // ---- token scripts (solver play, replay, layout evaluation) ------------------------------

        /// <summary>
        /// Applies one compact token: "end", "move:1", "load:2@0,1" (hand index) or
        /// "loadp:4/2@2,0" (card identity; order independent).
        /// </summary>
        public bool ApplyToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return true;
            }
            token = token.Trim();
            if (token == "end")
            {
                EndTurn();
                return true;
            }
            if (token.StartsWith("move:", StringComparison.Ordinal))
            {
                return int.TryParse(token.Substring(5), out int y) && ApplyMove(y);
            }
            if (token.StartsWith("load:", StringComparison.Ordinal))
            {
                string[] parts = token.Substring(5).Split('@');
                return parts.Length == 2 && int.TryParse(parts[0], out int handIndex) && LoadAt(handIndex, parts[1]);
            }
            if (token.StartsWith("loadp:", StringComparison.Ordinal))
            {
                string[] parts = token.Substring(6).Split('@');
                if (parts.Length != 2)
                {
                    return false;
                }
                string[] pc = parts[0].Split('/');
                if (pc.Length != 2 || !int.TryParse(pc[0], out int power))
                {
                    return false;
                }
                int[] colors = pc[1].Length == 0 ? Array.Empty<int>() : pc[1].Split(',').Select(int.Parse).ToArray();
                return LoadByCard(power, colors, parts[1]);
            }
            if (TryApplyExtraToken(token, out bool ok))
            {
                return ok;
            }
            Problems.Add("unknown token: " + token);
            return false;
        }

        /// <summary>Applies a ';'-separated token script from the current decision point.</summary>
        public bool SeekPrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return true;
            }
            foreach (string token in prefix.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!ApplyToken(token))
                {
                    Problems.Add("prefix token failed: " + token);
                    return false;
                }
                if (!Bm.inBattle || Bm.battleOver)
                {
                    return true;
                }
            }
            return true;
        }

        private bool LoadAt(int handIndex, string coorText)
        {
            string[] coor = coorText.Split(',');
            if (coor.Length != 2)
            {
                return false;
            }
            return ApplyLoad(handIndex, FindPlayerUnit(int.Parse(coor[0]), int.Parse(coor[1])));
        }

        private ShipUnitController FindPlayerUnit(int x, int y) =>
            Bm.playerShip?.shipUnitControllers?.FirstOrDefault(u => u != null && u.shipCell != null && u.coor.x == x && u.coor.y == y);

        private bool LoadByCard(int power, int[] colors, string coorText)
        {
            List<EnergyData> hand = HandPowers;
            for (int i = 0; i < hand.Count; i++)
            {
                EnergyData energy = hand[i];
                if (energy == null || energy.power != power)
                {
                    continue;
                }
                int[] have = Colors(energy.powerColors);
                if (!have.SequenceEqual(colors))
                {
                    continue;
                }
                return LoadAt(i, coorText);
            }
            return false;
        }

        // ---- simple greedy policy (fast full-battle evaluation) --------------------------------

        /// <summary>
        /// Plays the current player turn with a cheap heuristic policy (no lookahead): each card goes
        /// to the unit whose clash line most needs the power, preferring an exact colour match. Used
        /// to evaluate layouts quickly; the turn search replaces it where precision matters.
        /// </summary>
        public TurnOutcome GreedyTurn() => GreedyTurn(RolloutPolicy.Default);

        public TurnOutcome GreedyTurn(RolloutPolicy policy)
        {
            GreedyTokens(null, policy);
            return EndTurn();
        }

        /// <summary>
        /// The policy's actions for the current turn (applied, turn not ended). A moving policy tries
        /// staying, up and down (each with the policy's loads) and keeps the best clash preview.
        /// </summary>
        public void GreedyTokens(List<string> tokens, RolloutPolicy policy)
        {
            policy ??= RolloutPolicy.Default;
            if (!policy.TryMoves || MoveOptions().Count == 0)
            {
                GreedyLoads(tokens, policy);
                return;
            }
            WorldSnapshot start = WorldSnapshot.Capture();
            int bestMove = 0;
            double best = double.NegativeInfinity;
            foreach (int m in new[] { 0 }.Concat(MoveOptions()))
            {
                start.Restore();
                if (m != 0 && !ApplyMove(m))
                {
                    continue;
                }
                GreedyLoads(null, policy);
                double v = PreviewValue();
                if (v > best)
                {
                    best = v;
                    bestMove = m;
                }
            }
            start.Restore();
            if (bestMove != 0 && ApplyMove(bestMove))
            {
                tokens?.Add("move:" + bestMove);
            }
            GreedyLoads(tokens, policy);
        }

        /// <summary>Damage dealt minus 1.2 × damage taken in the current clash preview, with lethal either way dominating.</summary>
        public double PreviewValue()
        {
            BattleManager bm = Bm;
            bm.UpdateInfoForce();
            int dealt = 0, taken = 0;
            foreach (var kv in bm.battleResultInfos)
            {
                BattleResultInfo info = kv.Value;
                if (info?.playerShipController == null || info.enemyShipController == null)
                {
                    continue;
                }
                if (info.result > 0) dealt += info.result; else taken -= info.result;
            }
            ShipData p = bm.playerShip.shipData, e = bm.enemyShip.shipData;
            int eLife = e.currentHP + e.currentShield, pLife = p.currentHP + p.currentShield;
            double v = 1000.0 * Math.Min(dealt, eLife) - 1200.0 * Math.Min(taken, pLife);
            if (dealt >= eLife) v += 1e6;
            if (taken >= pLife) v -= 1e6;
            return v;
        }

        /// <summary>
        /// The greedy policy's loads for the current turn (without ending it). With
        /// <paramref name="tokens"/> the loads are also returned as loadp tokens, so a search that
        /// scores plans by greedy playouts can always choose the greedy turn itself.
        /// </summary>
        public void GreedyLoads(List<string> tokens) => GreedyLoads(tokens, RolloutPolicy.Default);

        public void GreedyLoads(List<string> tokens, RolloutPolicy policy)
        {
            policy ??= RolloutPolicy.Default;
            BattleManager bm = Bm;
            if (!bm.inBattle || bm.battleOver || bm.turnStatus != TurnStatus.PlayerTurn)
            {
                return;
            }
            for (int step = 0; step < 12; step++)
            {
                LoadOption best = null;
                double bestScore = double.NegativeInfinity;
                foreach (LoadOption option in LoadOptions())
                {
                    ShipUnitController unit = bm.playerShip.shipUnitControllers[option.UnitIndex];
                    if (unit?.shipCell == null)
                    {
                        continue;
                    }
                    int line = bm.GetBattleGridYByPlayerShipY(unit.coor.y);
                    int playerTotal = 0, enemyTotal = 0;
                    if (bm.battleResultInfos.TryGetValue(line, out BattleResultInfo info) && info != null)
                    {
                        playerTotal = info.playerTotal;
                        enemyTotal = info.enemyTotal;
                    }
                    int deficit = Math.Max(0, enemyTotal - playerTotal);
                    int slotRare = FirstFittingSlotRare(unit, option.Colors.Length == 0 ? 0 : option.Colors.Max());
                    double score = Math.Min(option.Power, deficit) * policy.Deficit + option.Power * policy.Power;
                    if (slotRare == (option.Colors.Length == 0 ? 0 : option.Colors.Max()))
                    {
                        score += policy.Match;
                    }
                    if (playerTotal > enemyTotal)
                    {
                        score -= policy.Overkill * Math.Min(option.Power, playerTotal - enemyTotal);
                    }
                    if (policy.EmptyLine != 0 && (info?.playerShipController == null || info.enemyShipController == null))
                    {
                        score -= policy.EmptyLine * option.Power;
                    }
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = option;
                    }
                }
                if (best == null || bestScore <= double.NegativeInfinity)
                {
                    break;
                }
                string token = $"loadp:{best.Power}/{string.Join(",", best.Colors)}@{best.X},{best.Y}";
                if (!ApplyLoad(best.HandIndex, best.UnitIndex))
                {
                    break;
                }
                tokens?.Add(token);
            }
        }

        private static int FirstFittingSlotRare(ShipUnitController unit, int highest)
        {
            foreach (ShipUnitSlot slot in unit.unitSlots)
            {
                if (slot.powerData == null && !slot.isDisturbed && highest >= slot.rare)
                {
                    return slot.rare;
                }
            }
            return -1;
        }

        /// <summary>
        /// Plays the whole battle out with the greedy policy. When <paramref name="endCurrentTurnFirst"/>
        /// is set the turn already in progress is resolved first (its placements are kept), so this can
        /// score a candidate turn plan by the rest of the battle.
        /// </summary>
        public BattleOutcome PlayToEndGreedy(bool endCurrentTurnFirst = false, int maxTurns = 30) =>
            PlayToEndGreedy(endCurrentTurnFirst, maxTurns, RolloutPolicy.Default);

        public BattleOutcome PlayToEndGreedy(bool endCurrentTurnFirst, int maxTurns, RolloutPolicy policy)
        {
            var outcome = new BattleOutcome();
            var sw = Stopwatch.StartNew();
            if (endCurrentTurnFirst && Bm.inBattle && !Bm.battleOver && Bm.turnStatus == TurnStatus.PlayerTurn)
            {
                TurnOutcome first = EndTurn();
                outcome.Turns++;
                outcome.Details.Add(first);
            }
            while (Bm.inBattle && !Bm.battleOver && outcome.Turns < maxTurns)
            {
                if (Bm.turnStatus != TurnStatus.PlayerTurn)
                {
                    WaitForNextPlayerTurn(Bm.round - 1);
                    if (!Bm.inBattle || Bm.battleOver)
                    {
                        break;
                    }
                    if (Bm.turnStatus != TurnStatus.PlayerTurn)
                    {
                        break;
                    }
                }
                TurnOutcome turn = GreedyTurn(policy);
                outcome.Turns++;
                outcome.Details.Add(turn);
                if (turn.BattleEnded)
                {
                    break;
                }
            }
            HeadlessGame.Settle(60 * 30);
            outcome.Seconds = sw.Elapsed.TotalSeconds;
            outcome.Ended = !Bm.inBattle || Bm.battleOver;
            outcome.PlayerHp = Bm.playerShip?.shipData?.currentHP ?? 0;
            outcome.PlayerFuel = Bm.playerShip?.shipData?.currentMove ?? 0;
            outcome.EnemyHp = Bm.enemyShip?.shipData?.currentHP ?? 0;
            outcome.MaxRound = Bm.round;
            outcome.Won = outcome.Ended && (Bm.enemyShip?.isDead ?? false) && !(Bm.playerShip?.isDead ?? true);
            outcome.PlayerHpLost = outcome.Details.Sum(t => t?.PlayerHpLost ?? 0);
            return outcome;
        }

        // ---- active skills (click / two-step target) --------------------------------------------

        /// <summary>Units whose active skill can be clicked at this decision point.</summary>
        public List<SkillOption> SkillOptions()
        {
            SkillTargetCapture.EnsurePatched();
            var result = new List<SkillOption>();
            BattleManager bm = Bm;
            if (!bm.inBattle || bm.battleOver || bm.turnStatus != TurnStatus.PlayerTurn)
            {
                return result;
            }
            for (int i = 0; i < bm.playerShip.shipUnitControllers.Count; i++)
            {
                ShipUnitController unit = bm.playerShip.shipUnitControllers[i];
                if (unit?.skill == null || unit.shipCell == null || unit.destroyed || !unit.shipUnitActive)
                {
                    continue;
                }
                bool clickable;
                try
                {
                    clickable = unit.skill.Clickable();
                }
                catch (Exception ex)
                {
                    Problems.Add("SkillOptions " + unit.unitData?.id + ": " + ex.GetType().Name);
                    continue;
                }
                if (!clickable)
                {
                    continue;
                }
                result.Add(new SkillOption
                {
                    UnitIndex = i,
                    X = unit.coor.x,
                    Y = unit.coor.y,
                    UnitId = unit.unitData?.id ?? 0,
                    Skill = unit.skill.GetType().Name,
                    TwoStep = unit.skill is ITwoStepClick,
                });
            }
            return result;
        }

        /// <summary>
        /// Clicks a unit's active skill through the same entry point the UI uses. For a two-step
        /// skill this opens its target panel; the offered targets are then read with
        /// <see cref="PendingTargets"/> and chosen with <see cref="ApplyTarget"/>.
        /// </summary>
        public bool ApplySkillClick(int unitIndex)
        {
            SkillTargetCapture.EnsurePatched();
            SkillTargetCapture.Reset();
            BattleManager bm = Bm;
            if (unitIndex < 0 || unitIndex >= bm.playerShip.shipUnitControllers.Count)
            {
                return false;
            }
            ShipUnitController unit = bm.playerShip.shipUnitControllers[unitIndex];
            if (unit?.shipUnitSkillActiveHandler == null)
            {
                return false;
            }
            bool ok = unit.shipUnitSkillActiveHandler.Click();
            HeadlessGame.Settle(300);
            bm.UpdateInfoForce();
            return ok;
        }

        public bool ApplySkillClick(int x, int y)
        {
            ShipUnitController unit = FindPlayerUnit(x, y);
            if (unit == null)
            {
                return false;
            }
            return ApplySkillClick(Bm.playerShip.shipUnitControllers.IndexOf(unit));
        }

        /// <summary>Choices offered by the two-step skill that was just clicked (empty otherwise).</summary>
        public List<TargetOption> PendingTargets()
        {
            var result = new List<TargetOption>();
            int index = 0;
            if (SkillTargetCapture.UnitTargets != null)
            {
                foreach (ShipUnitController unit in SkillTargetCapture.UnitTargets)
                {
                    if (unit == null || unit.shipCell == null)
                    {
                        continue;
                    }
                    result.Add(new TargetOption
                    {
                        Index = index++,
                        IsCell = false,
                        X = unit.coor.x,
                        Y = unit.coor.y,
                        UnitId = unit.unitData?.id ?? 0,
                    });
                }
            }
            else if (SkillTargetCapture.CellTargets != null)
            {
                foreach (ShipCell cell in SkillTargetCapture.CellTargets)
                {
                    if (cell == null)
                    {
                        continue;
                    }
                    result.Add(new TargetOption
                    {
                        Index = index++,
                        IsCell = true,
                        X = cell.coor.x,
                        Y = cell.coor.y,
                    });
                }
            }
            return result;
        }

        /// <summary>Chooses one of the pending targets, running the skill's own callback.</summary>
        public bool ApplyTarget(int index)
        {
            if (SkillTargetCapture.UnitTargets != null)
            {
                if (index < 0 || index >= SkillTargetCapture.UnitTargets.Count)
                {
                    return false;
                }
                ShipUnitController target = SkillTargetCapture.UnitTargets[index];
                EndPreviewSafely();
                SkillTargetCapture.UnitAction?.Invoke(target);
                HideSkillPanel();
                HeadlessGame.Settle(300);
                Bm.UpdateInfoForce();
                SkillTargetCapture.Reset();
                return true;
            }
            if (SkillTargetCapture.CellTargets != null)
            {
                if (index < 0 || index >= SkillTargetCapture.CellTargets.Count)
                {
                    return false;
                }
                ShipCell target = SkillTargetCapture.CellTargets[index];
                EndPreviewSafely();
                SkillTargetCapture.CellAction?.Invoke(target);
                HideSkillPanel();
                HeadlessGame.Settle(300);
                Bm.UpdateInfoForce();
                SkillTargetCapture.Reset();
                return true;
            }
            return false;
        }

        /// <summary>Closes a target panel without choosing anything (used when probing options).</summary>
        public void CancelSkillTarget()
        {
            HideSkillPanel();
            SkillTargetCapture.Reset();
            HeadlessGame.Settle(30);
        }

        private static void EndPreviewSafely()
        {
            try
            {
                Singleton<PreviewManager>.Instance().EndPreview();
            }
            catch
            {
                // preview was not running
            }
        }

        private static void HideSkillPanel()
        {
            try
            {
                Singleton<UIManager>.Instance().HidePanel(FilePath.shipDirPanel);
            }
            catch
            {
                // panel never opened
            }
        }

        /// <summary>Ends the player turn and runs the resolution/enemy turn until the next decision.</summary>
        public TurnOutcome EndTurn()
        {
            BattleManager bm = Bm;
            var outcome = new TurnOutcome
            {
                Round = bm.round,
                PlayerHpBefore = bm.playerShip.shipData.currentHP,
                EnemyHpBefore = bm.enemyShip.shipData.currentHP,
                PlayerHp = bm.playerShip.shipData.currentHP,
                PlayerShield = bm.playerShip.shipData.currentShield,
                EnemyHp = bm.enemyShip.shipData.currentHP,
                EnemyShield = bm.enemyShip.shipData.currentShield,
            };
            var sw = Stopwatch.StartNew();
            int frames0 = UnityRuntime.FrameCount;
            bm.EndTurn();
            bool reached = WaitForNextPlayerTurn(outcome.Round);
            outcome.Frames = UnityRuntime.FrameCount - frames0;
            outcome.Seconds = sw.Elapsed.TotalSeconds;
            outcome.TimedOut = !reached;
            if (!reached)
            {
                HeadlessGame.Settle(60 * 30);
            }
            bm = Bm;
            outcome.PlayerHp = bm.playerShip.shipData.currentHP;
            outcome.PlayerShield = bm.playerShip.shipData.currentShield;
            outcome.EnemyHp = bm.enemyShip.shipData.currentHP;
            outcome.EnemyShield = bm.enemyShip.shipData.currentShield;
            outcome.PlayerDead = bm.playerShip.isDead;
            outcome.EnemyDead = bm.enemyShip.isDead;
            outcome.BattleEnded = !bm.inBattle || bm.battleOver;
            outcome.PlayerWon = outcome.BattleEnded && outcome.EnemyDead && !outcome.PlayerDead;
            outcome.PlayerHpLost = Math.Max(0, outcome.PlayerHpBefore - outcome.PlayerHp);
            outcome.EnemyHpLost = Math.Max(0, outcome.EnemyHpBefore - outcome.EnemyHp);
            return outcome;
        }
        /// <summary>Full recorder-schema state (both ships, units, lines, hand, decks, AI cursors) as JSON.</summary>
        public static string Fingerprint() => SolverLogger.Log.ToJson(SolverLogger.Snapshot.Build("fp"));

        /// <summary>True once the battle is no longer running.</summary>
        public bool IsBattleOver()
        {
            BattleManager bm = Bm;
            return !bm.inBattle || bm.battleOver;
        }
    }
}
