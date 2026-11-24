using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityEngine;

namespace Lonestar.Engine
{
    /// <summary>
    /// The complete player action language at a decision point, as compact tokens:
    ///
    ///   loadp:P/C1,C2@x,y   load the hand card with power P and colours C onto the unit at (x,y)
    ///   load:H@x,y          load the H-th hand card (hand order)
    ///   move:1 / move:-1    move the ship one row up / down
    ///   swap:x1,y1>x2,y2    exchange the unit at (x1,y1) with the cell (x2,y2) (spends 1 exchange)
    ///   skill:x,y           click the active skill of the unit at (x,y)
    ///   skill:x,y>u:a,b     two-step skill aimed at the unit at (a,b)
    ///   skill:x,y>c:a,b     two-step skill aimed at the cell (a,b)
    ///   choice:j            pick the j-th button of the battle choice panel
    ///   paint:P/C@r         多彩号: drop the hand card P/C on the paint pot of colour r (adds colour r)
    ///   paintpool:r         多彩号 with clickPool: click the pot of colour r (new 1-power card)
    ///   end                 end the turn (runs resolution and the enemy turn)
    ///
    /// Every token goes through the same game entry point the UI uses.
    /// </summary>
    public sealed partial class BattleSession
    {
        // ---- enumeration -------------------------------------------------------------------------

        /// <summary>
        /// Every distinct legal action at this decision point ("end" last). Probing two-step skill
        /// targets clicks the skill and closes its panel again, which can leave transient UI state
        /// behind: callers that need the exact state restore a snapshot afterwards.
        /// </summary>
        public List<string> LegalActions(bool includeSwaps = true, bool includeSkills = true)
        {
            var tokens = new List<string>();
            BattleManager bm = Bm;
            if (!bm.inBattle || bm.battleOver || bm.turnStatus != TurnStatus.PlayerTurn)
            {
                return tokens;
            }
            if (ChoicePending())
            {
                foreach (int j in ChoiceOptions())
                {
                    tokens.Add("choice:" + j);
                }
                return tokens;
            }
            var seen = new HashSet<string>();
            foreach (LoadOption o in LoadOptions())
            {
                string t = $"loadp:{o.Power}/{string.Join(",", o.Colors)}@{o.X},{o.Y}";
                if (seen.Add(t))
                {
                    tokens.Add(t);
                }
            }
            tokens.AddRange(PaintTokens());
            foreach (int y in MoveOptions())
            {
                tokens.Add("move:" + y);
            }
            if (includeSwaps)
            {
                tokens.AddRange(SwapOptions());
            }
            if (includeSkills)
            {
                tokens.AddRange(SkillTokens());
            }
            tokens.Add("end");
            return tokens;
        }

        /// <summary>Diagnostics: BRAIN_NOPAINT=1 leaves paint out of the search (baseline for 多彩号 comparisons).</summary>
        public static bool PaintDisabled = Environment.GetEnvironmentVariable("BRAIN_NOPAINT") == "1";

        /// <summary>多彩号: every hand card (once per identity) on every pot with points left whose colour it lacks.</summary>
        private static List<string> PaintTokens()
        {
            var tokens = new List<string>();
            List<ColorSlotCell> pots = PaintDisabled ? new List<ColorSlotCell>() : SolverLogger.Paint.Pots();
            if (pots.Count == 0)
            {
                return tokens;
            }
            if (SolverLogger.Paint.PoolMode)
            {
                foreach (ColorSlotCell pot in pots.Where(p => SolverLogger.Paint.PointsLeft(p) != 0))
                {
                    tokens.Add("paintpool:" + pot.rare);
                }
                return tokens;
            }
            var seen = new HashSet<string>();
            foreach (EnergyData energy in HandPowers)
            {
                if (energy == null)
                {
                    continue;
                }
                string card = $"{energy.power}/{string.Join(",", Colors(energy.powerColors))}";
                if (!seen.Add(card))
                {
                    continue;
                }
                foreach (ColorSlotCell pot in pots)
                {
                    if (SolverLogger.Paint.CanPaint(energy, pot))
                    {
                        tokens.Add($"paint:{card}@{pot.rare}");
                    }
                }
            }
            return tokens;
        }

