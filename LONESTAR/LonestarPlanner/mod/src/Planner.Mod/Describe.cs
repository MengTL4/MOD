using System;
using System.Linq;
using Lean.Localization;
using LonestarPlanner.Core;

namespace LonestarPlanner.Mod
{
    /// <summary>把动作令牌、摆位步骤、战斗结局翻译成给人看的话（中文/英文跟随界面语言）。</summary>
    public static class Describe
    {
        public static string Token(string token)
        {
            try
            {
                if (token == "end") return L.T("结束回合", "End turn");
                if (token.StartsWith("move:"))
                {
                    return int.Parse(token.Substring(5)) > 0
                        ? L.T("整船上移一行", "Move the ship up one row")
                        : L.T("整船下移一行", "Move the ship down one row");
                }
                if (token.StartsWith("loadp:"))
                {
                    string[] parts = token.Substring(6).Split('@');
                    ActionExecutor.ParseCard(parts[0], out int power, out int[] colors);
                    ActionExecutor.ParseXY(parts[1], out int x, out int y);
                    return Card(power, colors) + " → " + UnitAt(x, y);
                }
                if (token.StartsWith("load:"))
                {
                    string[] parts = token.Substring(5).Split('@');
                    ActionExecutor.ParseXY(parts[1], out int x, out int y);
                    int n = int.Parse(parts[0]) + 1;
                    return L.T("第" + n + "张手牌", "Card " + n) + " → " + UnitAt(x, y);
                }
                if (token.StartsWith("swap:"))
                {
                    string[] ab = token.Substring(5).Split('>');
                    ActionExecutor.ParseXY(ab[0], out int ax, out int ay);
                    ActionExecutor.ParseXY(ab[1], out int bx, out int by);
                    return L.T("交换 " + UnitAt(ax, ay) + " 和 " + UnitOrCell(bx, by), "Swap " + UnitAt(ax, ay) + " and " + UnitOrCell(bx, by));
                }
                if (token.StartsWith("skill:"))
                {
                    string body = token.Substring(6);
                    string target = null;
                    int gt = body.IndexOf('>');
                    if (gt >= 0)
                    {
                        target = body.Substring(gt + 1);
                        body = body.Substring(0, gt);
                    }
                    ActionExecutor.ParseXY(body, out int x, out int y);
                    string s = L.T("发动 " + UnitAt(x, y) + " 的技能", "Use the skill of " + UnitAt(x, y));
                    if (target != null)
                    {
                        ActionExecutor.ParseXY(target.Substring(2), out int tx, out int ty);
                        s += " → " + (target.StartsWith("c:") ? Cell(tx, ty) : UnitAt(tx, ty));
                    }
                    return s;
                }
                if (token.StartsWith("paint:"))
                {
                    string[] parts = token.Substring(6).Split('@');
                    ActionExecutor.ParseCard(parts[0], out int power, out int[] colors);
                    int rare = int.Parse(parts[1]);
                    return L.T("给 " + Card(power, colors) + " 涂上" + Paint(rare), "Paint " + Card(power, colors) + " " + Paint(rare));
                }
                if (token.StartsWith("paintpool:"))
                {
                    return L.T("点" + Paint(int.Parse(token.Substring(10))) + "涂料（生成 1 点能量）", "Click the " + Paint(int.Parse(token.Substring(10))) + " paint (new 1-power energy)");
                }
                if (token.StartsWith("choice:"))
                {
                    int n = int.Parse(token.Substring(7)) + 1;
                    return L.T("选择第 " + n + " 个选项", "Pick option " + n);
                }
            }
            catch
            {
                // fall through
            }
            return token;
        }

        public static string Card(int power, int[] colors)
        {
            int top = colors.Length == 0 ? 0 : colors.Max();
            string c = colors.Length > 1 ? L.T("彩", "Prism ") : ColorName(top);
            return c + power;
        }

        public static string ColorName(int color)
        {
            switch (color)
            {
                case 3: return L.T("橙", "Orange ");
                case 2: return L.T("蓝", "Blue ");
                case 1: return L.T("白", "White ");
                default: return L.T("无色", "Colorless ");
            }
        }

