using System;
using System.Collections.Generic;
using Lean.Localization;
using LonestarPlanner.Core;
using Tool.Database;
using UnityEngine;
using PC = LonestarPlanner.Core.PowerColor;

namespace LonestarPlanner.Mod
{
    /// <summary>战斗快照：能量求解器要的 BattleState + 摆位求解器要的 PlacementState，两者 id 对齐。</summary>
    public sealed class BattleSnapshot
    {
        public BattleState Battle;
        public PlacementState Placement;

        /// <summary>核心模型 id → 游戏对象。自动接管要靠它把"计划"落成"点击"。</summary>
        public Dictionary<int, ShipUnitController> UnitById = new Dictionary<int, ShipUnitController>();
        public Dictionary<int, EnergyData> TokenById = new Dictionary<int, EnergyData>();
    }

    /// <summary>
    /// 游戏运行时 → 纯逻辑核心 的唯一适配层。
    ///
    /// 两个入口：
    ///   ReadBattle() —— 战斗中：部件位置/槽位/手牌/敌方行战力/交换次数
    ///   ReadDock()   —— 船坞：网格/已装备部件/背包部件/载重/期望能量供给
    ///
    /// 游戏更新后只需要改这个文件。
    /// </summary>
    public static class GameStateReader
    {
        // ---------------- 战斗 ----------------

        public static bool TryReadBattle(out BattleSnapshot snapshot, out string error)
        {
            try
            {
                snapshot = ReadBattle();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                snapshot = null;
                error = ex.Message;
                return false;
            }
        }