        /// <summary>Paints the hand card P/C with colour <paramref name="rare"/> (see <see cref="SolverLogger.Paint"/>).</summary>
        public bool ApplyPaint(int power, int[] colors, int rare)
        {
            EnergyData energy = HandPowers.FirstOrDefault(e => e != null && e.power == power && Colors(e.powerColors).SequenceEqual(colors));
            if (energy == null || !SolverLogger.Paint.Apply(energy, rare))
            {
                return false;
            }
            HeadlessGame.Settle(300);
            Bm.UpdateInfoForce();
            return true;
        }

        public bool ApplyPaintPool(int rare)
        {
            if (!SolverLogger.Paint.ApplyPool(rare))
            {
                return false;
            }
            HeadlessGame.Settle(300);
            Bm.UpdateInfoForce();
            return true;
        }

        private List<string> SkillTokens()
        {
            var tokens = new List<string>();
            foreach (SkillOption s in SkillOptions())
            {
                if (!s.TwoStep)
                {
                    tokens.Add($"skill:{s.X},{s.Y}");
                    continue;
                }
                try
                {
                    if (ApplySkillClick(s.UnitIndex))
                    {
                        foreach (TargetOption t in PendingTargets())
                        {
                            tokens.Add($"skill:{s.X},{s.Y}>{(t.IsCell ? "c" : "u")}:{t.X},{t.Y}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Problems.Add("skill probe " + s.UnitId + ": " + ex.GetType().Name);
                }
                CancelSkillTarget();
            }
            return tokens;
        }

        // ---- exchange ----------------------------------------------------------------------------

        private static PlayerExchangeShipController ExchangeShip => Bm.playerShip as PlayerExchangeShipController;

        /// <summary>Whether the unit at <paramref name="cell"/> can start an exchange drag (ShipUnitVisual.DragBeginActionExchange).</summary>
        private static bool CanStartExchange(ShipCell cell)
        {
            BattleManager bm = Bm;
            if (bm.turnStatus != TurnStatus.PlayerTurn || ExchangeShip == null || bm.playerShip.shipData.currentExchange <= 0)
            {
                return false;
            }
            ShipUnitController unit = cell?.shipUnitController;
            if (unit == null || unit.destroyed)
            {
                return false;
            }
            if (unit.skill is IClick)
            {
                Buff_Click buff = unit.GetBuff<Buff_Click>();
                if (buff != null && buff.isPlayer && buff.CheckClickable())
                {
                    return false; // dragging this unit clicks its skill instead
                }
            }
            return true;
        }

        private static IEnumerable<ShipCell> PlayerCells()
        {
            GridMap<ShipCell> grid = Bm.playerShip?.shipData?.grid;
            if (grid == null)
            {
                yield break;
            }
            for (int x = 0; x < grid.width; x++)
            {
                for (int y = 0; y < grid.height; y++)
                {
                    ShipCell c = grid.GetContent(x, y);
                    if (c != null && c.shipCellStatus != ShipCellStatus.None)
                    {
                        yield return c;
                    }
                }
            }
        }

        /// <summary>Distinct exchanges: unit↔unit once per pair, unit→empty cell for every empty cell.</summary>
        public List<string> SwapOptions()
        {
            var tokens = new List<string>();
            if (ExchangeShip == null || Bm.playerShip.shipData.currentExchange <= 0 || Bm.turnStatus != TurnStatus.PlayerTurn)
            {
                return tokens;
            }
            List<ShipCell> cells = PlayerCells().ToList();
            var pairs = new HashSet<(int, int, int, int)>();
            foreach (ShipCell a in cells)
            {
                if (!CanStartExchange(a))
                {
                    continue;
                }
                foreach (ShipCell b in cells)
                {
                    if (b == a)
                    {
                        continue;
                    }
                    bool bUnit = b.shipUnitController != null;
                    if (bUnit && CanStartExchange(b))
                    {
                        // the same swap can start from either side; keep one
                        var key = Order(a.coor, b.coor);
                        if (!pairs.Add(key))
                        {
                            continue;
                        }
                    }
                    tokens.Add($"swap:{a.coor.x},{a.coor.y}>{b.coor.x},{b.coor.y}");
                }
            }
            return tokens;
        }

        private static (int, int, int, int) Order(Vector2Int a, Vector2Int b) =>
            (a.x, a.y).CompareTo((b.x, b.y)) <= 0 ? (a.x, a.y, b.x, b.y) : (b.x, b.y, a.x, a.y);

        /// <summary>
        /// Exchanges the unit at (ax,ay) with the cell (bx,by) the way a completed drag does:
        /// StartTeleport → TeleportPreview (hover) → SetExchange_Battle(-1) → DoTeleportAtEnd.
        /// </summary>
        public bool ApplyExchange(int ax, int ay, int bx, int by)
        {
            PlayerExchangeShipController ship = ExchangeShip;
            if (ship == null)
            {
                return false;
            }
            ShipCell a = ship.shipData.grid.GetContent(ax, ay);
            ShipCell b = ship.shipData.grid.GetContent(bx, by);
            if (a == null || b == null || a == b || b.shipCellStatus == ShipCellStatus.None || !CanStartExchange(a))
            {
                return false;
            }
            ship.StartTeleport(a);
            ship.TeleportPreview(b);
            ship.shipData.SetExchange_Battle(-1);
            ship.DoTeleportAtEnd();
            HeadlessGame.Settle(300);
            Bm.UpdateInfoForce();
            return true;
        }

        // ---- battle choices ------------------------------------------------------------------------

        private static BattleChoicePanel ChoicePanel()
        {
            try
            {
                return Singleton<UIManager>.Instance().GetPanel<BattleChoicePanel>(FilePath.battleChoicePanel);
            }
            catch
            {
                return null;
            }
        }

        public static bool ChoicePending()
        {
            if (BattleChoiceGroup.groups.Count == 0)
            {
                return false;
            }
            BattleChoicePanel panel = ChoicePanel();
            return panel != null && panel.buttons != null && panel.buttons.Any(IsLiveChoice);
        }

        private static bool IsLiveChoice(BattleChoiceButton b) =>
            b != null && b.gameObject.activeSelf && b.option != null && b.option.check;

        public static List<int> ChoiceOptions()
        {
            var result = new List<int>();
            BattleChoicePanel panel = ChoicePanel();
            if (panel?.buttons == null)
            {
                return result;
            }
            for (int j = 0; j < panel.buttons.Length; j++)
            {
                if (IsLiveChoice(panel.buttons[j]))
                {
                    result.Add(j);
                }
            }
            return result;
        }

        public bool ApplyChoice(int index)
        {
            BattleChoicePanel panel = ChoicePanel();
            if (panel?.buttons == null || index < 0 || index >= panel.buttons.Length || !IsLiveChoice(panel.buttons[index]))
            {
                return false;
            }
            panel.buttons[index].Click();
            HeadlessGame.Settle(300);
            Bm.UpdateInfoForce();
            return true;
        }

        // ---- skills by coordinate ------------------------------------------------------------------

        /// <summary>Clicks the skill at (x,y); for a two-step skill also picks the target given as "u:a,b" / "c:a,b".</summary>
        public bool ApplySkill(int x, int y, string target)
        {
            if (!SkillOptions().Any(o => o.X == x && o.Y == y))
            {
                return false;
            }
            if (!ApplySkillClick(x, y))
            {
                return false;
            }
            List<TargetOption> targets = PendingTargets();
            if (target == null)
            {
                if (targets.Count > 0)
                {
                    CancelSkillTarget(); // a two-step skill needs a target
                    return false;
                }
                return true;
            }
            bool isCell = target.StartsWith("c:", StringComparison.Ordinal);
            int[] c = target.Substring(2).Split(',').Select(int.Parse).ToArray();
            TargetOption pick = targets.FirstOrDefault(t => t.IsCell == isCell && t.X == c[0] && t.Y == c[1]);
            if (pick == null)
            {
                CancelSkillTarget();
                return false;
            }
            return ApplyTarget(pick.Index);
        }

        // ---- token dispatch --------------------------------------------------------------------------

        private bool TryApplyExtraToken(string token, out bool ok)
        {
            ok = false;
            if (token.StartsWith("swap:", StringComparison.Ordinal))
            {
                string[] ab = token.Substring(5).Split('>');
                if (ab.Length == 2)
                {
                    int[] a = ab[0].Split(',').Select(int.Parse).ToArray();
                    int[] b = ab[1].Split(',').Select(int.Parse).ToArray();
                    ok = ApplyExchange(a[0], a[1], b[0], b[1]);
                }
                return true;
            }
            if (token.StartsWith("skill:", StringComparison.Ordinal))
            {
                string body = token.Substring(6);
                string target = null;
                int gt = body.IndexOf('>');
                if (gt >= 0)
                {
                    target = body.Substring(gt + 1);
                    body = body.Substring(0, gt);
                }
                int[] xy = body.Split(',').Select(int.Parse).ToArray();
                ok = ApplySkill(xy[0], xy[1], target);
                return true;
            }
            if (token.StartsWith("choice:", StringComparison.Ordinal))
            {
                ok = int.TryParse(token.Substring(7), out int j) && ApplyChoice(j);
                return true;
            }
            if (token.StartsWith("paint:", StringComparison.Ordinal))
            {
                // paint:P/C1,C2@r
                string[] parts = token.Substring(6).Split('@');
                string[] pc = parts[0].Split('/');
                if (parts.Length == 2 && pc.Length == 2 && int.TryParse(pc[0], out int power) && int.TryParse(parts[1], out int rare))
                {
                    int[] colors = pc[1].Length == 0 ? Array.Empty<int>() : pc[1].Split(',').Select(int.Parse).ToArray();
                    ok = ApplyPaint(power, colors, rare);
                }
                return true;
            }
            if (token.StartsWith("paintpool:", StringComparison.Ordinal))
            {
                ok = int.TryParse(token.Substring(10), out int rare) && ApplyPaintPool(rare);
                return true;
            }
            if (token.StartsWith("click:", StringComparison.Ordinal))
            {
                // legacy tools/Solve spelling: click:x,y (optionally followed by target:i)
                int[] xy = token.Substring(6).Split(',').Select(int.Parse).ToArray();
                ok = ApplySkillClick(xy[0], xy[1]);
                return true;
            }
            if (token.StartsWith("target:", StringComparison.Ordinal))
            {
                ok = int.TryParse(token.Substring(7), out int i) && ApplyTarget(i);
                return true;
            }
            return false;
        }

        // ---- recorded actions beyond load/move/end ---------------------------------------------------

        private bool ApplyRecordedExtra(JsonElement action)
        {
            switch (Recording.Type(action))
            {
                case "act_click":
                {
                    JsonElement unit = action.GetProperty("unit");
                    if (unit.ValueKind != JsonValueKind.Object || !unit.GetProperty("player").GetBoolean())
                    {
                        return true; // enemy AI clicks happen inside the enemy turn on their own
                    }
                    JsonElement coor = unit.GetProperty("coor");
                    if (!ApplySkillClick(coor[0].GetInt32(), coor[1].GetInt32()))
                    {
                        Problems.Add($"#{N(action)} act_click: click failed");
                    }
                    return true;
                }
                case "act_target":
                {
                    bool isCell = action.GetProperty("kind").GetString() == "cell";
                    JsonElement t = action.GetProperty("target");
                    JsonElement coor = isCell ? t : t.GetProperty("coor");
                    int x = coor[0].GetInt32(), y = coor[1].GetInt32();
                    TargetOption pick = PendingTargets().FirstOrDefault(o => o.IsCell == isCell && o.X == x && o.Y == y);
                    if (pick == null || !ApplyTarget(pick.Index))
                    {
                        Problems.Add($"#{N(action)} act_target: target ({x},{y}) not offered");
                    }
                    return true;
                }
                case "act_exchange":
                {
                    JsonElement from = action.GetProperty("start_coor");
                    JsonElement to = action.GetProperty("start_cell");
                    if (to.ValueKind != JsonValueKind.Array ||
                        !ApplyExchange(from[0].GetInt32(), from[1].GetInt32(), to[0].GetInt32(), to[1].GetInt32()))
                    {
                        Problems.Add($"#{N(action)} act_exchange: exchange failed");
                    }
                    return true;
                }
                case "act_paint":
                {
                    JsonElement e = action.GetProperty("energy");
                    int[] colors = e[1].EnumerateArray().Select(c => c.GetInt32()).ToArray();
                    if (!ApplyPaint(e[0].GetInt32(), colors, action.GetProperty("rare").GetInt32()))
                    {
                        Problems.Add($"#{N(action)} act_paint: paint failed");
                    }
                    return true;
                }
                case "act_paint_pool":
                    if (!ApplyPaintPool(action.GetProperty("rare").GetInt32()))
                    {
                        Problems.Add($"#{N(action)} act_paint_pool: click failed");
                    }
                    return true;
                case "act_choice":
                {
                    int index = -1;
                    if (action.TryGetProperty("index", out JsonElement ix) && ix.ValueKind == JsonValueKind.Number)
                    {
                        index = ix.GetInt32();
                    }
                    else if (action.TryGetProperty("option", out JsonElement name))
                    {
                        BattleChoicePanel panel = ChoicePanel();
                        index = ChoiceOptions().FirstOrDefault(j => panel.buttons[j].option.GetType().Name == name.GetString(), -1);
                    }
                    if (index < 0 || !ApplyChoice(index))
                    {
                        Problems.Add($"#{N(action)} act_choice: option not available");
                    }
                    return true;
                }
                default:
                    return false;
            }
        }

        private static long N(JsonElement r) => r.GetProperty("n").GetInt64();

        /// <summary>
        /// The recorded player decisions of the current turn as action tokens (all action types).
        /// Two-step skills appear as one skill:x,y>... token.
        /// </summary>
        public List<string> RecordedTurnTokens()
        {
            var tokens = new List<string>();
            string pendingSkill = null;
            for (int i = applied; i < recordedActions.Count; i++)
            {
                JsonElement a = recordedActions[i];
                switch (Recording.Type(a))
                {
                    case "act_load":
                    {
                        JsonElement e = a.GetProperty("energy");
                        JsonElement coor = a.GetProperty("target").GetProperty("coor");
                        string colors = string.Join(",", e[1].EnumerateArray().Select(c => c.GetInt32()));
                        tokens.Add($"loadp:{e[0].GetInt32()}/{colors}@{coor[0].GetInt32()},{coor[1].GetInt32()}");
                        break;
                    }
                    case "act_move":
                        if (!a.TryGetProperty("ship_is_player", out JsonElement p) || p.GetBoolean())
                        {
                            tokens.Add("move:" + a.GetProperty("y").GetInt32());
                        }
                        break;
                    case "act_exchange":
                    {
                        JsonElement f = a.GetProperty("start_coor"), t = a.GetProperty("start_cell");
                        tokens.Add($"swap:{f[0].GetInt32()},{f[1].GetInt32()}>{t[0].GetInt32()},{t[1].GetInt32()}");
                        break;
                    }
                    case "act_click":
                    {
                        JsonElement u = a.GetProperty("unit");
                        if (u.ValueKind == JsonValueKind.Object && u.GetProperty("player").GetBoolean())
                        {
                            JsonElement c = u.GetProperty("coor");
                            string t = $"skill:{c[0].GetInt32()},{c[1].GetInt32()}";
                            if (a.TryGetProperty("two_step", out JsonElement ts) && ts.GetBoolean())
                            {
                                pendingSkill = t;
                            }
                            else
                            {
                                tokens.Add(t);
                            }
                        }
                        break;
                    }
                    case "act_target":
                    {
                        bool isCell = a.GetProperty("kind").GetString() == "cell";
                        JsonElement t = a.GetProperty("target");
                        JsonElement c = isCell ? t : t.GetProperty("coor");
                        if (pendingSkill != null)
                        {
                            tokens.Add($"{pendingSkill}>{(isCell ? "c" : "u")}:{c[0].GetInt32()},{c[1].GetInt32()}");
                            pendingSkill = null;
                        }
                        break;
                    }
                    case "act_paint":
                    {
                        JsonElement e = a.GetProperty("energy");
                        string colors = string.Join(",", e[1].EnumerateArray().Select(c => c.GetInt32()));
                        tokens.Add($"paint:{e[0].GetInt32()}/{colors}@{a.GetProperty("rare").GetInt32()}");
                        break;
                    }
                    case "act_paint_pool":
                        tokens.Add("paintpool:" + a.GetProperty("rare").GetInt32());
                        break;
                    case "act_choice":
                        if (a.TryGetProperty("index", out JsonElement ix))
                        {
                            tokens.Add("choice:" + ix.GetInt32());
                        }
                        break;
                    case "act_end_turn":
                        if (IsPlayerCaller(a, "BattlePanel.EndTurn"))
                        {
                            tokens.Add("end");
                            return tokens;
                        }
                        break;
                }
            }
            return tokens;
        }
    }
}
