using System;
using System.Linq;
using UnityEngine;

namespace LonestarPlanner.Mod
{
    /// <summary>
    /// 战斗内界面：
    ///   · 棋盘标注：接下来几步直接画在船上 —— 要动的部件、要用的手牌、交换箭头、步骤序号；
    ///   · 侧边面板：预计结局、本回合收支、求解进度、按顺序的操作清单、每条对撞线的预测、下回合预案；
    ///   · 操作：下一步(F5) / 执行本回合 / 自动接管(F7) / 重新求解，F8 显示/隐藏，标题栏可折叠。
    /// 船坞由 <see cref="PlannerOverlay"/> 负责。
    /// </summary>
    public class PlannerHud : MonoBehaviour
    {
        private readonly TurnPilot pilot = new TurnPilot();
        private bool showMore;
        private readonly FitScroll steps = new FitScroll();
        private bool wasInBattle;
        private float scale = 1f;

        public TurnPilot Pilot => pilot;

        private static PlannerMod.PlannerConfig Cfg => PlannerMod.config;

        private void OnApplicationQuit()
        {
            BrainService.Shutdown();
        }

        private static bool InBattle()
        {
            BattleManager bm = Singleton<BattleManager>.Instance();
            return bm != null && bm.inBattle;
        }

        private void Update()
        {
            L.Refresh();
            try
            {
                bool inBattle = InBattle();
                if (inBattle && !wasInBattle)
                {
                    pilot.Reset();
                    pilot.Mode = Cfg != null && Cfg.autoPilotOnBattleStart ? TurnPilot.RunMode.Auto : TurnPilot.RunMode.Manual;
                }
                if (!inBattle && wasInBattle) pilot.Reset();
                wasInBattle = inBattle;
                if (!inBattle) return;

                if (Input.GetKeyDown(KeyCode.F8)) Panel.Visible = !Panel.Visible;
                if (Input.GetKeyDown(KeyCode.F7)) pilot.ToggleAuto();
                if (Input.GetKeyDown(KeyCode.F5)) pilot.StepOnce();
                pilot.Tick(Time.realtimeSinceStartup);
            }
            catch (Exception ex)
            {
                pilot.Mode = TurnPilot.RunMode.Manual;
                pilot.Status = L.T("出错已停止：", "Stopped after an error: ") + ex.Message;
                Debug.LogWarning("[Planner] hud: " + ex);
            }
        }

        // ---------------------------------------------------------------- drawing

        private void OnGUI()
        {
            if (!InBattle()) return;
            Ui.Ensure();
            Font oldFont = GUI.skin.font;
            if (Ui.Font != null) GUI.skin.font = Ui.Font;
            try
            {
                scale = Ui.Scale;
                if (Panel.Visible && (Cfg == null || Cfg.boardMarks)) DrawBoard();
                BattleManager bm = Singleton<BattleManager>.Instance();
                Panel.Draw(L.T("战术求解器", "Tactics Solver"), L.T("第 " + bm.round + " 回合", "Round " + bm.round), DrawBody);
            }
            finally
            {
                GUI.skin.font = oldFont;
            }
        }

        private void DrawBody()
        {
            BrainPlan plan = pilot.Plan;

            if (BrainService.GameChanged && !pilot.PreferLocal)
            {
                GUILayout.Label(L.T("△ 游戏更新过，求解器是按旧版本做的，方案可能不准（等 mod 更新）",
                    "△ The game was updated after this solver was built; plans may be off until the mod is updated"), Ui.WarnText);
            }

            DrawHeadline(plan);
            DrawProgress();
            if (!string.IsNullOrEmpty(pilot.LastError)) GUILayout.Label("× " + pilot.LastError, Ui.ErrorText);

            GUILayout.Space(4);
            DrawButtons();

            if (!Panel.Collapsed)
            {
                DrawSteps(plan);
                DrawLines(plan);
                DrawMore(plan);
                Ui.Separator();
                float t = SolveBudget.Row(L.T("每回合求解", "Time per turn"), SolveBudget.BattleSteps, Cfg?.battleSeconds ?? 0f);
                if (Cfg != null && t != Cfg.battleSeconds)
                {
                    Cfg.battleSeconds = t;
                    PlannerMod.SaveConfig();
                }
                GUILayout.Label(L.T("F5 下一步 · F7 自动 · F8 隐藏 · 拖动标题栏移动", "F5 step · F7 auto · F8 hide · drag the title to move"), Ui.Faint);
            }
        }