        /// <summary>涂料颜色："蓝色" / "blue"。</summary>
        public static string Paint(int rare)
        {
            switch (rare)
            {
                case 3: return L.T("橙色", "orange");
                case 2: return L.T("蓝色", "blue");
                case 1: return L.T("白色", "white");
                default: return "?";
            }
        }

        public static string Cell(int x, int y) => L.T("格", "cell ") + "(" + x + "," + y + ")";

        public static string UnitAt(int x, int y)
        {
            ShipUnitController u = ActionExecutor.PlayerUnit(x, y);
            return u == null ? Cell(x, y) : UnitName(u.unitData);
        }

        public static string UnitOrCell(int x, int y)
        {
            ShipUnitController u = ActionExecutor.PlayerUnit(x, y);
            return u == null ? L.T("空", "empty ") + Cell(x, y) : UnitName(u.unitData);
        }

        public static string UnitName(UnitData unitData)
        {
            try
            {
                var data = unitData?.GetData();
                if (data == null) return unitData == null ? "?" : "#" + unitData.id;
                string text = LeanLocalization.GetTranslationText(data.Name, data.Name, true);
                return string.IsNullOrEmpty(text) ? data.Name : text;
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>"loadp:..;move:1;end" → 一句话，给"下回合预案"/备选用。</summary>
        public static string Plan(string tokens, int max = 6)
        {
            if (string.IsNullOrEmpty(tokens)) return L.T("（无）", "(none)");
            string[] parts = tokens.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            string s = string.Join(L.T("，", ", "), parts.Take(max).Select(t => Token(t.Trim())).ToArray());
            return parts.Length > max ? s + "…" : s;
        }

        /// <summary>船坞里的一步：上船 / 卸下 / 移动 / 交换。</summary>
        public static string Move(LayoutMove m)
        {
            switch (m.Kind)
            {
                case LayoutMove.MoveKind.Deploy: return L.T("上船 ", "Equip ") + m.UnitName + " → " + Cell(m.ToX, m.ToY);
                case LayoutMove.MoveKind.Bench: return L.T("卸下 ", "Unequip ") + m.UnitName + " " + Cell(m.FromX, m.FromY);
                case LayoutMove.MoveKind.Move: return L.T("移动 ", "Move ") + m.UnitName + " " + Cell(m.FromX, m.FromY) + " → " + Cell(m.ToX, m.ToY);
                case LayoutMove.MoveKind.Swap: return L.T("交换 " + Cell(m.FromX, m.FromY) + " 和 " + Cell(m.ToX, m.ToY), "Swap " + Cell(m.FromX, m.FromY) + " and " + Cell(m.ToX, m.ToY)) + "  " + m.UnitName;
                default: return m.ToString();
            }
        }

        /// <summary>Brain 的 Outcome（Kind/Round/Hp/Fuel/Damage）→ 一句话，和颜色。</summary>
        public static string Outcome(LitJson.JsonData o, out UnityEngine.Color color)
        {
            string kind = BrainPlan.Str(o, "Kind");
            int round = BrainPlan.Int(o, "Round"), hp = BrainPlan.Int(o, "Hp"), dmg = BrainPlan.Int(o, "Damage");
            // 后半局是快速打完的（只为显示当前摆位的结果）：实际至少能打成这样
            string atLeast = BrainPlan.Bool(o, "AtLeast") ? L.T("（至少）", " (at least)") : "";
            switch (kind)
            {
                case "win":
                    color = Ui.Good;
                    // 燃料跨战斗保留（旧服务不给 Fuel）
                    bool hasFuel = BrainPlan.Has(o, "Fuel");
                    int fuel = BrainPlan.Int(o, "Fuel");
                    return L.T("胜利 · 第 " + round + " 回合 · 剩 " + hp + " HP" + (hasFuel ? " · 燃料 " + fuel : ""),
                        "Win · round " + round + " · " + hp + " HP" + (hasFuel ? ", " + fuel + " fuel" : "") + " left") + atLeast;
                case "loss":
                    color = Ui.Bad;
                    return L.T("失败 · 第 " + round + " 回合 · 共造成 " + dmg + " 伤害", "Loss · round " + round + " · " + dmg + " damage dealt") + atLeast;
                case "open":
                    color = Ui.Gold;
                    return L.T("未分胜负", "Undecided");
                default:
                    color = Ui.Dim;
                    return L.T("无法模拟", "Could not be simulated");
            }
        }
    }
}