        public static BattleSnapshot ReadBattle()
        {
            var battle = Singleton<BattleManager>.Instance();
            if (battle == null || !battle.inBattle)
            {
                throw new InvalidOperationException(L.T("不在战斗中", "Not in battle"));
            }

            var ship = battle.playerShip;
            if (ship == null) throw new InvalidOperationException(L.T("玩家飞船未就绪", "Player ship not ready"));

            var shipData = ship.shipData;
            if (shipData == null || shipData.grid == null) throw new InvalidOperationException(L.T("飞船网格未就绪", "Ship grid not ready"));

            var grid = shipData.grid;
            int width = grid.GetWidth();
            int height = grid.GetHeight();

            var battleState = new BattleState
            {
                RowCount = height,
                Tag = "round " + battle.round,
                EnemyRowPower = new int[height],
                PlayerRowFlatBonus = new int[height],
                EnemyRowHasUnit = new bool[height],
                PlayerRowHasUnit = new bool[height],
            };

            for (int y = 0; y < height; y++)
            {
                var info = battle.GetBattleResultInfosByPlayerShipY(y);
                battleState.EnemyRowPower[y] = info == null ? 0 : info.enemyTotal;
                battleState.EnemyRowHasUnit[y] = info != null && info.enemyShipController != null;
                battleState.PlayerRowHasUnit[y] = info != null && info.playerShipController != null;
            }

            // 车道对齐：船可以整体移动，所以敌方战力必须按"战场行"存
            battleState.Lanes = BuildLanes(battle, ship, height);

            try { battleState.EqualCountsAsWin = Define.defineValue.equalBreak; }
            catch { battleState.EqualCountsAsWin = false; }

            // 破坏条与 HP —— 真实收益模型的关键输入
            try
            {
                var enemy = battle.enemyShip;
                if (enemy != null)
                {
                    var breakSystem = enemy.breakSystem;
                    if (breakSystem != null)
                    {
                        battleState.EnemyBreakCount = breakSystem.breakCount.Value;
                        battleState.EnemyBreakLimit = breakSystem.breakLimit;
                        battleState.EnemyBroken = breakSystem.breakTurn || breakSystem.breakCount.Value == 0;
                    }

                    if (enemy.shipData != null)
                    {
                        battleState.EnemyHpLeft = Math.Max(0, enemy.shipData.currentHP + enemy.shipData.currentShield);
                    }
                }

                if (ship.shipData != null)
                {
                    battleState.PlayerHpLeft = Math.Max(0, ship.shipData.currentHP + ship.shipData.currentShield);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Planner] 读取破坏条/HP 失败: " + ex.Message);
            }

            var placement = new PlacementState
            {
                Grid = new ShipGridModel(width, height, (x, yy) => IsCellUsable(grid, x, yy)),
                LoadLimit = int.MaxValue,
                EnemyRowPower = (int[])battleState.EnemyRowPower.Clone(),
                PlayerRowFlatBonus = new int[height],
                SwapBudget = Math.Max(0, shipData.currentExchange),
                Tag = "battle round " + battle.round,
            };

            var snapshotUnitMap = new Dictionary<int, ShipUnitController>();
            var snapshotTokenMap = new Dictionary<int, EnergyData>();

            int nextId = 0;
            var controllers = ship.shipUnitControllers;
            if (controllers != null)
            {
                for (int i = 0; i < controllers.Count; i++)
                {
                    var controller = controllers[i];
                    if (controller == null || controller.destroyed) continue;

                    // 辅助部件不能装能量，但可以换位（光环/相邻效果），所以：
                    //   - 进 PlacementState（给换位搜索用）
                    //   - 不进 BattleState（能量分配与行战力只算 powerable）
                    bool powerable = controller.powerable;

                    var coor = controller.coor;
                    if (coor.y < 0 || coor.y >= height) continue;

                    var unitState = new UnitState
                    {
                        Id = nextId,
                        Name = DisplayName(controller.unitData),
                        Row = coor.y,
                        Col = coor.x,
                    };

                    var card = new UnitCard
                    {
                        Id = nextId,
                        Name = unitState.Name,
                        LoadCost = 0,
                        Powerable = powerable,
                    };

                    int slotPowerTotal = 0;
                    var slots = controller.unitSlots;
                    if (slots != null)
                    {
                        for (int s = 0; s < slots.Count; s++)
                        {
                            var slot = slots[s];
                            if (slot == null) continue;

                            var requirement = ToPlannerColor(slot.rare);
                            card.SlotRarities.Add((int)requirement);

                            var slotState = new SlotState
                            {
                                Index = s,
                                Requirement = requirement,
                                Disturbed = slot.isDisturbed,
                            };

                            var power = slot.powerData;
                            if (power != null)
                            {
                                int value = Math.Max(1, power.power);
                                slotPowerTotal += value;
                                // id 用保留段，避免和手牌 token id 撞号
                                slotState.Loaded = new EnergyToken(1000 + nextId * 16 + s, value, requirement);
                            }

                            unitState.Slots.Add(slotState);
                        }
                    }

                    int total = 0;
                    try
                    {
                        if (controller.powerDataContainer != null) total = controller.powerDataContainer.TotalPower();
                    }
                    catch
                    {
                        total = 0;
                    }

                    int flat = Math.Max(0, total - slotPowerTotal);
                    unitState.FlatPowerBonus = flat;
                    card.FlatPowerBonus = flat;

                    if (powerable) battleState.Units.Add(unitState);
                    placement.Units.Add(card);
                    placement.CurrentLayout.Add(new PlacementSlot(nextId, coor.x, coor.y));
                    snapshotUnitMap[nextId] = controller;

                    nextId++;
                }
            }

            var powerManager = Singleton<PowerManager>.Instance();
            var hand = powerManager == null ? null : powerManager.GetHandPowers();
            if (hand != null)
            {
                for (int i = 0; i < hand.Count; i++)
                {
                    var energy = hand[i];
                    if (energy == null) continue;

                    // 被技能锁住的 token 本回合不能用，直接排除（否则会给出做不到的建议）
                    if (energy.powerController != null && energy.powerController.locked) continue;

                    var color = ReadHighestColor(energy);
                    var token = new EnergyToken(i, Math.Max(1, energy.power), color);
                    battleState.Hand.Add(token);
                    placement.TokenPool.Add(new EnergyToken(i, token.Power, color));
                    snapshotTokenMap[i] = energy;
                }
            }

            return new BattleSnapshot
            {
                Battle = battleState,
                Placement = placement,
                UnitById = snapshotUnitMap,
                TokenById = snapshotTokenMap,
            };
        }