        private void DrawHeadline(BrainPlan plan)
        {
            if (plan == null)
            {
                if (!pilot.Solving) GUILayout.Label(string.IsNullOrEmpty(pilot.Status) ? L.T("等待我方回合…", "Waiting for your turn…") : pilot.Status, Ui.Big);
                return;
            }
            string headline;
            Color c;
            if (plan.Local)
            {
                headline = L.T("本地估算（只看本回合）", "Local estimate (this turn only)");
                c = Ui.Gold;
            }
            else if (plan.Win)
            {
                // 燃料跨战斗保留：显示出来，玩家才看得懂"这回合为什么不动"
                string fuel = plan.PredictFuel >= 0 ? L.T(" · 燃料 " + plan.PredictFuel, ", " + plan.PredictFuel + " fuel") : "";
                headline = L.T("预计第 " + plan.PredictRound + " 回合获胜 · 剩 " + plan.PredictHp + " HP" + fuel,
                    "Win expected in round " + plan.PredictRound + " · " + plan.PredictHp + " HP" + fuel + " left");
                c = Ui.Good;
            }
            else
            {
                headline = L.T("预计会输：尽量多打伤害", "Likely loss: dealing as much damage as possible");
                c = Ui.Bad;
            }
            Ui.Colored(headline, Ui.Big, c);
            if (!plan.Local)
            {
                GUILayout.Label(L.T("本回合：造成 " + plan.TurnDealt + " 伤害 · 承受 " + plan.TurnTaken,
                                    "This turn: deal " + plan.TurnDealt + " · take " + plan.TurnTaken)
                                + (plan.Seconds > 0 ? L.T(" · 用时 ", " · ") + L.Clock(plan.Seconds) : ""), Ui.Small);
            }
            if (plan.Desync != null && plan.Desync.Count > 0)
            {
                GUILayout.Label(L.T("△ 模拟器和游戏有 " + plan.Desync.Count + " 处不一致，方案可能不准",
                    "△ " + plan.Desync.Count + " differences between the simulator and the game; the plan may be off"), Ui.WarnText);
            }
        }

        /// <summary>求解中：进度条 + 在做什么 + 用时，Brain 求解时可以"立即出结果"。</summary>
        private void DrawProgress()
        {
            if (!pilot.Solving) return;
            GUILayout.Space(2);
            BrainClient brain = pilot.Brain;
            if (brain.Busy)
            {
                float frac = brain.Fraction;
                float budget = Cfg?.battleSeconds ?? 0f;
                if (budget > 0 && frac >= 0) frac = Mathf.Max(frac, (float)(brain.Elapsed / budget));
                Ui.Progress(frac, Ui.Spinner + " " + brain.ProgressText + "   " + L.Clock(brain.Elapsed), Ui.Accent);
                if (brain.State == BrainClient.ClientState.Solving
                    && GUILayout.Button(L.T("立即出结果（用目前最好的方案）", "Answer now (best plan so far)"), Ui.Button))
                {
                    brain.Hurry();
                }
            }
            else
            {
                Ui.Progress(-1f, Ui.Spinner + " " + pilot.Status, Ui.Gold);
            }
        }

