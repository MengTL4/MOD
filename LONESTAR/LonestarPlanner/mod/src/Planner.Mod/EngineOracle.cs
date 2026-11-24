using System;
using System.Collections.Generic;
using LonestarPlanner.Core;
using UnityEngine;

namespace LonestarPlanner.Mod
{
    /// <summary>一次换位的坐标（战斗内）</summary>
    public struct SwapMove
    {
        public int X1;
        public int Y1;
        public int X2;
        public int Y2;

        public SwapMove(int x1, int y1, int x2, int y2)
        {
            X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
        }

        public override string ToString()
        {
            return "(" + X1 + "," + Y1 + ")↔(" + X2 + "," + Y2 + ")";
        }
    }

    /// <summary>一次引擎实测的结果（交给 UI 展示，也用来和模型预测做对账）。</summary>
    public sealed class EngineProbeResult
    {
        public bool Success;
        public string Error;

        public int Shift;
        public int SwapCount;
        public double Score;

        public int[] PlayerPower;
        public int[] EnemyPower;
        public bool[] PlayerHasUnit;
        public bool[] EnemyHasUnit;
        public int[] Result;

        public string Summary()
        {
            if (!Success) return "实测失败: " + Error;

            var parts = new List<string>();
            for (int i = 0; i < Result.Length; i++)
            {
                bool empty = (PlayerHasUnit != null && !PlayerHasUnit[i]) || (EnemyHasUnit != null && !EnemyHasUnit[i]);
                parts.Add("R" + i + " " + (empty ? "空" : PlayerPower[i] + "v" + EnemyPower[i] + "=" + Result[i]));
            }
            return "实测 score=" + Score.ToString("0.##") + "  " + string.Join("  ", parts.ToArray());
        }
    }

    /// <summary>
    /// 引擎探针：不靠我们自己写公式，而是把候选方案塞进游戏的预览系统，
    /// 让游戏自己算出每行的结果，再读回来。
    ///
    /// 支持三类动作的组合实测：
    ///   - 换位：TeleportPreview(a,b) 是可逆的（再调用一次就换回来），
    ///           且不会触发 exchange 事件、不扣 exchange
    ///   - 移动：AdjustPlaperGridPosition + grid.SetGridWorldPosition（反向还原）
    ///   - 装载：PreviewManager.StartPreview + LoadEnergyPreview
    ///
    /// 所有还原都在 finally 里；任何异常都会把探针整体禁用。
    /// </summary>
    public sealed class EngineOracle
    {
        private bool _broken;

        public bool IsBroken
        {
            get { return _broken; }
        }

        public bool CanProbe(BattleManager battle)
        {
            if (_broken) return false;
            if (battle == null || !battle.inBattle) return false;
            if (battle.turnStatus != TurnStatus.PlayerTurn) return false;
            if (battle.playerShip == null || battle.playerShip.shipData == null) return false;

            var preview = Singleton<PreviewManager>.Instance();
            if (preview != null && (preview.isPreviewing || preview.isExchanging)) return false;

            if (PowerController.draggingPowerController != null) return false;
            if (PowerController.clickDragging) return false;

            return true;
        }

