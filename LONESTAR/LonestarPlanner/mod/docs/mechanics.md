# 游戏机制清单 vs 规划器覆盖度

这份文档是给"这个规划器到底能信多少"用的。左侧是反编译确认过的机制，右侧是当前模型是否覆盖。

> 结论先放这里：**目前的模型是"单回合、静态行战力"的近似**。
> 它不是错的，但它假设了一堆不成立的东西。要把它变成可信的工具，正确的路线不是继续手写机制，
> 而是接**引擎探针**（用游戏自己的预览/结算算结果）——见文末。

---

## 1. 回合结构

`BattleManager` 的循环：

```
PlayerTurnPre  → 生成 token（第一回合 4 枚，之后每回合 +3，手牌上限 10）
PlayerTurn     → 玩家装能量 / 移动 / 点技能；EndTurn
PlayerTurnEnd
TurnCaculatePre/TurnCaculate → 按行结算：result = 我方行战力 - 敌方行战力
              → 结算后 clearLoad：所有槽位清空（装进去的能量消失，不回手）
EnemyTurnPre → EnemyTurn     → 敌方 AI 行动
```

| 机制 | 覆盖 |
|---|---|
| 每回合 token 生成（4 / +3 上限 10） | ✅ 摆位阶段用期望池近似 |
| 槽位回合结束清空 | ✅ 能量求解器只算当回合 |
| 手牌跨回合保留 | ⚠️ 未建模"该用还是该留"（没用掉的 token 在本模型里无代价） |
| 行结算 `playerTotal - enemyTotal` | ✅ 与游戏口径一致 |

---

## 2. 能量与装载

| 机制 | 代码 | 覆盖 |
|---|---|---|
| 一 token 整块进一槽，不拆分 | `ShipUnitController.LoadSlot` | ✅ |
| 颜色门槛 `(int)GetHighestColor() >= slot.rare` | `ShipUnitSlot.LoadCheckColor` | ✅ |
| "第一个能装的槽"，可跳过装不下的低稀有度槽 | `ShipUnitController.LoadSlot` | ✅ |
| 装载顺序影响结果 | 同上 | ✅ 已作为决策变量搜索 |
| 干扰槽 `isDisturbed` | `ShipUnitSlot.isDisturbed` | ✅ |
| **`loadCheckEvent` 钩子：部件/增益可以额外禁止装载** | `ShipUnitSlot.LoadCheckEvent`、`Buff_Taunt` | ✅ 由引擎实测覆盖（不再靠模型猜） |
| **`powerLoading` / `powerLoadingAfter` 钩子：装载时改能量数值** | `BattleSkill` → `IPowerLoading` | ✅ 由引擎实测覆盖 |
| **锁定 token** | `PowerController.locked` | ✅ 读取时排除 |
| 装满触发技能（`IsLoadedAfterOverAllSlot` + `LoadFullCheck`） | `ShipUnitSkill.TriggerLoadFull` | ⚠️ 只当作固定价值，不区分技能 |

---

## 3. 车道与移动（v0.4.0 补上）

游戏里船是**可以整体移动**的：

- `ShipMoveCell.OnMove(±1)` → `ShipController.OnMove(y)`
- `BattleManager.AdjustPlaperGridPosition(y)` → `battlePlayerGridOffest += y`
- 于是 `GetBattleGridYByPlayerShipY(r)` 整体偏移 → 你的第 r 行对上敌人另一行

成本：

- 每回合**移动动作数** `Define.defineValue.moveCount`（默认 1），`playerTurnCount` 时重置
- 每次移动扣 1 点 `ShipData.currentMove`（战斗级资源，靠技能回复；`ShipMoveCell.IsFreeMove` 可免费）
- `TS_MoveExchangeRecover` 之类还会因为移动给 exchange

| 机制 | 覆盖 |
|---|---|
| 移动 d 行后各行对齐变化 | ✅ `LaneAlignment` + `BattlePlanner` 枚举 d |
| 移动预算（动作数 × currentMove） | ✅ 读取 `shipMoveCell.moveCount` 与 `currentMove` |
| 移动触发的技能/免费移动 | ❌ 只按 `IsFreeMove` 粗略放行 |
| 敌方也会移动 | ❌ 未知，按"本回合不动"处理 |

---

## 4. 破坏条（Break）

敌方有破坏条 `BreakSystem.breakCount`：

