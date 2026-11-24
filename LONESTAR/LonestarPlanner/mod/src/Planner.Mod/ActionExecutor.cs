using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;

namespace LonestarPlanner.Mod
{
    /// <summary>
    /// 在游戏里执行 Brain 的动作令牌。每种令牌走的入口和无头引擎（LonestarSolver 的
    /// BattleSession）完全一致，所以录制器记下来的动作，求解服务能原样回放、保持同步：
    ///
    ///   loadp:P/C@x,y   PowerController.PowerLoad(部件)
    ///   move:±1         ShipMoveCell.OnMove
    ///   swap:a>b        StartTeleport → TeleportPreview → SetExchange_Battle(-1) → DoTeleportAtEnd
    ///   skill:x,y[>u:a,b|>c:a,b]  ShipUnitSkillActiveHandler.Click() (+ 二段选靶回调)
    ///   choice:j        BattleChoiceButton.Click()
    ///   paint:P/C@r     多彩号：手牌拖到 r 色涂料格上（SolverLogger.Paint，事件序列同 PowerController.OnEndDrag）
    ///   paintpool:r     多彩号 + clickPool：点 r 色涂料格
    ///   end             BattlePanel.EndTurn（和点"结束回合"按钮同一个方法）
    /// </summary>
    public static class ActionExecutor
    {
        public static bool Execute(string token, out string error)
        {
            error = null;
            try
            {
                BattleManager bm = Singleton<BattleManager>.Instance();
                if (bm == null || !bm.inBattle || bm.battleOver)
                {
                    error = L.T("不在战斗中", "Not in battle");
                    return false;
                }
                if (bm.turnStatus != TurnStatus.PlayerTurn)
                {
                    error = L.T("不是玩家回合", "Not your turn");
                    return false;
                }
                if (token == "end") return EndTurn(out error);
                if (token.StartsWith("loadp:")) return LoadByCard(token.Substring(6), out error);
                if (token.StartsWith("load:")) return LoadByIndex(token.Substring(5), out error);
                if (token.StartsWith("move:")) return Move(int.Parse(token.Substring(5)), out error);
                if (token.StartsWith("swap:")) return Swap(token.Substring(5), out error);
                if (token.StartsWith("skill:")) return Skill(token.Substring(6), out error);
                if (token.StartsWith("choice:")) return Choice(int.Parse(token.Substring(7)), out error);
                if (token.StartsWith("paint:")) return PaintCard(token.Substring(6), out error);
                if (token.StartsWith("paintpool:")) return PaintPool(int.Parse(token.Substring(10)), out error);
                error = L.T("未知动作 ", "Unknown action ") + token;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                Debug.LogWarning("[Planner] execute " + token + " failed: " + ex);
                return false;
            }
        }

        // ---- lookups (shared with the HUD) ---------------------------------------------------------

        public static ShipUnitController PlayerUnit(int x, int y)
        {
            BattleManager bm = Singleton<BattleManager>.Instance();
            if (bm?.playerShip?.shipUnitControllers == null) return null;
            return bm.playerShip.shipUnitControllers.FirstOrDefault(u => u != null && u.shipCell != null && u.coor.x == x && u.coor.y == y);
        }

        public static ShipCell PlayerCell(int x, int y)
        {
            BattleManager bm = Singleton<BattleManager>.Instance();
            var grid = bm?.playerShip?.shipData?.grid;
            if (grid == null || x < 0 || y < 0 || x >= grid.width || y >= grid.height) return null;
            return grid.GetContent(x, y);
        }

        public static List<EnergyData> Hand()
        {
            PowerManager pm = Singleton<PowerManager>.Instance();
            return pm == null ? new List<EnergyData>() : SolverLogger.Snap.HandPowers(pm) ?? new List<EnergyData>();
        }

        public static int[] Colors(EnergyData e) =>
            e?.powerColors?.colorList == null ? new int[0] : e.powerColors.colorList.Select(c => (int)c).ToArray();