        /// <summary>
        /// 构造车道对齐信息。
        /// BattleResultInfo 的 key 就是战场行号，value.enemyTotal 是该行敌方战力；
        /// 玩家第 r 行当前映射到 GetBattleGridYByPlayerShipY(r)，移动 d 行后映射 +d。
        /// </summary>
        private static LaneAlignment BuildLanes(BattleManager battle, PlayerShipController ship, int height)
        {
            var lanes = new LaneAlignment
            {
                PlayerRowToBattleRow = new int[height],
                MaxShift = 0,
            };

            for (int r = 0; r < height; r++)
            {
                lanes.PlayerRowToBattleRow[r] = battle.GetBattleGridYByPlayerShipY(r);
            }

            int maxBattleRow = 0;
            var infos = battle.battleResultInfos;
            if (infos != null)
            {
                foreach (var kv in infos)
                {
                    if (kv.Key > maxBattleRow) maxBattleRow = kv.Key;
                }

                var enemyByRow = new int[maxBattleRow + 1];
                foreach (var kv in infos)
                {
                    enemyByRow[kv.Key] = kv.Value == null ? 0 : kv.Value.enemyTotal;
                }
                lanes.EnemyPowerByBattleRow = enemyByRow;
            }
            else
            {
                lanes.EnemyPowerByBattleRow = new int[1];
            }

            // 本回合能移动几行：受"每回合移动动作数"和 currentMove 共同限制
            int actions = 0;
            try
            {
                var moveCell = GetMoveCell(ship);
                actions = moveCell != null ? moveCell.moveCount : Define.defineValue.moveCount;
            }
            catch
            {
                actions = 0;
            }

            bool canPay = ship.shipData.currentMove > 0
                          || ShipMoveCell.IsFreeMove(1)
                          || ShipMoveCell.IsFreeMove(-1);

            lanes.MaxShift = canPay ? Math.Max(0, actions) : 0;
            return lanes;
        }

        public static ShipMoveCell GetMoveCell(PlayerShipController ship)
        {
            try
            {
                if (ship == null || ship.shipVisual == null) return null;
                var bar = ship.shipVisual.barPanel;
                return bar == null ? null : bar.shipMoveCell;
            }
            catch
            {
                return null;
            }
        }

        // ---------------- 船坞 ----------------

        public static bool TryReadDock(out DockSnapshot snapshot, out string error)
        {
            try
            {
                snapshot = ReadDock();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                snapshot = null;
                error = ex.Message;
                return false;
            }
        }

        public static DockSnapshot ReadDock()
        {
            var manager = Singleton<ShipDatasManager>.Instance();
            var ship = manager == null ? null : manager.currentPlayerShipData;
            if (ship == null) throw new InvalidOperationException(L.T("尚未开局（没有玩家飞船）", "No run in progress (no player ship)"));

            var grid = ship.grid;
            if (grid == null) throw new InvalidOperationException(L.T("飞船网格未初始化", "Ship grid not initialised"));

            int width = grid.GetWidth();
            int height = grid.GetHeight();

            var state = new PlacementState
            {
                Grid = new ShipGridModel(width, height, (x, y) => IsCellUsable(grid, x, y)),
                LoadLimit = ship.shipLoadLimit,
                EnemyRowPower = new int[height],
                PlayerRowFlatBonus = new int[height],
                SwapBudget = int.MaxValue,
                Tag = "dock",
            };

            var snapshot = new DockSnapshot
            {
                State = state,
                ShipData = ship,
            };

            int nextId = 0;

            // 已装备的部件
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var cell = grid.GetContent(x, y);
                    if (cell == null || cell.shipCellStatus == ShipCellStatus.None) continue;
                    if (cell.unitData == null) continue;

                    bool powerable = cell.shipUnitController == null || cell.shipUnitController.powerable;
                    var card = MakeCard(nextId, cell.unitData, powerable);
                    card.Pinned = !powerable; // 辅助部件位置不动（效果模型算不出来）
                    state.Units.Add(card);
                    state.CurrentLayout.Add(new PlacementSlot(nextId, x, y));
                    snapshot.EquippedByCardId[nextId] = cell.unitData;
                    nextId++;
                }
            }

