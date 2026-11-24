using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace LonestarPlanner.Mod
{
    /// <summary>
    /// 每个玩家回合的"方案 + 执行"状态机，HUD 和自动接管共用：
    ///
    ///   回合开始 / 局面被手动改了 → 向 Brain 求解（连不上就用本地估算）
    ///   方案就绪 → 等玩家按"下一步 / 执行本回合"，或者自动接管时自己一步步执行
    ///   某一步执行失败 → 当前局面重新求解（每回合最多 2 次），还不行就停下来交给玩家
    ///
    /// 局面签名只看会影响决策的东西（手牌、部件槽、偏移、资源、血量），鼠标悬停之类不算改动。
    /// </summary>
    public sealed class TurnPilot
    {
        public enum RunMode { Manual, Turn, Auto }

        public readonly BrainClient Brain = new BrainClient();
        public readonly LocalPlanner Local = new LocalPlanner();

        public BrainPlan Plan;
        public int Next;
        public RunMode Mode = RunMode.Manual;
        public string Status = "";
        public string LastError;
        public readonly List<string> Done = new List<string>();

        public bool AutoSolve = true;
        // 都现读设置：面板上改了，下一次求解/下一步就生效
        public bool PreferLocal => PlannerMod.config?.preferLocal ?? false;
        private static float SolveSeconds => PlannerMod.config?.battleSeconds ?? 0f;
        private static float ActionInterval => Mathf.Max(0.15f, PlannerMod.config?.autoActionInterval ?? 0.45f);
        /// <summary>本地估算只是后备：最多 6 秒。</summary>
        private const float LocalSeconds = 6f;
        private const int LocalMaxSwaps = 3;

        private int planRound = -1;
        private int endedRound = -1;
        private string warmedBattle;
        private string solveSig;
        private string expectedSig;
        private float nextActionAt;
        private float turnSeenAt = -1f;
        private int turnSeenRound = -1;
        private int resolvesThisTurn;
        private bool singleStep;
        private bool solving;

        public bool Solving => solving;

        public bool HasPlan => Plan != null && Plan.Plan.Count > 0;

        public string NextToken => HasPlan && Next < Plan.Plan.Count ? Plan.Plan[Next] : null;

        public IEnumerable<string> Remaining => HasPlan ? Plan.Plan.Skip(Next) : Enumerable.Empty<string>();

        public void Reset()
        {
            Brain.Clear();
            Local.Cancel();
            Plan = null;
            Next = 0;
            planRound = -1;
            endedRound = -1;
            expectedSig = null;
            solving = false;
            singleStep = false;
            resolvesThisTurn = 0;
            Done.Clear();
            if (Mode == RunMode.Turn) Mode = RunMode.Manual;
        }

        /// <summary>立刻按当前局面重新求解。</summary>
        public void Resolve()
        {
            BattleManager bm = Singleton<BattleManager>.Instance();
            if (bm == null || !bm.inBattle || bm.turnStatus != TurnStatus.PlayerTurn) return;
            Plan = null;
            Next = 0;
            Done.Clear();
            LastError = null;
            planRound = bm.round;
            expectedSig = Signature();
            solveSig = expectedSig;
            solving = true;
            string battleFile = SolverLogger.Log.BattleFilePath;
            if (!PreferLocal && battleFile != null && SolverLogger.Recorder.Active && Brain.Installed)
            {
                Brain.Solve(battleFile, SolveSeconds);
                Status = L.T("求解中…", "Solving…");
            }
            else
            {
                Local.Start(LocalSeconds, LocalMaxSwaps);
                Status = Local.Busy ? L.T("本地估算中…", "Local estimate…") : Local.Progress;
                if (!Local.Busy) solving = false;
            }
        }

        public void StepOnce()
        {
            if (Mode == RunMode.Manual) singleStep = true;
        }

        public void RunTurn()
        {
            if (Mode == RunMode.Manual) Mode = RunMode.Turn;
        }

        public void ToggleAuto()
        {
            Mode = Mode == RunMode.Auto ? RunMode.Manual : RunMode.Auto;
            if (Mode == RunMode.Auto) resolvesThisTurn = 0;
        }

        public void Tick(float now)
        {
            BattleManager bm = Singleton<BattleManager>.Instance();
            if (bm == null || !bm.inBattle || bm.battleOver)
            {
                if (Plan != null || solving) Reset();
                Status = bm != null && bm.inBattle ? L.T("战斗结束", "Battle over") : "";
                return;
            }

            CollectSolve();

            string battleFile = SolverLogger.Log.BattleFilePath;
            if (battleFile != null && battleFile != warmedBattle && !PreferLocal && SolverLogger.Recorder.Active)
            {
                warmedBattle = battleFile;
                Brain.Warm(battleFile);
            }

            if (bm.turnStatus != TurnStatus.PlayerTurn || IsUserBusy())
            {
                if (bm.turnStatus != TurnStatus.PlayerTurn) Status = L.T("等待我方回合…", "Waiting for your turn…");
                return;
            }

            // 回合开始后等抽牌动画稳定一下
            if (turnSeenRound != bm.round)
            {
                turnSeenRound = bm.round;
                turnSeenAt = now;
                resolvesThisTurn = 0;
                if (Mode == RunMode.Turn) Mode = RunMode.Manual;
            }
            if (now - turnSeenAt < 0.6f) return;

            if (endedRound == bm.round) return; // 已经点了结束回合，等结算开始

            bool stale = Plan == null || planRound != bm.round || (expectedSig != null && expectedSig != Signature());
            if (stale && !solving)
            {
                if (AutoSolve || Mode != RunMode.Manual)
                {
                    Resolve();
                }
                return;
            }
            if (solving || !HasPlan) return;

            bool wantAction = Mode != RunMode.Manual || singleStep;
            if (!wantAction || now < nextActionAt) return;
            string token = NextToken;
            if (token == null) return;

            singleStep = false;
            if (!ActionExecutor.Execute(token, out string error))
            {
                LastError = Describe.Token(token) + L.T("：", ": ") + error;
                Debug.Log("[Planner] step failed: " + token + " (" + error + ")");
                if (resolvesThisTurn < 2)
                {
                    resolvesThisTurn++;
                    Status = L.T("这一步执行不了，重新求解…", "That step failed, solving again…");
                    Resolve();
                }
                else
                {
                    Status = L.T("连续执行失败，已停下，请手动操作", "Steps keep failing: stopped, please continue by hand");
                    Mode = RunMode.Manual;
                }
                return;
            }
            Done.Add(token);
            Next++;
            nextActionAt = now + ActionInterval;
            if (token == "end")
            {
                Status = L.T("已结束回合", "Turn ended");
                endedRound = bm.round;
                Plan = null;
                expectedSig = null;
                if (Mode == RunMode.Turn) Mode = RunMode.Manual;
                return;
            }
            expectedSig = null; // 动作的动画/效果还在结算，等它们落定再取签名
            pendingSigAt = now + Math.Max(0.2f, ActionInterval * 0.85f);
        }

        private float pendingSigAt = -1f;

        /// <summary>主线程每帧调用：收 Brain/本地结果，以及动作后补记签名。</summary>
        private void CollectSolve()
        {
            if (pendingSigAt > 0f && Time.realtimeSinceStartup >= pendingSigAt)
            {
                pendingSigAt = -1f;
                expectedSig = Signature();
            }
            if (!solving) return;
            if (Local.Busy)
            {
                Local.Step();
                Status = Local.Progress;
                if (Local.Result != null)
                {
                    Accept(Local.Result);
                }
                return;
            }
            switch (Brain.State)
            {
                case BrainClient.ClientState.Ready:
                    Accept(Brain.Result);
                    break;
                case BrainClient.ClientState.Failed:
                    // 服务不可用 → 本地估算
                    Status = Brain.ErrorText + L.T("。改用本地估算", ". Using the local estimate");
                    Local.Start(LocalSeconds, LocalMaxSwaps);
                    if (!Local.Busy) solving = false;
                    break;
                default:
                    Status = Brain.ProgressText;
                    break;
            }
        }

        private void Accept(BrainPlan plan)
        {
            solving = false;
            Plan = plan;
            Next = 0;
            Done.Clear();
            // 求解期间局面若被手动改过，签名对不上 → 下一帧自动重算
            expectedSig = solveSig ?? Signature();
            Status = plan.Local ? L.T("本地估算方案", "Local estimate") : L.T("方案就绪", "Plan ready");
        }

        public static bool IsUserBusy()
        {
            try
            {
                var preview = Singleton<PreviewManager>.Instance();
                if (preview != null && (preview.isExchanging)) return true;
                var drag = Singleton<DragManager>.Instance();
                if (drag != null && drag.isDragging) return true;
                if (PowerController.draggingPowerController != null || PowerController.clickDragging) return true;
                if (ColumnClashesRunning(Singleton<BattleManager>.Instance())) return true;
            }
            catch
            {
                return true;
            }
            return false;
        }

        private static readonly HarmonyLib.AccessTools.FieldRef<BattleManager, bool> ColumnClashesRunning =
            HarmonyLib.AccessTools.FieldRefAccess<BattleManager, bool>("columnClashesRunning");

        /// <summary>决策相关的局面签名（不含悬停预览等瞬时状态）。</summary>
        public static string Signature()
        {
            try
            {
                BattleManager bm = Singleton<BattleManager>.Instance();
                var sb = new StringBuilder();
                sb.Append(bm.round).Append('|').Append(bm.battlePlayerGridOffest).Append('|');
                foreach (EnergyData e in ActionExecutor.Hand())
                {
                    if (e == null) continue;
                    sb.Append(e.power).Append('/').Append(string.Join(",", ActionExecutor.Colors(e))).Append(';');
                }
                ShipData p = bm.playerShip.shipData, en = bm.enemyShip.shipData;
                sb.Append('|').Append(p.currentHP).Append(',').Append(p.currentShield).Append(',').Append(p.currentMove).Append(',').Append(p.currentExchange);
                sb.Append('|').Append(en.currentHP).Append(',').Append(en.currentShield);
                foreach (ShipUnitController u in bm.playerShip.shipUnitControllers)
                {
                    if (u == null || u.shipCell == null) continue;
                    sb.Append('|').Append(u.coor.x).Append(',').Append(u.coor.y).Append(':');
                    foreach (ShipUnitSlot s in u.unitSlots)
                    {
                        sb.Append(s.powerData == null ? "_" : s.powerData.power.ToString()).Append(s.rare).Append(',');
                    }
                }
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