```csharp
// PowerDataContainer.TotalPower()
if (breakCount.Value == 0) return 0;   // 破坏后该部件战力直接归零
```

也就是说"先把对面打破坏"本身就是核心策略，而且会连锁改变整行战力。

| 机制 | 覆盖 |
|---|---|
| 当前已破坏 → 敌方行战力为 0 | ✅ 读到的 `enemyTotal` 已经包含 |
| 本回合会造成破坏 → 下回合行战力骤降 | ✅ 已建模：赢的行数 ≥ 剩余破坏条时给破防奖励 |

---

## 5. 护盾 / HP / 死亡

- 我方有 `currentShield` / `maxShield`，伤害先吃盾：`HPBarPanel.DamagePreview(total, currentShield, out hpDamage, out shieldDamage)`
- 行输了扣 HP，HP 归零战斗失败（`DeadCheck`）

| 机制 | 覆盖 |
|---|---|
| 行胜负的价值 | ⚠️ 只用 `diff * 权重` 近似 |
| 护盾吸收 | ❌ 未建模 |
| 死亡阈值 | ✅ 已建模：承受伤害 ≥ 我方剩余 HP 时给致死惩罚 |

---

## 6. 其他未建模机制

| 机制 | 代码 | 说明 |
|---|---|---|
| 列冲突 | `TA_FullColumnClash` → `QueueColumnClash` | 特定宝物触发，整列参与结算 |
| 嘲讽 | `Buff_Taunt` | 通过 `loadCheckEvent` 限制谁能装能量 |
| 敌方 AI | `AISequence` / `AIC_*` / `AIPower` | 敌方怎么装能量、怎么移动、是否破坏，未知 |
| 宝物 / 天赋 / 驾驶员 / 舰船技能 | `TS_*` / `TA_*` / `SH_*` | 大量数值修改，部分带预览回调 |
| 事件战 | `BattleEventSkill` | 战斗中触发的事件技能 |
| 商店经济 | `ShopPrice` / `ShopPanel` | 未做 |

---

## 7. 这套模型的能力边界

**可以信的部分**

- 单回合内的"token → 槽位"可行性（颜色/顺序/干扰）
- 行战力的算术（我方槽位总和 vs 读到敌方行战力）
- 移动 d 行之后对齐会变成什么样

**不能信的部分**

- 任何依赖技能效果、破坏条、护盾、嘲讽的结论
- 多回合规划（留 token、预期破坏）
- 敌方下一步会做什么

---

## 8. 正确的下一步：引擎探针

游戏自己就会算这些东西，而且算得比我们准：

| 想评估 | 游戏现成入口 |
|---|---|
| 移动 d 行之后各行战力 | `ShipController.OnMovePreview(d)` → 读 `battleResultInfos` → `OnMovePreviewEnd()` |
| 把某枚 token 装到某个部件后的结果 | `ShipUnitController.LoadEnergyPreview(energy)` / `ShipUnitSlot.LoadPreview` → 读 `battleResultInfos`（预览态） |
| 装满技能会不会触发 | 预览事件 `loadFullPreview` + `LoadFullCheckPreview` |

所以正确架构是：

```
近似模型跑搜索（快，覆盖广）
      ↓ 取前 N 个候选
引擎探针实测（准，但慢、有副作用）
      ↓
输出真正可信的方案
```

接口已经留好：`IAssignmentScorer` / `IPlacementEvaluator`。
下一步就是把 `EngineOracle` 实现出来，把"近似分"换成"游戏算出来的分"。


---

## 附：内置 GM 控制台命令表

反射匹配 `GameMasterManager.Command_<名字>`，参数空格分隔。

`addbountyevent addclue addcoin addcolor adddays additem addlimit addmove addtalent addtreasure
allrace alltreasure bossfightcurrent break clearitem clearpower clearsteamachi difficulty enemy
enemymove event exchange exp fixedTimeStep fontsize gc heal hidetest hideui language lose mono
opendata p pause pilotnotreasure playermove power power2 power3 powercore powers rare recoverdata
refresh resetachi resettreasureunlock resettutorial resume roll setbe setlv setmove
setrandomtalent setroll showtest showui steamcreateitem steamdeleteitem steamgetitemstate
steamgetlocaliteminfo t1..t17 testcount time timescale tohistory unitnum unitpool unlock
victory warp web wenben win`