            // 背包里的部件
            var items = Singleton<ItemManager>.Instance();
            var inventory = items == null ? null : items.itemUnits;
            if (inventory != null)
            {
                for (int i = 0; i < inventory.Count; i++)
                {
                    var item = inventory[i];
                    if (item == null || item.unitData == null) continue;
                    state.Units.Add(MakeCard(nextId, item.unitData, IsCannon(item.unitData)));
                    snapshot.ItemByCardId[nextId] = item;
                    nextId++;
                }
            }

            var pool = BuildExpectedPool(ship);
            state.TokenPool.AddRange(pool);

            return snapshot;
        }

        // ---------------- 公共 ----------------

        /// <summary>
        /// 按能量核心的权重抽样出一个"期望手牌"，用于评估整场战斗的表现。
        /// 默认按前 3 回合：4 枚 + 3 枚 + 3 枚 = 10 枚。
        /// </summary>
        public static List<EnergyToken> BuildExpectedPool(PlayerShipData ship, int turns = 3)
        {
            var pool = new List<EnergyToken>();
            if (ship == null) return pool;

            var rng = new System.Random(20260817);
            int count = 4 + Math.Max(0, turns - 1) * 3;

            var powerWeights = ship.powerWeights;
            var colorWeights = ship.powerColorWeights;

            for (int i = 0; i < count; i++)
            {
                int power = 1;
                if (powerWeights != null && powerWeights.Count > 0)
                {
                    power = powerWeights[rng.Next(powerWeights.Count)].power;
                }

                var color = PC.White;
                if (colorWeights != null && colorWeights.Count > 0)
                {
                    var entry = colorWeights[rng.Next(colorWeights.Count)];
                    if (entry != null && entry.powerColors != null)
                    {
                        color = ToPlannerColor((int)entry.powerColors.GetHighestColor());
                    }
                }

                pool.Add(new EnergyToken(i, Math.Max(1, power), color));
            }

            return pool;
        }

        private static UnitCard MakeCard(int id, UnitData unitData, bool powerable)
        {
            var card = new UnitCard
            {
                Id = id,
                Name = DisplayName(unitData),
                LoadCost = unitData == null ? 0 : Math.Max(0, unitData.GetEquiptLimit()),
                Powerable = powerable,
            };

            if (unitData != null)
            {
                var rares = UnitData.GetSlotsRares(unitData);
                if (rares != null)
                {
                    for (int i = 0; i < rares.Count; i++) card.SlotRarities.Add(rares[i]);
                }
            }

            return card;
        }

        private static bool IsCannon(UnitData unitData)
        {
            try
            {
                var data = unitData.GetData();
                return data != null && data.Type == (int)ShipUnitTag.Cannon;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsCellUsable(GridMap<ShipCell> grid, int x, int y)
        {
            try
            {
                var cell = grid.GetContent(x, y);
                return cell != null && cell.shipCellStatus != ShipCellStatus.None;
            }
            catch
            {
                return false;
            }
        }

        private static PC ReadHighestColor(EnergyData energy)
        {
            try
            {
                if (energy.powerColors != null) return ToPlannerColor((int)energy.powerColors.GetHighestColor());
            }
            catch
            {
                // ignore
            }
            return PC.White;
        }

        private static PC ToPlannerColor(int raw)
        {
            if (raw >= (int)PC.Purple) return PC.Purple;
            if (raw == (int)PC.Blue) return PC.Blue;
            return PC.White;
        }

        private static string DisplayName(UnitData unitData)
        {
            try
            {
                if (unitData == null) return "?";
                var data = unitData.GetData();
                if (data == null) return "ID" + unitData.id;

                string text = LeanLocalization.GetTranslationText(data.Name, data.Name, true);
                return string.IsNullOrEmpty(text) ? data.Name : text;
            }
            catch
            {
                return "?";
            }
        }
    }
}