        /// <summary>与引擎 LoadByCard 一致：按手牌顺序找第一张点数和颜色完全相同的牌。</summary>
        public static EnergyData FindCard(int power, int[] colors)
        {
            List<EnergyData> hand = Hand();
            EnergyData exact = hand.FirstOrDefault(e => e != null && e.power == power && Colors(e).SequenceEqual(colors));
            if (exact != null) return exact;
            // 本地估算方案只知道最高颜色
            int highest = colors.Length == 0 ? 0 : colors.Max();
            return hand.FirstOrDefault(e => e != null && e.power == power && (Colors(e).Length == 0 ? 0 : Colors(e).Max()) == highest);
        }

        public static bool ParseCard(string spec, out int power, out int[] colors)
        {
            power = 0;
            colors = new int[0];
            string[] pc = spec.Split('/');
            if (pc.Length != 2 || !int.TryParse(pc[0], out power)) return false;
            colors = pc[1].Length == 0 ? new int[0] : pc[1].Split(',').Select(int.Parse).ToArray();
            return true;
        }

        public static bool ParseXY(string text, out int x, out int y)
        {
            x = y = 0;
            string[] p = text.Split(',');
            return p.Length == 2 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y);
        }

        // ---- actions ---------------------------------------------------------------------------

        /// <summary>多彩号：把手牌拖到涂料格上（和无头引擎同一段代码，见 SolverLogger.Paint）。</summary>
        private static bool PaintCard(string body, out string error)
        {
            error = null;
            string[] parts = body.Split('@');
            if (parts.Length != 2 || !ParseCard(parts[0], out int power, out int[] colors) || !int.TryParse(parts[1], out int rare))
            {
                error = L.T("格式错误", "Bad action format");
                return false;
            }
            EnergyData card = FindCard(power, colors);
            if (card?.powerController == null)
            {
                error = L.T("手牌里没有 " + power + " 点的这张牌", "No " + power + "-power card like this in hand");
                return false;
            }
            if (!SolverLogger.Paint.Apply(card, rare))
            {
                error = L.T("涂不上（涂料用完了，或这张牌已经有这个颜色）", "Cannot paint (no paint left, or the card already has that colour)");
                return false;
            }
            return true;
        }

        private static bool PaintPool(int rare, out string error)
        {
            error = null;
            if (!SolverLogger.Paint.ApplyPool(rare))
            {
                error = L.T("涂料用不了", "Cannot use that paint");
                return false;
            }
            return true;
        }

        private static bool LoadByCard(string body, out string error)
        {
            error = null;
            string[] parts = body.Split('@');
            if (parts.Length != 2 || !ParseCard(parts[0], out int power, out int[] colors) || !ParseXY(parts[1], out int x, out int y))
            {
                error = L.T("格式错误", "Bad action format");
                return false;
            }
            EnergyData card = FindCard(power, colors);
            if (card?.powerController == null)
            {
                error = L.T("手牌里没有 " + power + " 点的这张牌", "No " + power + "-power card like this in hand");
                return false;
            }
            return Load(card, x, y, out error);
        }

        private static bool LoadByIndex(string body, out string error)
        {
            error = null;
            string[] parts = body.Split('@');
            List<EnergyData> hand = Hand();
            if (parts.Length != 2 || !int.TryParse(parts[0], out int i) || i < 0 || i >= hand.Count || !ParseXY(parts[1], out int x, out int y))
            {
                error = L.T("格式错误", "Bad action format");
                return false;
            }
            return Load(hand[i], x, y, out error);
        }

        private static bool Load(EnergyData card, int x, int y, out string error)
        {
            error = null;
            ShipUnitController unit = PlayerUnit(x, y);
            if (unit == null)
            {
                error = L.T($"({x},{y}) 没有部件", $"No unit at ({x},{y})");
                return false;
            }
            if (!card.powerController.PowerLoad(unit))
            {
                error = L.T("游戏拒绝装载", "The game refused to load the card");
                return false;
            }
            return true;
        }

