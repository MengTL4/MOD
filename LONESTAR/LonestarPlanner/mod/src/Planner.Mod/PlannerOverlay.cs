using System;
using System.IO;
using System.Text;
using LonestarPlanner.Core;
using UnityEngine;

namespace LonestarPlanner.Mod
{
    /// <summary>
    /// 船坞面板（战斗内由 <see cref="PlannerHud"/> 负责）：
    ///   · 真实模拟摆位：针对下一场悬赏，用 Brain 在真实引擎里试遍摆法（见 PlannerOverlay.Loadout.cs）；
    ///   · 摆放：走游戏自己的装配链路，执行前备份、逐步预检、失败回滚、完成后核对。
    ///     会真的改动舰船，所以要点两次确认（F6 同理）。
    /// （1.0 的"快速估算"——近似模型、不看敌人——在 1.1 去掉了：真实模拟摆位全面取代了它。）
    /// </summary>
    public partial class PlannerOverlay : MonoBehaviour
    {
        /// <summary>每秒读一次船坞和下一场悬赏。</summary>
        private const float RefreshInterval = 1f;

        private readonly FitScroll _scroll = new FitScroll();

        private DockSnapshot _dock;
        private bool _wasInBattle;

        private string _status = "";
        private float _lastRefreshAt = -999f;

        // 摆放要点两次：第一次只是"准备"，几秒内再点一次才执行
        private string _confirming;
        private float _confirmUntil;

        private static PlannerMod.PlannerConfig Cfg => PlannerMod.config;

        private static string BackupDir => Path.Combine(PlannerMod.DataRoot ?? Application.persistentDataPath, "backups");

        private void Update()
        {
            L.Refresh();
            try
            {
                var battle = Singleton<BattleManager>.Instance();
                _wasInBattle = battle != null && battle.inBattle;
                if (_wasInBattle) return;

                PollBrainLoadout();
                if (Input.GetKeyDown(KeyCode.F8) && _dock != null) Panel.Visible = !Panel.Visible;
                if (Input.GetKeyDown(KeyCode.F6) && _dock != null) ApplyPrimary();
                if (Cfg != null && Cfg.enableDump && Input.GetKeyDown(KeyCode.F9)) DumpSnapshot();

                if (Time.realtimeSinceStartup - _lastRefreshAt < RefreshInterval) return;
                _lastRefreshAt = Time.realtimeSinceStartup;
                RefreshWanted();
                _dock = GameStateReader.TryReadDock(out DockSnapshot dock, out _) ? dock : null;
            }
            catch (Exception ex)
            {
                _status = L.T("更新失败：", "Update failed: ") + ex.Message;
            }
        }

        // ---------------------------------------------------------------- drawing

        private void OnGUI()
        {
            if (_wasInBattle || _dock == null) return;
            Ui.Ensure();
            Font oldFont = GUI.skin.font;
            try
            {
                if (Ui.Font != null) GUI.skin.font = Ui.Font;
                Panel.Draw(L.T("战术求解器", "Tactics Solver"), L.T("船坞", "Dock"), DrawBody);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Planner] OnGUI error: " + ex);
            }
            finally
            {
                GUI.skin.font = oldFont;
            }
        }

        private void DrawBody()
        {
            if (BrainService.GameChanged)
            {
                GUILayout.Label(L.T("△ 游戏更新过，求解器是按旧版本做的，结果可能不准（等 mod 更新）",
                    "△ The game was updated after this solver was built; results may be off until the mod is updated"), Ui.WarnText);
            }

            // 标题栏和底部提示约 90 高
            _scroll.Begin(Mathf.Min(Screen.height / Ui.Scale * 0.75f, Panel.Room - 90f));
            DrawBrainLoadout();
            if (!Panel.Collapsed && !string.IsNullOrEmpty(_status)) GUILayout.Label(_status, Ui.Small);
            _scroll.End();
            if (!Panel.Collapsed)
            {
                GUILayout.Label(L.T("F6 摆放推荐（按两次）· F8 隐藏 · 拖动标题栏移动", "F6 applies the recommendation (press twice) · F8 hide · drag the title to move"), Ui.Faint);
            }
        }

        private static void DrawMoves(System.Collections.Generic.List<LayoutMove> moves, int limit)
        {
            int n = Math.Min(moves.Count, limit);
            for (int i = 0; i < n; i++)
            {
                GUILayout.Label((i + 1) + ". " + Describe.Move(moves[i]), Ui.Step);
            }
            if (moves.Count > n) GUILayout.Label(L.T("… 还有 " + (moves.Count - n) + " 步", "… " + (moves.Count - n) + " more"), Ui.Small);
        }

        /// <summary>要点两次的按钮：第一次变成"确认？"，4 秒内再点才返回 true。</summary>
        private bool ConfirmButton(string key, string label, GUIStyle style)
        {
            bool armed = _confirming == key && Time.realtimeSinceStartup < _confirmUntil;
            if (!GUILayout.Button(armed ? L.T("确认改动舰船？再点一次", "Change the ship? Click again") : label, armed ? Ui.ButtonOn : style)) return false;
            return Confirm(key);
        }