        private void DrawButtons()
        {
            GUILayout.BeginHorizontal();
            bool manual = pilot.Mode == TurnPilot.RunMode.Manual;
            GUI.enabled = pilot.HasPlan && manual;
            if (GUILayout.Button(L.T("下一步 F5", "Step F5"), Ui.Primary)) pilot.StepOnce();
            if (GUILayout.Button(L.T("执行本回合", "Play turn"), Ui.Button)) pilot.RunTurn();
            GUI.enabled = true;
            bool auto = pilot.Mode == TurnPilot.RunMode.Auto;
            if (GUILayout.Button(auto ? L.T("自动：开 F7", "Auto: on F7") : L.T("自动：关 F7", "Auto: off F7"), auto ? Ui.ButtonOn : Ui.Button)) pilot.ToggleAuto();
            if (GUILayout.Button(L.T("重算", "Re-solve"), Ui.Button)) pilot.Resolve();
            GUILayout.EndHorizontal();
        }

        private void DrawSteps(BrainPlan plan)
        {
            if (plan == null || plan.Plan.Count == 0) return;
            GUILayout.Label(L.T("操作顺序", "Steps"), Ui.Section);
            steps.Begin(250);
            for (int i = 0; i < plan.Plan.Count; i++)
            {
                GUIStyle s = i < pilot.Next ? Ui.StepDone : i == pilot.Next ? Ui.StepNow : Ui.Step;
                string mark = i < pilot.Next ? "√ " : i == pilot.Next ? "→ " : "   ";
                GUILayout.Label(mark + (i + 1) + ". " + Describe.Token(plan.Plan[i]), s);
            }
            steps.End();
        }

        private void DrawLines(BrainPlan plan)
        {
            if (plan?.Lines == null || plan.Lines.Count == 0) return;
            GUILayout.Label(L.T("执行后的对撞预测（我方 : 敌方）", "Clashes after these steps (you : enemy)"), Ui.Section);
            int k = 1;
            foreach (int[] l in plan.Lines)
            {
                DrawLine(k++, l);
            }
        }

        private void DrawMore(BrainPlan plan)
        {
            if (plan == null) return;
            showMore = Ui.Foldout(showMore, L.T("下回合预案 / 备选方案", "Next turn / alternatives"));
            if (!showMore) return;
            if (!string.IsNullOrEmpty(plan.NextTurn)) GUILayout.Label(L.T("下回合：", "Next turn: ") + Describe.Plan(plan.NextTurn), Ui.Step);
            foreach (string alt in plan.Alternatives.Take(3))
            {
                int sp = alt.IndexOf("  ", StringComparison.Ordinal);
                string value = sp > 0 ? alt.Substring(0, sp) : "";
                GUILayout.Label(L.T("备选", "Alt") + " [" + Value(value) + "] " + Describe.Plan(sp > 0 ? alt.Substring(sp + 2) : alt, 4), Ui.Small);
            }
            string solver = pilot.PreferLocal ? L.T("本地估算", "local estimate")
                : !pilot.Brain.Installed ? L.T("未找到求解器，使用本地估算", "solver not found, local estimate")
                : L.T("真实引擎模拟", "real-engine simulation") + (plan.Jobs > 0 ? L.T(" · 模拟 " + plan.Jobs + " 次", " · " + plan.Jobs + " simulations") : "");
            GUILayout.Label(L.T("求解：", "Solver: ") + solver, Ui.Faint);
        }

        /// <summary>"WIN hp=26 fuel=2 round=3" / "LOSS dmg=40" → 短句（旧服务没有 fuel=）。</summary>
        private static string Value(string v)
        {
            try
            {
                if (v.StartsWith("WIN"))
                {
                    string hp = Field(v, "hp="), round = Field(v, "round=");
                    bool hasFuel = v.Contains("fuel=");
                    string fuel = hasFuel ? Field(v, "fuel=") : "";
                    return L.T("第" + round + "回合胜 剩" + hp + (hasFuel ? " 燃料" + fuel : ""),
                        "win r" + round + " " + hp + "HP" + (hasFuel ? " " + fuel + "fuel" : ""));
                }
                if (v.StartsWith("LOSS")) return L.T("负 伤害" + Field(v, "dmg="), "loss " + Field(v, "dmg=") + " dmg");
            }
            catch
            {
                // 原样
            }
            return v;
        }

