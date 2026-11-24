extern alias RealHarmony;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RealHarmony::HarmonyLib;
using UnityEngine;

namespace Lonestar.Engine
{
    /// <summary>
    /// Re-plays a recorded battle headlessly: restores the save, enters the same battle, and feeds
    /// the recorded player decisions through the same game entry points the UI uses. The shared
    /// recorder hooks write a new battle file that can be diffed against the original.
    /// </summary>
    public sealed class ReplayDriver
    {
        private const int MaxTurnFrames = 60 * 600;

        private static bool patched;

        public List<string> Problems { get; } = new List<string>();

        private BattleSession attached;

        public string OutputLog => SolverLogger.Log.BattleFilePath;

        public void Run(Recording rec, string exportDir, string resourcesIndex, string workDir, Action<string> log)
        {
            if (!patched)
            {
                new Harmony("lonestar.headless.recorder").PatchAll(typeof(SolverLogger.Recorder).Assembly);
                patched = true;
            }
            SolverLogger.Log.Init(workDir);

            HeadlessGame.Boot(exportDir, resourcesIndex, System.IO.Path.Combine(workDir, "persistent"), rec.SaveFiles, log);
            HeadlessGame.EnterBattle(rec.EnemyId, rec.BattleEventId);
            if (!WaitForPlayerTurn(0))
            {
                Problems.Add("battle never reached the first player turn");
                return;
            }

            attached = BattleSession.Attach(rec, log);
            foreach (JsonElement action in rec.Actions)
            {
                BattleManager bm = Singleton<BattleManager>.Instance();
                if (!bm.inBattle || bm.battleOver)
                {
                    break;
                }
                try
                {
                    Apply(action);
                }
                catch (Exception ex)
                {
                    Problems.Add($"#{action.GetProperty("n").GetInt64()} {Recording.Type(action)}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            // Let the ending (death check, victory/defeat flow) play out.
            HeadlessGame.Settle(60 * 30);
        }

        private void Apply(JsonElement action)
        {
            BattleManager bm = Singleton<BattleManager>.Instance();
            switch (Recording.Type(action))
            {
                case "act_load":
                {
                    int handIndex = action.GetProperty("hand_index").GetInt32();
                    List<EnergyData> hand = SolverLogger.Snap.HandPowers(Singleton<PowerManager>.Instance());
                    if (handIndex < 0 || handIndex >= hand.Count)
                    {
                        Problems.Add($"#{N(action)} act_load: hand index {handIndex} out of range (hand {hand.Count})");
                        return;
                    }
                    ShipUnitController target = FindUnit(action.GetProperty("target"));
                    if (target == null)
                    {
                        Problems.Add($"#{N(action)} act_load: target unit not found");
                        return;
                    }
                    hand[handIndex].powerController.PowerLoad(target);
                    HeadlessGame.Settle(300);
                    return;
                }
                case "act_move":
                {
                    ShipController ship = action.GetProperty("ship_is_player").GetBoolean() ? bm.playerShip : bm.enemyShip;
                    ship.shipVisual.barPanel.shipMoveCell.OnMove(action.GetProperty("y").GetInt32());
                    HeadlessGame.Settle(300);
                    return;
                }
                case "act_end_turn":
                {
                    if (!IsPlayerCaller(action, "BattlePanel.EndTurn"))
                    {
                        return; // ended by a game effect, which the replay reproduces on its own
                    }
                    int round = bm.round;
                    bm.EndTurn();
                    WaitForPlayerTurn(round);
                    return;
                }
                default:
                    if (!attached.ApplyRecordedAction(action))
                    {
                        Problems.Add($"#{N(action)} {Recording.Type(action)}: replay not implemented");
                    }
                    Problems.AddRange(attached.Problems);
                    attached.Problems.Clear();
                    return;
            }
        }

        private static bool WaitForPlayerTurn(int previousRound)
        {
            BattleManager bm = Singleton<BattleManager>.Instance();
            int frames = UnityRuntime.RunUntil(
                // battleOver starts out true, so only trust it once the battle is set up (inBattle).
                () => bm.inBattle && (bm.battleOver || (bm.turnStatus == TurnStatus.PlayerTurn && bm.round != previousRound)),
                MaxTurnFrames);
            HeadlessGame.Settle(300);
            return frames >= 0;
        }

        private static ShipUnitController FindUnit(JsonElement target)
        {
            BattleManager bm = Singleton<BattleManager>.Instance();
            ShipController ship = target.GetProperty("player").GetBoolean() ? bm.playerShip : bm.enemyShip;
            JsonElement coor = target.GetProperty("coor");
            int x = coor[0].GetInt32(), y = coor[1].GetInt32();
            return ship.shipUnitControllers.FirstOrDefault(u => u != null && u.shipCell != null && u.coor.x == x && u.coor.y == y);
        }

        private static bool IsPlayerCaller(JsonElement action, string frame) =>
            action.TryGetProperty("by", out JsonElement by) && by.EnumerateArray().Any(f => f.GetString()?.Contains(frame) == true);

        private static long N(JsonElement r) => r.GetProperty("n").GetInt64();
    }
}