        private bool Confirm(string key)
        {
            if (_confirming == key && Time.realtimeSinceStartup < _confirmUntil)
            {
                _confirming = null;
                return true;
            }
            _confirming = key;
            _confirmUntil = Time.realtimeSinceStartup + 4f;
            return false;
        }

        /// <summary>F6：按推荐摆放（要按两次）。</summary>
        private void ApplyPrimary()
        {
            if (_brainPlan == null || _brainPlan.Moves.Count == 0)
            {
                _brainStatus = L.T("还没有可以摆放的推荐：先点\"求最佳摆位\"", "Nothing to apply yet: click \"Find the best layout\" first");
                return;
            }
            if (Confirm("brain")) ApplyBrainLoadout();
            else _brainStatus = L.T("再按一次 F6 确认按推荐摆放", "Press F6 again to apply the recommendation");
        }

        /// <summary>执行前把当前布局写到备份目录，出问题可以照着手动摆回来。</summary>
        private string WriteDockBackup(DockSnapshot snapshot)
        {
            try
            {
                Directory.CreateDirectory(BackupDir);
                var sb = new StringBuilder();
                sb.AppendLine("loadLimit=" + snapshot.State.LoadLimit);
                foreach (var slot in snapshot.State.CurrentLayout)
                {
                    string name = snapshot.EquippedByCardId.TryGetValue(slot.UnitId, out UnitData unit) && unit != null
                        ? Describe.UnitName(unit)
                        : "#" + slot.UnitId;
                    sb.AppendLine(name + "  @(" + slot.X + "," + slot.Y + ")  cost=" + LoadOf(unit));
                }
                foreach (var card in snapshot.State.Units)
                {
                    if (!snapshot.ItemByCardId.ContainsKey(card.Id)) continue;
                    sb.AppendLine("[storage] " + card.Name + "  cost=" + card.LoadCost);
                }
                string path = Path.Combine(BackupDir, "dock-before-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(path, sb.ToString());
                Debug.Log("[Planner] dock backup -> " + path);
                return path;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Planner] dock backup failed: " + ex.Message);
                return null;
            }
        }

        private static int LoadOf(UnitData unit)
        {
            try { return unit == null ? 0 : unit.GetEquiptLimit(); }
            catch { return 0; }
        }

        /// <summary>执行后再读一次船坞，核对目标布局是否真的到位。</summary>
        private static string VerifyLayout(DockSnapshot before, LayoutPlan plan)
        {
            try
            {
                if (!GameStateReader.TryReadDock(out DockSnapshot after, out string error)) return L.T("（未核对：", "(not verified: ") + error + L.T("）", ")");
                int matched = 0;
                foreach (var slot in plan.Layout)
                {
                    if (before.EquippedByCardId.TryGetValue(slot.UnitId, out UnitData expected) && expected != null)
                    {
                        if (Matches(after, expected, slot.X, slot.Y)) matched++;
                        continue;
                    }
                    if (before.ItemByCardId.TryGetValue(slot.UnitId, out ItemUnit item) && item?.unitData != null)
                    {
                        if (Matches(after, item.unitData, slot.X, slot.Y)) matched++;
                    }
                }
                return matched == plan.Layout.Count
                    ? L.T("核对通过 ", "verified ") + matched + "/" + plan.Layout.Count
                    : L.T("△ 只有 " + matched + "/" + plan.Layout.Count + " 个部件到位", "△ only " + matched + "/" + plan.Layout.Count + " units in place");
            }
            catch (Exception ex)
            {
                return L.T("（核对出错：", "(verify error: ") + ex.Message + L.T("）", ")");
            }
        }

        private static bool Matches(DockSnapshot snapshot, UnitData expected, int x, int y)
        {
            try
            {
                var cell = snapshot.ShipData.grid.GetContent(x, y);
                if (cell == null || cell.unitData == null) return false;
                return cell.unitData.id == expected.id && cell.unitData.lv == expected.lv;
            }
            catch
            {
                return false;
            }
        }

        private void DumpSnapshot()
        {
            try
            {
                if (!GameStateReader.TryReadDock(out DockSnapshot dock, out string error))
                {
                    _status = L.T("导出失败：", "Dump failed: ") + error;
                    return;
                }
                Directory.CreateDirectory(BackupDir);
                var st = dock.State;
                var sb = new StringBuilder();
                sb.AppendLine("loadLimit=" + st.LoadLimit);
                sb.AppendLine("units=" + st.Units.Count + ", placed=" + st.CurrentLayout.Count);
                sb.AppendLine("tokenPool=" + string.Join(", ", st.TokenPool.ConvertAll(t => t.Power.ToString() + t.Color.ShortName()).ToArray()));
                string path = Path.Combine(BackupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-dock.txt");
                File.WriteAllText(path, sb.ToString());
                _status = L.T("已导出：", "Dumped: ") + path;
            }
            catch (Exception ex)
            {
                _status = L.T("导出出错：", "Dump error: ") + ex.Message;
            }
        }
    }
}
