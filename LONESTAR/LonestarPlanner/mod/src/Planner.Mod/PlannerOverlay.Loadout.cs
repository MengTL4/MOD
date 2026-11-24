using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using LitJson;
using LonestarPlanner.Core;
using UnityEngine;

namespace LonestarPlanner.Mod
{
    /// <summary>
    /// 船坞："真实模拟摆位"。针对下一场悬赏的敌人，把当前存档交给 Brain：
    /// 它在真实引擎里试遍各种摆法（谁上船、放哪格），每种都实际打一遍，
    /// 最后几种用完整求解器逐回合打完，选出胜率/剩余血量/回合数最好的。
    /// </summary>
    public partial class PlannerOverlay
    {
        private readonly BrainClient _loadoutBrain = new BrainClient();
        private LayoutPlan _brainPlan;
        private string _brainStatus = "";
        private bool _brainStatusBad;
        private JsonData _brainOutcome, _brainCurrentOutcome;
        private bool _brainIsCurrent;
        private string _brainEnemyName;
        private bool _brainPending;

        // 下一场对手（每秒刷新一次，没打开悬赏面板时也能从当前悬赏流程里找到）
        private string _nextEnemyName;
        private int _nextEnemyId;
        /// <summary>悬赏附带的战斗事件（没有为 null）。</summary>
        private string _nextEventName;
        private int _nextEventId;

        private static readonly AccessTools.FieldRef<WantedPanel, Wanted> WantedOfPanel =
            AccessTools.FieldRefAccess<WantedPanel, Wanted>("wanted");

        private static bool TryFindWanted(out Wanted wanted)
        {
            wanted = null;
            try
            {
                WantedPanel panel = Singleton<UIManager>.Instance().GetPanel<WantedPanel>(FilePath.wantedPanel);
                if (panel != null) wanted = WantedOfPanel(panel);
            }
            catch
            {
                // 面板没开
            }
            if (wanted?.enemyData == null)
            {
                wanted = Singleton<WantedManager>.Instance()?.wantedProcess?.currentWanted;
            }
            return wanted?.enemyData != null;
        }

        private void RefreshWanted()
        {
            if (!TryFindWanted(out Wanted wanted))
            {
                _nextEnemyName = null;
                _nextEnemyId = 0;
                _nextEventName = null;
                _nextEventId = 0;
                return;
            }
            if (wanted.enemyData.ID == _nextEnemyId && wanted.battleEventID == _nextEventId && _nextEnemyName != null) return;
            _nextEnemyId = wanted.enemyData.ID;
            _nextEnemyName = EnemyName(wanted.enemyData);
            _nextEventId = wanted.battleEventID;
            _nextEventName = EventName(wanted.battleEventID);
        }

        private static string EventName(int id)
        {
            if (id <= 0) return null;
            try
            {
                var data = Tool.Database.DataBattleEventManager.Instance().GetDataList().FirstOrDefault(e => e.ID == id);
                if (data == null) return "#" + id;
                string n = Lean.Localization.LeanLocalization.GetTranslationText(data.Name, data.Name, true);
                return string.IsNullOrEmpty(n) ? "#" + id : n;
            }
            catch
            {
                return "#" + id;
            }
        }

