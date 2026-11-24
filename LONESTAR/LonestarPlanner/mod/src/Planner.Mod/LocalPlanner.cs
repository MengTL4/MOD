using System;
using System.Collections.Generic;
using LonestarPlanner.Core;
using UnityEngine;

namespace LonestarPlanner.Mod
{
    /// <summary>
    /// 求解服务不可用时的后备：旧的"游戏预览探针 + 局部搜索"（只看本回合）。
    /// 结果转换成和 Brain 一样的动作令牌，HUD/自动接管走同一套执行路径。
    /// </summary>
    public sealed class LocalPlanner
    {
        private readonly EngineOracle oracle = new EngineOracle();
        private ProbeSearchSession session;
        private BattleSnapshot snapshot;

        public BrainPlan Result;
        public string Progress = "";

        public bool Busy => session != null;

        public void Start(float seconds, int maxSwaps)
        {
            Result = null;
            session = null;
            if (!GameStateReader.TryReadBattle(out snapshot, out string error))
            {
                Progress = L.T("读取局面失败：", "Cannot read the battle: ") + error;
                return;
            }
            if (!oracle.CanProbe(Singleton<BattleManager>.Instance()))
            {
                Progress = L.T("本地估算不可用", "Local estimate unavailable");
                return;
            }
            int swaps = Math.Max(0, Math.Min(snapshot.Placement.SwapBudget, maxSwaps));
            session = new ProbeSearchSession(snapshot, oracle, PlanSelector.NewOptions(),
                new EnergySolverOptions { BeamWidth = 384, ExactTokenLimit = 4 }, swaps, 400, seconds);
        }

        public void Cancel()
        {
            session = null;
        }

        /// <summary>每帧推进几毫秒（不卡画面）。</summary>
        public void Step()
        {
            if (session == null) return;
            session.Step(0.006f);
            Progress = L.T("本地估算中…", "Local estimate…");
            if (!session.Done) return;
            ProbeSearchResult r = session.Finish();
            session = null;
            Result = ToPlan(r);
        }

        private BrainPlan ToPlan(ProbeSearchResult r)
        {
            var plan = new BrainPlan { Local = true, Value = "local" };
            BattleManager bm = Singleton<BattleManager>.Instance();
            plan.Round = bm.round;
            plan.PlayerHp = bm.playerShip.shipData.currentHP;
            plan.EnemyHp = bm.enemyShip.shipData.currentHP;
            if (r == null) return plan;

            // 部件 id → 坐标，按换位顺序推演
            var coor = new Dictionary<int, Vector2Int>();
            foreach (var kv in snapshot.UnitById)
            {
                if (kv.Value != null && kv.Value.shipCell != null) coor[kv.Key] = kv.Value.coor;
            }
            foreach (SwapMove s in r.Swaps)
            {
                int? a = null, b = null;
                foreach (var kv in coor)
                {
                    if (kv.Value.x == s.X1 && kv.Value.y == s.Y1) a = kv.Key;
                    if (kv.Value.x == s.X2 && kv.Value.y == s.Y2) b = kv.Key;
                }
                if (a == null && b == null) continue;
                plan.Plan.Add(a != null
                    ? "swap:" + s.X1 + "," + s.Y1 + ">" + s.X2 + "," + s.Y2
                    : "swap:" + s.X2 + "," + s.Y2 + ">" + s.X1 + "," + s.Y1);
                if (a != null) coor[a.Value] = new Vector2Int(s.X2, s.Y2);
                if (b != null) coor[b.Value] = new Vector2Int(s.X1, s.Y1);
            }
            for (int i = 0; i < Math.Abs(r.Shift); i++)
            {
                plan.Plan.Add("move:" + (r.Shift > 0 ? 1 : -1));
            }
            if (r.Energy != null)
            {
                foreach (PlanStep step in r.Energy.Steps)
                {
                    if (!coor.TryGetValue(step.UnitId, out Vector2Int c)) continue;
                    plan.Plan.Add("loadp:" + step.TokenPower + "/" + (int)step.TokenColor + "@" + c.x + "," + c.y);
                }
            }
            plan.Plan.Add("end");
            if (r.Probe != null && r.Probe.Success && r.Probe.Result != null)
            {
                for (int i = 0; i < r.Probe.Result.Length; i++)
                {
                    bool both = (r.Probe.PlayerHasUnit == null || r.Probe.PlayerHasUnit[i]) && (r.Probe.EnemyHasUnit == null || r.Probe.EnemyHasUnit[i]);
                    plan.Lines.Add(new[] { i, r.Probe.PlayerPower[i], r.Probe.EnemyPower[i], r.Probe.Result[i], both ? 1 : 0, i, i });
                    if (!both) continue;
                    if (r.Probe.Result[i] > 0) plan.TurnDealt += r.Probe.Result[i];
                    else plan.TurnTaken += -r.Probe.Result[i];
                }
            }
            return plan;
        }
    }
}