        /// <summary>
        /// 实测一个候选方案：换位 → 移动 → 预装能量 → 读每行结果 → 全部还原。
        /// 是真的"试一下"，不会消耗 token / exchange / currentMove。
        /// </summary>
        public EngineProbeResult Probe(
            BattleSnapshot snapshot,
            Plan plan,
            int shift,
            IReadOnlyList<SwapMove> swaps,
            ScorerOptions options)
        {
            var result = new EngineProbeResult { Shift = shift, SwapCount = swaps == null ? 0 : swaps.Count };
            options = options ?? new ScorerOptions();

            var battle = Singleton<BattleManager>.Instance();
            if (snapshot == null || plan == null)
            {
                result.Error = "没有可实测的方案";
                return result;
            }

            if (!CanProbe(battle))
            {
                result.Error = "当前状态不允许实测";
                return result;
            }

            var ship = battle.playerShip;
            var preview = Singleton<PreviewManager>.Instance();
            var exchange = ship as PlayerExchangeShipController;

            int appliedShift = 0;
            int appliedSwaps = 0;
            var touched = new List<ShipUnitController>();
            bool previewStarted = false;

            try
            {
                // ---- 1. 换位（可逆）----
                if (swaps != null && swaps.Count > 0)
                {
                    if (exchange == null)
                    {
                        result.Error = "飞船没有交换控制器";
                        return result;
                    }

                    for (int i = 0; i < swaps.Count; i++)
                    {
                        var s = swaps[i];
                        exchange.TeleportPreview(new Vector2Int(s.X1, s.Y1), new Vector2Int(s.X2, s.Y2));
                        appliedSwaps++;
                    }
                }

                // ---- 2. 移动（可逆）----
                if (shift != 0)
                {
                    battle.AdjustPlaperGridPosition(shift);
                    ship.shipData.grid.SetGridWorldPosition(0, shift);
                    appliedShift = shift;
                }

                // ---- 3. 预装能量 ----
                if (plan.Steps.Count > 0)
                {
                    ShipUnitController firstUnit = null;
                    var resolutions = new List<KeyValuePair<ShipUnitController, EnergyData>>();

                    for (int i = 0; i < plan.Steps.Count; i++)
                    {
                        var step = plan.Steps[i];

                        ShipUnitController unit;
                        if (!snapshot.UnitById.TryGetValue(step.UnitId, out unit) || unit == null || unit.destroyed)
                        {
                            continue;
                        }

                        EnergyData energy;
                        if (!snapshot.TokenById.TryGetValue(step.TokenId, out energy) || energy == null)
                        {
                            continue;
                        }

                        resolutions.Add(new KeyValuePair<ShipUnitController, EnergyData>(unit, energy));
                        if (firstUnit == null) firstUnit = unit;
                    }

                    if (resolutions.Count > 0)
                    {
                        preview.StartPreview(() =>
                        {
                            for (int i = 0; i < resolutions.Count; i++)
                            {
                                var unit = resolutions[i].Key;
                                var energy = resolutions[i].Value;
                                if (unit == null || unit.destroyed || energy == null) continue;
                                try
                                {
                                    unit.LoadEnergyPreview(energy);
                                }
                                catch (Exception ex)
                                {
                                    Debug.LogWarning("[Planner] probe load preview failed: " + ex.Message);
                                }
                            }
                        }, firstUnit, null);

                        previewStarted = preview.isPreviewing;
                        if (!previewStarted)
                        {
                            result.Error = "游戏未进入预览状态（可能关闭了预览功能），无法实测";
                            return result;
                        }

                        for (int i = 0; i < resolutions.Count; i++)
                        {
                            if (!touched.Contains(resolutions[i].Key)) touched.Add(resolutions[i].Key);
                        }
                    }
                }

                // ---- 4. 强制重算并读值 ----
                battle.UpdateInfoForce();

                int rows = snapshot.Battle.RowCount;
                result.PlayerPower = new int[rows];
                result.EnemyPower = new int[rows];
                result.PlayerHasUnit = new bool[rows];
                result.EnemyHasUnit = new bool[rows];
                result.Result = new int[rows];

                var diffs = new int[rows];
                for (int r = 0; r < rows; r++)
                {
                    var info = battle.GetBattleResultInfosByPlayerShipY(r);
                    int player = info == null ? 0 : info.playerTotal;
                    int enemy = info == null ? 0 : info.enemyTotal;
                    int diff = info == null ? 0 : info.result;

                    result.PlayerPower[r] = player;
                    result.EnemyPower[r] = enemy;
                    result.PlayerHasUnit[r] = info != null && info.playerShipController != null;
                    result.EnemyHasUnit[r] = info != null && info.enemyShipController != null;
                    result.Result[r] = diff;
                    diffs[r] = diff;
                }

                result.Score = RowScorer.Score(diffs, snapshot.Battle.EnemyBreakCount, snapshot.Battle.EnemyHpLeft, options, snapshot.Battle.PlayerHpLeft, snapshot.Battle.EnemyBroken, result.PlayerHasUnit, result.EnemyHasUnit);
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                Debug.LogWarning("[Planner] probe failed: " + ex);
                return result;
            }
            finally
            {
                // ---- 5. 还原（无论成败）----
                try
                {
                    for (int i = 0; i < touched.Count; i++)
                    {
                        var unit = touched[i];
                        if (unit == null) continue;

                        if (unit.powerDataContainer != null) unit.powerDataContainer.ClearPreview();

                        var slots = unit.unitSlots;
                        if (slots != null)
                        {
                            for (int s = 0; s < slots.Count; s++)
                            {
                                var slot = slots[s];
                                if (slot == null) continue;
                                slot.powerDataPreview = null;
                                slot.emptyDatasPreview.Clear();
                            }
                        }
                    }

                    if (previewStarted && preview.isPreviewing)
                    {
                        preview.EndPreview();
                    }
                }
                catch (Exception ex)
                {
                    _broken = true;
                    Debug.LogWarning("[Planner] probe cleanup failed (probe disabled): " + ex);
                }

                try
                {
                    if (appliedShift != 0)
                    {
                        battle.AdjustPlaperGridPosition(-appliedShift);
                        ship.shipData.grid.SetGridWorldPosition(0, -appliedShift);
                    }
                }
                catch (Exception ex)
                {
                    _broken = true;
                    Debug.LogWarning("[Planner] probe shift revert failed (probe disabled): " + ex);
                }

                try
                {
                    // 反向再换一次 = 还原
                    if (appliedSwaps > 0 && exchange != null && swaps != null)
                    {
                        for (int i = appliedSwaps - 1; i >= 0; i--)
                        {
                            var s = swaps[i];
                            exchange.TeleportPreview(new Vector2Int(s.X1, s.Y1), new Vector2Int(s.X2, s.Y2));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _broken = true;
                    Debug.LogWarning("[Planner] probe swap revert failed (probe disabled): " + ex);
                }

                try
                {
                    battle.UpdateInfoForce();
                }
                catch
                {
                    // 结算重算失败不影响战斗继续
                }
            }
        }
    }
}