        private static string EnemyName(Tool.Database.DataEnemyShip enemy)
        {
            try
            {
                string n = Lean.Localization.LeanLocalization.GetTranslationText(enemy.Name, enemy.Name, true);
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch
            {
                // 名字拿不到就用 id
            }
            return "#" + enemy.ID;
        }

        private void SetBrainStatus(string text, bool bad = false)
        {
            _brainStatus = text;
            _brainStatusBad = bad;
        }

        private void StartBrainLoadout()
        {
            _brainPlan = null;
            _brainOutcome = _brainCurrentOutcome = null;
            if (!TryFindWanted(out Wanted wanted))
            {
                SetBrainStatus(L.T("先打开悬赏面板，选好下一场要打的敌人", "Open the bounty board and pick the next enemy first"), true);
                return;
            }
            if (!_loadoutBrain.Installed)
            {
                SetBrainStatus(L.T("没找到求解器：", "Solver not found: ") + BrainService.ExpectedPath, true);
                return;
            }
            try
            {
                // 和进战斗前游戏自己做的一样：先存档，求解器从这份存档开局
                Singleton<SaveManager>.Instance().Save();
                string logs = SolverLogger.Log.FilePath == null
                    ? Path.Combine(PlannerMod.DataRoot ?? Application.persistentDataPath, "logs")
                    : Path.GetDirectoryName(SolverLogger.Log.FilePath);
                string dir = Path.Combine(logs, "loadout");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_e" + wanted.enemyData.ID + ".jsonl");
                string begin = SolverLogger.Log.ToJson(new List<object> { "n", 0, "t", "battle_begin", "save_files", SolverLogger.Recorder.SaveFiles() });
                string init = SolverLogger.Log.ToJson(new List<object> { "n", 1, "t", "battle_init", "enemy", wanted.enemyData.ID, "battle_event", wanted.battleEventID });
                File.WriteAllText(file, begin + "\n" + init + "\n");
                _brainEnemyName = EnemyName(wanted.enemyData);
                _loadoutBrain.Loadout(file, 0, Cfg?.loadoutSeconds ?? 600f);
                _brainPending = true;
                SetBrainStatus("");
            }
            catch (Exception ex)
            {
                SetBrainStatus(L.T("准备失败：", "Could not start: ") + ex.Message, true);
                Debug.LogWarning("[Planner] loadout request failed: " + ex);
            }
        }

        private void PollBrainLoadout()
        {
            if (!_brainPending) return;
            switch (_loadoutBrain.State)
            {
                case BrainClient.ClientState.Ready:
                    _brainPending = false;
                    OnBrainLoadout(_loadoutBrain.Reply);
                    break;
                case BrainClient.ClientState.Failed:
                    _brainPending = false;
                    SetBrainStatus(_loadoutBrain.ErrorText, true);
                    break;
                case BrainClient.ClientState.Idle:
                    _brainPending = false;
                    break;
            }
        }

        private void OnBrainLoadout(JsonData reply)
        {
            _brainOutcome = BrainPlan.Has(reply, "Outcome") ? reply["Outcome"] : null;
            _brainCurrentOutcome = BrainPlan.Has(reply, "CurrentOutcome") ? reply["CurrentOutcome"] : null;
            _brainIsCurrent = BrainPlan.Bool(reply, "IsCurrent");
            if (!GameStateReader.TryReadDock(out DockSnapshot dock, out string error))
            {
                SetBrainStatus(L.T("读取船坞失败：", "Cannot read the dock: ") + error, true);
                return;
            }
            _brainPlan = BuildPlan(dock, reply, out error);
            int tried = BrainPlan.Int(reply, "Evaluated");
            string stats = L.T("（试了 " + tried + " 种摆法，用时 ", " (" + tried + " layouts tried in ") + L.Clock(BrainPlan.Dbl(reply, "Seconds")) + L.T("）", ")");
            if (_brainPlan == null)
            {
                SetBrainStatus(L.T("结果和当前船坞对不上（", "The result does not match the dock any more (") + error + L.T("），请重新求解", "), please solve again"), true);
            }
            else if (_brainIsCurrent || _brainPlan.Moves.Count == 0)
            {
                SetBrainStatus(L.T("当前摆位已经是最好的", "Your current layout is already the best") + stats);
            }
            else
            {
                SetBrainStatus(L.T("找到更好的摆位", "Found a better layout") + stats);
            }
            Panel.Visible = true; // 算了好几分钟的结果，主动给玩家看
        }

        /// <summary>Brain 的目标布局（按来源格/背包序号指认部件）→ 船坞操作步骤。</summary>
        private static LayoutPlan BuildPlan(DockSnapshot dock, JsonData reply, out string error)
        {
            error = null;
            if (!BrainPlan.Has(reply, "Entries") || !reply["Entries"].IsArray)
            {
                error = L.T("没有布局", "no layout");
                return null;
            }
            var grid = dock.ShipData.grid;
            var bag = Singleton<ItemManager>.Instance().itemUnits;
            var target = new List<PlacementSlot>();
            foreach (JsonData e in reply["Entries"])
            {
                int x = (int)e[0], y = (int)e[1], id = (int)e[3], lv = (int)e[4], sx = (int)e[5], sy = (int)e[6], b = (int)e[7];
                UnitData unit = b >= 0
                    ? (b < bag.Count ? bag[b]?.unitData : null)
                    : grid.GetContent(sx, sy)?.unitData;
                if (unit == null || unit.id != id || unit.lv != lv)
                {
                    error = L.T("部件 #" + id + " 不在原来的位置", "unit #" + id + " has moved");
                    return null;
                }
                int card = dock.EquippedByCardId.Where(kv => kv.Value == unit).Select(kv => kv.Key)
                    .Concat(dock.ItemByCardId.Where(kv => kv.Value?.unitData == unit).Select(kv => kv.Key))
                    .DefaultIfEmpty(-1).First();
                if (card < 0)
                {
                    error = L.T("找不到部件 #", "unit not found: #") + id;
                    return null;
                }
                target.Add(new PlacementSlot(card, x, y));
            }
            var plan = new LayoutPlan { Layout = target };
            plan.Moves = LayoutSequencer.Build(dock.State, target);
            var placed = new HashSet<int>(target.Select(t => t.UnitId));
            plan.BenchedUnitIds = dock.State.Units.Where(u => !placed.Contains(u.Id)).Select(u => u.Id).ToList();
            return plan;
        }

        private void ApplyBrainLoadout()
        {
            try
            {
                if (!GameStateReader.TryReadDock(out DockSnapshot snapshot, out string error))
                {
                    SetBrainStatus(L.T("摆放失败：", "Could not apply: ") + error, true);
                    return;
                }
                LayoutPlan plan = BuildPlan(snapshot, _loadoutBrain.Reply, out error);
                if (plan == null)
                {
                    SetBrainStatus(L.T("船坞已经变了（", "The dock has changed (") + error + L.T("），请重新求解", "), please solve again"), true);
                    return;
                }
                string backup = WriteDockBackup(snapshot);
                bool ok = DockExecutor.Apply(snapshot, plan, out string report);
                SetBrainStatus((ok ? L.T("已按推荐摆好：", "Applied: ") : L.T("摆放失败：", "Could not apply: ")) + report + "  " + VerifyLayout(snapshot, plan)
                               + (string.IsNullOrEmpty(backup) ? "" : L.T("（原布局已备份）", " (old layout backed up)")), !ok);
                _brainPlan = null;
                _lastRefreshAt = -999f;
            }
            catch (Exception ex)
            {
                SetBrainStatus(L.T("摆放出错：", "Error while applying: ") + ex.Message, true);
                Debug.LogWarning("[Planner] apply brain loadout failed: " + ex);
            }
        }

        private void DrawBrainLoadout()
        {
            GUILayout.Label(L.T("真实模拟摆位", "Real-simulation loadout"), Ui.Section);
            if (!Panel.Collapsed)
            {
                GUILayout.Label(L.T("针对下一场悬赏，在真实游戏引擎里把各种摆法实际打一遍，选出打得最好的。",
                    "Plays the next bounty battle in the real game engine with many layouts and picks the one that does best."), Ui.Small);
            }

            // 对手
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("下一场：", "Next battle: "), Ui.Small, GUILayout.ExpandWidth(false));
            if (_nextEnemyName != null) GUILayout.Label(_nextEnemyName + (_nextEventName != null ? L.T(" · 事件：", " · event: ") + _nextEventName : ""), Ui.Body);
            else GUILayout.Label(L.T("未选（打开悬赏面板选一个敌人）", "none (pick an enemy on the bounty board)"), Ui.WarnText);
            GUILayout.EndHorizontal();

            // 按钮
            GUILayout.BeginHorizontal();
            GUI.enabled = !_brainPending && _nextEnemyName != null;
            if (GUILayout.Button(_brainPending ? L.T("求解中…", "Solving…") : _brainOutcome != null ? L.T("重新求解", "Solve again") : L.T("求最佳摆位", "Find the best layout"), Ui.Primary))
            {
                StartBrainLoadout();
            }
            GUI.enabled = _brainPlan != null && _brainPlan.Moves.Count > 0 && !_brainPending;
            if (ConfirmButton("brain", L.T("按推荐摆放", "Apply"), Ui.Button)) ApplyBrainLoadout();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // 进度
            if (_brainPending)
            {
                float cap = Cfg?.loadoutSeconds ?? 600f;
                float frac = _loadoutBrain.Fraction;
                Ui.Progress(frac, Ui.Spinner + " " + _loadoutBrain.ProgressText + "   " + L.Clock(_loadoutBrain.Elapsed)
                                  + L.T(" / 上限 ", " / cap ") + L.Duration(cap), Ui.Accent);
                if (_loadoutBrain.State == BrainClient.ClientState.Solving
                    && GUILayout.Button(L.T("立即出结果（用目前最好的摆法）", "Answer now (best layout so far)"), Ui.Button))
                {
                    _loadoutBrain.Hurry();
                }
            }

            if (!string.IsNullOrEmpty(_brainStatus)) GUILayout.Label(_brainStatus, _brainStatusBad ? Ui.ErrorText : Ui.Body);

            // 结果：推荐 vs 当前
            if (_brainOutcome != null && !_brainPending)
            {
                if (_brainEnemyName != null && _brainEnemyName != _nextEnemyName)
                {
                    GUILayout.Label(L.T("（这是对 " + _brainEnemyName + " 的结果）", "(this result is for " + _brainEnemyName + ")"), Ui.WarnText);
                }
                OutcomeRow(_brainIsCurrent ? L.T("当前摆位", "Current") : L.T("推荐摆位", "Recommended"), _brainOutcome);
                if (!_brainIsCurrent && _brainCurrentOutcome != null) OutcomeRow(L.T("当前摆位", "Current"), _brainCurrentOutcome);
            }
            if (_brainPlan != null && _brainPlan.Moves.Count > 0 && !Panel.Collapsed)
            {
                GUILayout.Label(L.T("要做的调整（也可以在船坞里自己拖）", "Changes to make (you can also drag them yourself)"), Ui.Section);
                DrawMoves(_brainPlan.Moves, 12);
            }

            if (!Panel.Collapsed)
            {
                float t = SolveBudget.Row(L.T("求解时间上限", "Time cap"), SolveBudget.LoadoutSteps, Cfg?.loadoutSeconds ?? 600f);
                if (Cfg != null && t != Cfg.loadoutSeconds)
                {
                    Cfg.loadoutSeconds = t;
                    PlannerMod.SaveConfig();
                }
                GUILayout.Label(L.T("找不到更好的摆法会提前停；复杂的首领可能要几分钟。", "Stops early once it stops finding better layouts; complex bosses can take a few minutes."), Ui.Faint);
            }
        }

        private static void OutcomeRow(string label, JsonData outcome)
        {
            string text = Describe.Outcome(outcome, out Color c);
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, Ui.Small, GUILayout.Width(80));
            Ui.Colored(text, Ui.Body, c);
            GUILayout.EndHorizontal();
        }
    }
}