        private static string Field(string s, string key)
        {
            int i = s.IndexOf(key, StringComparison.Ordinal) + key.Length;
            int j = s.IndexOf(' ', i);
            return j < 0 ? s.Substring(i) : s.Substring(i, j - i);
        }

        private void DrawLine(int k, int[] l)
        {
            // y, player, enemy, result, both, playerLine, enemyLine
            bool both = l.Length > 4 && l[4] == 1;
            int result = l[3];
            Rect r = GUILayoutUtility.GetRect(370, 22);
            Color c = !both ? Ui.Dim : result > 0 ? Ui.Good : result < 0 ? Ui.Bad : Ui.Dim;
            string tag = !both ? L.T("空行", "empty") : result > 0 ? L.T("赢 +", "win +") + result : result < 0 ? L.T("输 ", "lose ") + result : L.T("平", "tie");
            float total = Mathf.Max(1, l[1] + l[2]);
            const float w = 160;
            var bar = new Rect(r.x + 64, r.y + 6, w, 10);
            Ui.Fill(bar, new Color(1, 1, 1, 0.08f));
            Ui.Fill(new Rect(bar.x, bar.y, w * l[1] / total, bar.height), new Color(Ui.Accent.r, Ui.Accent.g, Ui.Accent.b, 0.85f));
            Ui.Fill(new Rect(bar.xMax - w * l[2] / total, bar.y, w * l[2] / total, bar.height), new Color(Ui.Bad.r, Ui.Bad.g, Ui.Bad.b, 0.7f));
            GUI.Label(new Rect(r.x, r.y, 64, 22), L.T("第" + k + "行", "Row " + k), Ui.LineText);
            GUI.Label(new Rect(bar.xMax + 8, r.y, 70, 22), l[1] + " : " + l[2], Ui.LineText);
            Color old = GUI.contentColor;
            GUI.contentColor = c;
            GUI.Label(new Rect(bar.xMax + 76, r.y, 90, 22), tag, Ui.LineText);
            GUI.contentColor = old;
        }

        // ---------------------------------------------------------------- board annotations

        private void DrawBoard()
        {
            if (!pilot.HasPlan) return;
            float cell = CellPixels();
            int shown = 0;
            for (int i = pilot.Next; i < pilot.Plan.Plan.Count && shown < 3; i++, shown++)
            {
                DrawStepOnBoard(pilot.Plan.Plan[i], i + 1, shown == 0, cell);
            }
        }