        private static bool Move(int y, out string error)
        {
            error = null;
            BattleManager bm = Singleton<BattleManager>.Instance();
            ShipMoveCell cell = bm.playerShip?.shipVisual?.barPanel?.shipMoveCell;
            if (cell == null)
            {
                error = L.T("找不到移动控件", "Move control not found");
                return false;
            }
            int before = bm.battlePlayerGridOffest;
            cell.OnMove(y);
            if (bm.battlePlayerGridOffest == before)
            {
                error = L.T("游戏拒绝移动", "The game refused the move");
                return false;
            }
            return true;
        }

        private static bool Swap(string body, out string error)
        {
            error = null;
            string[] ab = body.Split('>');
            if (ab.Length != 2 || !ParseXY(ab[0], out int ax, out int ay) || !ParseXY(ab[1], out int bx, out int by))
            {
                error = L.T("格式错误", "Bad action format");
                return false;
            }
            BattleManager bm = Singleton<BattleManager>.Instance();
            var ship = bm.playerShip as PlayerExchangeShipController;
            if (ship == null)
            {
                error = L.T("这艘船不能交换", "This ship cannot swap");
                return false;
            }
            if (ship.shipData.currentExchange <= 0)
            {
                error = L.T("交换次数用完了", "No swaps left");
                return false;
            }
            ShipCell a = PlayerCell(ax, ay);
            ShipCell b = PlayerCell(bx, by);
            if (a?.shipUnitController == null || b == null || a == b || b.shipCellStatus == ShipCellStatus.None)
            {
                error = L.T("格子不可交换", "That cell cannot be swapped");
                return false;
            }
            ShipUnitController unit = a.shipUnitController;
            if (unit.skill is IClick)
            {
                Buff_Click buff = unit.GetBuff<Buff_Click>();
                if (buff != null && buff.isPlayer && buff.CheckClickable())
                {
                    error = L.T("该部件现在拖动会触发技能", "Dragging this unit now would trigger its skill");
                    return false;
                }
            }
            ship.StartTeleport(a);
            ship.TeleportPreview(b);
            ship.shipData.SetExchange_Battle(-1);
            ship.DoTeleportAtEnd();
            return true;
        }

        private static bool Skill(string body, out string error)
        {
            error = null;
            string target = null;
            int gt = body.IndexOf('>');
            if (gt >= 0)
            {
                target = body.Substring(gt + 1);
                body = body.Substring(0, gt);
            }
            if (!ParseXY(body, out int x, out int y))
            {
                error = L.T("格式错误", "Bad action format");
                return false;
            }
            ShipUnitController unit = PlayerUnit(x, y);
            if (unit?.skill == null || unit.shipUnitSkillActiveHandler == null || !unit.skill.Clickable())
            {
                error = L.T("技能现在不能用", "The skill cannot be used now");
                return false;
            }
            TargetCapture.Reset();
            if (!unit.shipUnitSkillActiveHandler.Click())
            {
                error = L.T("游戏拒绝点击技能", "The game refused the skill");
                return false;
            }
            if (target == null)
            {
                return true;
            }
            bool isCell = target.StartsWith("c:");
            if (!ParseXY(target.Substring(2), out int tx, out int ty))
            {
                error = L.T("目标格式错误", "Bad target format");
                return false;
            }
            bool ok = isCell ? TargetCapture.ChooseCell(tx, ty) : TargetCapture.ChooseUnit(tx, ty);
            if (!ok)
            {
                TargetCapture.Cancel();
                error = L.T("目标不在可选列表里", "The target cannot be selected");
            }
            return ok;
        }