        private void DrawStepOnBoard(string token, int number, bool current, float cell)
        {
            float alpha = current ? 1f : 0.45f;
            float thick = current ? 4f : 2f;
            if (token.StartsWith("loadp:") || token.StartsWith("load:"))
            {
                string[] parts = token.Substring(token.IndexOf(':') + 1).Split('@');
                ActionExecutor.ParseXY(parts[1], out int x, out int y);
                Rect? unit = UnitRect(x, y, cell);
                EnergyData card = null;
                if (token.StartsWith("loadp:") && ActionExecutor.ParseCard(parts[0], out int p, out int[] cs)) card = ActionExecutor.FindCard(p, cs);
                Rect? cardRect = card?.powerController != null ? UiRect(card.powerController.transform, 70 * scale) : null;
                Color c = new Color(Ui.Accent.r, Ui.Accent.g, Ui.Accent.b, alpha);
                Color gold = new Color(Ui.Gold.r, Ui.Gold.g, Ui.Gold.b, alpha);
                if (unit.HasValue) Ui.Outline(unit.Value, c, thick);
                if (cardRect.HasValue && current) Ui.Outline(cardRect.Value, gold, thick);
                if (unit.HasValue && cardRect.HasValue && current) Ui.Line(cardRect.Value.center, unit.Value.center, new Color(Ui.Gold.r, Ui.Gold.g, Ui.Gold.b, 0.75f), 3f);
                if (unit.HasValue) Badge(unit.Value, number, c);
                return;
            }
            if (token.StartsWith("paint:") || token.StartsWith("paintpool:"))
            {
                // 多彩号：手牌 → 涂料格，用涂料的颜色
                int rare;
                EnergyData card = null;
                if (token.StartsWith("paint:"))
                {
                    string[] parts = token.Substring(6).Split('@');
                    int.TryParse(parts[1], out rare);
                    if (ActionExecutor.ParseCard(parts[0], out int p, out int[] cs)) card = ActionExecutor.FindCard(p, cs);
                }
                else
                {
                    int.TryParse(token.Substring(10), out rare);
                }
                ColorSlotCell pot = SolverLogger.Paint.Pot(rare);
                Rect? potRect = pot != null ? UiRect(pot.transform, 46 * scale) : null;
                Rect? cardRect = card?.powerController != null ? UiRect(card.powerController.transform, 70 * scale) : null;
                Color pc = PaintColor(rare);
                Color c = new Color(pc.r, pc.g, pc.b, alpha);
                if (potRect.HasValue) Ui.Outline(potRect.Value, c, thick);
                if (cardRect.HasValue && current) Ui.Outline(cardRect.Value, c, thick);
                if (potRect.HasValue && cardRect.HasValue && current) Ui.Line(cardRect.Value.center, potRect.Value.center, new Color(pc.r, pc.g, pc.b, 0.75f), 3f);
                if (cardRect.HasValue) Badge(cardRect.Value, number, c, L.T("涂", "P"));
                else if (potRect.HasValue) Badge(potRect.Value, number, c, L.T("涂", "P"));
                return;
            }
            if (token.StartsWith("swap:"))
            {
                string[] ab = token.Substring(5).Split('>');
                ActionExecutor.ParseXY(ab[0], out int ax, out int ay);
                ActionExecutor.ParseXY(ab[1], out int bx, out int by);
                Color c = new Color(Ui.Purple.r, Ui.Purple.g, Ui.Purple.b, alpha);
                Rect? a = CellRect(ax, ay, cell), b = CellRect(bx, by, cell);
                if (a.HasValue) Ui.Outline(a.Value, c, thick);
                if (b.HasValue) Ui.Outline(b.Value, c, thick);
                if (a.HasValue && b.HasValue) Ui.Line(a.Value.center, b.Value.center, c, 3f);
                if (a.HasValue) Badge(a.Value, number, c, L.T("换", "S"));
                return;
            }
            if (token.StartsWith("skill:"))
            {
                string body = token.Substring(6);
                int gt = body.IndexOf('>');
                string target = gt >= 0 ? body.Substring(gt + 1) : null;
                if (gt >= 0) body = body.Substring(0, gt);
                ActionExecutor.ParseXY(body, out int x, out int y);
                Color c = new Color(Ui.Orange.r, Ui.Orange.g, Ui.Orange.b, alpha);
                Rect? u = UnitRect(x, y, cell);
                if (u.HasValue)
                {
                    Ui.Outline(u.Value, c, thick);
                    Badge(u.Value, number, c, "★");
                }
                if (target != null && ActionExecutor.ParseXY(target.Substring(2), out int tx, out int ty))
                {
                    Rect? t = target.StartsWith("c:") ? CellRect(tx, ty, cell) : UnitRect(tx, ty, cell);
                    if (t.HasValue)
                    {
                        Ui.Outline(t.Value, c, thick);
                        if (u.HasValue) Ui.Line(u.Value.center, t.Value.center, c, 2f);
                    }
                }
                return;
            }
            if (token.StartsWith("move:") && current)
            {
                BattleManager bm = Singleton<BattleManager>.Instance();
                Vector2? ship = WorldToGui(bm.playerShip.transform.position);
                if (ship.HasValue)
                {
                    bool up = token.EndsWith(":1");
                    var r = new Rect(ship.Value.x - 80 * scale, ship.Value.y - (up ? 2.2f : -1.4f) * cell, 160 * scale, 40 * scale);
                    Ui.Fill(r, new Color(0.08f, 0.09f, 0.12f, 0.9f));
                    Ui.Outline(r, Ui.Good, 2);
                    GUI.Label(r, (up ? "▲ " : "▼ ") + number + ". " + (up ? L.T("上移一行", "Move up") : L.T("下移一行", "Move down")), BadgeStyle());
                }
            }
        }