        private static bool Choice(int index, out string error)
        {
            error = null;
            BattleChoicePanel panel = Singleton<UIManager>.Instance().GetPanel<BattleChoicePanel>(FilePath.battleChoicePanel);
            if (panel?.buttons == null || index < 0 || index >= panel.buttons.Length)
            {
                error = L.T("没有这个选项", "No such option");
                return false;
            }
            BattleChoiceButton b = panel.buttons[index];
            if (b == null || !b.gameObject.activeSelf || b.option == null || !b.option.check)
            {
                error = L.T("选项不可用", "Option unavailable");
                return false;
            }
            b.Click();
            return true;
        }

        private static readonly MethodInfo PanelEndTurn = AccessTools.Method(typeof(BattlePanel), "EndTurn");

        private static bool EndTurn(out string error)
        {
            error = null;
            BattlePanel panel = Singleton<UIManager>.Instance().GetPanel<BattlePanel>(FilePath.battlePanel);
            if (panel == null || PanelEndTurn == null)
            {
                error = L.T("找不到战斗面板", "Battle panel not found");
                return false;
            }
            PanelEndTurn.Invoke(panel, null);
            return true;
        }
    }

    /// <summary>
    /// 二段技能的选靶面板（ShipDirPanel）回调捕获。优先级最低：录制器的前缀先把回调包一层
    /// （记录 act_target），我们拿到的是包过的那个，所以自动选靶同样会被录下来。
    /// </summary>
    public static class TargetCapture
    {
        public static List<ShipUnitController> UnitTargets;
        public static UnityAction<ShipUnitController> UnitAction;
        public static List<ShipCell> CellTargets;
        public static UnityAction<ShipCell> CellAction;

        public static void Reset()
        {
            UnitTargets = null;
            UnitAction = null;
            CellTargets = null;
            CellAction = null;
        }

        public static bool ChooseUnit(int x, int y)
        {
            ShipUnitController t = UnitTargets?.FirstOrDefault(u => u != null && u.shipCell != null && u.coor.x == x && u.coor.y == y);
            if (t == null || UnitAction == null) return false;
            EndPreview();
            UnitAction(t);
            HidePanel();
            Reset();
            return true;
        }

        public static bool ChooseCell(int x, int y)
        {
            ShipCell t = CellTargets?.FirstOrDefault(c => c != null && c.coor.x == x && c.coor.y == y);
            if (t == null || CellAction == null) return false;
            EndPreview();
            CellAction(t);
            HidePanel();
            Reset();
            return true;
        }

        public static void Cancel()
        {
            HidePanel();
            Reset();
        }

        private static void EndPreview()
        {
            try
            {
                Singleton<PreviewManager>.Instance().EndPreview();
            }
            catch
            {
                // 没有预览
            }
        }

        private static void HidePanel()
        {
            try
            {
                Singleton<UIManager>.Instance().HidePanel(FilePath.shipDirPanel);
            }
            catch
            {
                // 面板没开
            }
        }
    }

    [HarmonyPatch(typeof(ShipDirPanel), nameof(ShipDirPanel.Init),
        typeof(List<ShipUnitController>), typeof(UnityAction<ShipUnitController>), typeof(UnityAction<ShipUnitController>))]
    [HarmonyPriority(Priority.Last)]
    internal static class Patch_CaptureUnitTargets
    {
        private static void Prefix(List<ShipUnitController> shipUnitControllers, ref UnityAction<ShipUnitController> action)
        {
            TargetCapture.UnitTargets = shipUnitControllers?.Where(u => u != null).ToList();
            TargetCapture.UnitAction = action;
        }
    }

    [HarmonyPatch(typeof(ShipDirPanel), nameof(ShipDirPanel.Init),
        typeof(List<ShipCell>), typeof(UnityAction<ShipCell>), typeof(UnityAction<ShipCell>))]
    [HarmonyPriority(Priority.Last)]
    internal static class Patch_CaptureCellTargets
    {
        private static void Prefix(List<ShipCell> shipCells, ref UnityAction<ShipCell> action)
        {
            TargetCapture.CellTargets = shipCells?.Where(c => c != null).ToList();
            TargetCapture.CellAction = action;
        }
    }
}