        /// <summary>游戏里三种能量颜色（白 / 蓝 / 橙）。</summary>
        private static Color PaintColor(int rare)
        {
            switch (rare)
            {
                case 3: return new Color(1f, 0.38f, 0f);
                case 2: return new Color(0.15f, 0.57f, 1f);
                default: return new Color(0.85f, 0.85f, 0.85f);
            }
        }

        private float CellPixels()
        {
            try
            {
                BattleManager bm = Singleton<BattleManager>.Instance();
                var grid = bm.playerShip.shipData.grid;
                ShipCell a = grid.GetContent(0, 0), b = grid.GetContent(Math.Min(1, grid.width - 1), 0);
                if (a?.obj != null && b?.obj != null && a != b)
                {
                    Vector2? pa = WorldToGui(a.obj.transform.position), pb = WorldToGui(b.obj.transform.position);
                    if (pa.HasValue && pb.HasValue)
                    {
                        float d = Vector2.Distance(pa.Value, pb.Value);
                        if (d > 10) return d;
                    }
                }
            }
            catch
            {
                // fall back
            }
            return 90 * scale;
        }

        private Rect? UnitRect(int x, int y, float cell)
        {
            ShipUnitController u = ActionExecutor.PlayerUnit(x, y);
            if (u == null) return CellRect(x, y, cell);
            Vector2? p = WorldToGui(u.transform.position);
            return p.HasValue ? new Rect(p.Value.x - cell * 0.46f, p.Value.y - cell * 0.46f, cell * 0.92f, cell * 0.92f) : (Rect?)null;
        }

        private Rect? CellRect(int x, int y, float cell)
        {
            ShipCell c = ActionExecutor.PlayerCell(x, y);
            if (c?.obj == null) return null;
            Vector2? p = WorldToGui(c.obj.transform.position);
            return p.HasValue ? new Rect(p.Value.x - cell * 0.46f, p.Value.y - cell * 0.46f, cell * 0.92f, cell * 0.92f) : (Rect?)null;
        }

        private static Camera WorldCamera()
        {
            try
            {
                Camera cam = Singleton<UIManager>.Instance().mainCamera;
                if (cam != null) return cam;
            }
            catch
            {
                // ignore
            }
            return Camera.main;
        }

        private static Vector2? WorldToGui(Vector3 world)
        {
            Camera cam = WorldCamera();
            if (cam == null) return null;
            Vector3 s = cam.WorldToScreenPoint(world);
            if (s.z < 0) return null;
            return new Vector2(s.x, Screen.height - s.y);
        }

        private static Rect? UiRect(Transform t, float size)
        {
            Canvas canvas = t.GetComponentInParent<Canvas>();
            Vector2 s;
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                s = RectTransformUtility.WorldToScreenPoint(null, t.position);
            }
            else
            {
                Camera cam = canvas.worldCamera != null ? canvas.worldCamera : WorldCamera();
                s = RectTransformUtility.WorldToScreenPoint(cam, t.position);
            }
            return new Rect(s.x - size / 2, Screen.height - s.y - size / 2, size, size);
        }

        /// <summary>棋盘标注不受面板缩放矩阵影响，字号按缩放单独算。</summary>
        private GUIStyle BadgeStyle()
        {
            Ui.Badge.fontSize = Mathf.RoundToInt(15 * scale);
            return Ui.Badge;
        }

        private void Badge(Rect target, int number, Color c, string prefix = null)
        {
            float s = 26 * scale;
            var r = new Rect(target.x - s * 0.35f, target.y - s * 0.35f, s * (prefix == null ? 1f : 1.6f), s);
            Ui.Fill(r, new Color(0.08f, 0.09f, 0.12f, 0.92f));
            Ui.Outline(r, c, 2);
            GUI.Label(r, (prefix ?? "") + number, BadgeStyle());
        }
    }
}
