# LONESTAR 战斗机制规格（求解器版）

本文档基于 `decompiled/Assembly-CSharp` 的**原样反编译代码**（行号即该目录下 `.cs` 文件的行号），逐条给出战斗的回合流程、玩家动作空间与合法性、能量/行能量汇总、结算公式、敌方 AI、buff/技能触发体系，以及供状态哈希使用的字段清单。

约定：

- **"玩家船 / 敌方船"**：`BattleManager.playerShip`（`PlayerExchangeShipController`）/ `BattleManager.enemyShip`（`EnemyShipController`）。
- **"local y / line"**：船自身网格坐标系中的行号（`ShipUnitController.coor.y`、`ShipUnitController.shipData.grid` 的行）。
- **"battle row / key"**：`BattleManager.battlePlayerGrid / battleEnemyGrid` 的行号，也是 `battleResultInfos` 的 key（世界坐标对齐用的中间坐标系）。
- 标注 **【待验证】** 的结论是代码可读出、但没有用现有 3 场录制逐帧确认过的点。
- 标注 **【推导】** 的是由多个函数组合推出的等价公式（附源码公式，便于独立复核）。

---

## 0. 相关文件与职责

| 文件 | 职责 | 关键成员 |
|---|---|---|
| `BattleManager.cs` | 战斗总控：回合状态机、clash 结算、break/胜负、行能量汇总 | `round`(L107)、`turnStatus`(L105)、`BattleBegin`(L431)、`PlayerTurnPre`(L541)、`PlayerTurn`(L571)、`PlayerTurnEnd`(L582)、`TurnCaculatePre`(L600)、`TurnCaculate`(L610)、`DeadCheck`(L834)、`TurnCaculateEnd`(L908)、`EndTurn`(L1030)、`CaculatePlayerGridPower`(L1232)、`CaculateEnemyGridPower`(L1244)、`RefreshResultPower`(L1304) |
| `BattleResultInfo.cs` | 单行（row）对撞结果 | `GetResult`(L127)、`RefreshDamage`(L194)、`result`(L48) |
| `PowerDataLineContainer.cs` | 单行（local y）的伤害修正钩子 | `damageMul`(L23)、`GetDamagedMulsTotal`(L56)、`GetDamagedTotal`(L145)、`GetDamageTotal`(L164)、`GetLineTotalPower`(L183) |
| `PowerDataContainer.cs` | 单位累计 power | `TotalPower`(L196)、`AddPowerData`(L61)、`OnClear`(L337) |
| `PowerData.cs` | 一条已装填的 power | `power`(L38)、`powerExtra`(L65)、`powerAdded`(L84)、`powerDataType`、`slot`、`from`、`isClearBySlot`、`endTurn` |
| `EnergyData.cs` | 手牌上的一张能量 | `power`(L15，setter 钳到 [1,`Define.powerLimit`=9])、`powerColors`、`round` |
| `PowerController.cs` | 手牌 UI 对象 + 玩家装填入口 | `PowerLoad`(L811)、`OnBeginDrag`(L597)、`OnEndDrag`(L671) |
| `PowerManager.cs` | 手牌/牌库管理 | `handPowers`(L28)、`powerCountLimit=10`(L34)、`InitCore`(L75)、`GetPowerNumByPowerCore`(L159)、`GetPowerColorByPowerCore`(L181)、`CreatePowerFirstTurn`(L232)、`CreatePowerEveryTurn`(L241)、`Create`(L318) |
| `ShipUnitController.cs` | 单位：槽位、装填、技能、buff | `LoadEnergy`(L666)、`LoadSlot`(L215)、`LoadPower`(L172)、`ClearSlot`(L332)、`InitShipUnitSlot`(L121)、`powerable`(L20)、`shipUnitActive`(L30) |
| `ShipUnitSlot.cs` | 单位上的一个槽 | `rare`(L33)、`powerData`、`emptyDatas`、`isDisturbed`、`Load`(L105)、`LoadCheckColor`(L145)、`LoadCheckDisturb`(L204) |
| `ShipController.cs` | 船的通用行为 | `OnMove`(L88)、`TakeDamage`(L260)、`DeadCheck`(L316)、`Exchange`(L559)、`GetPowerDataLineContainerByY`(L656) |
| `ShipData.cs` | 船数值 | `InitOnBattle`(L104)、`SetHP_Battle`(L186)、`SetCurrentShield_Battle`(L241)、`SetMove_Battle`(L330)、`SetExchange_Battle`(L402) |
| `PlayerShipController.cs` / `PlayerExchangeShipController.cs` | 玩家船 / 可交换船 | `InitController`(L11)、`StartTeleport`(L21)、`TeleportPreview`(L72)、`TeleportEnd`(L84)、`DoTeleportAtEnd`(L158) |
| `ShipMoveCell.cs` | 移动动作入口 | `OnMove`(L126)、`MoveReset`(L190)、`freeMove`(L62)、`moveCount`(L43) |
| `ShipUnitSkill.cs` / `Buff*.cs` | 单位技能 / buff | `Init`(L35)、`InitBuffs`(L138)、`Clickable`(L461)、`Clicked`(L479)、`Buff_Use`、`Buff_Times` |
| `AISequence.cs` / `AIController.cs` / `ENEMY*Asset` | 敌方 AI（纯数据驱动） | `SequenceStartCo`(L42)、`LoadToShipUnit`(L123)、`ReOrder`(L37) |
| `BreakSystem.cs` | 破防（break）系统 | `BreakCount`(L72)、`BreakRecover`(L141) |
| `WantedProcess.cs` / `UtilsClass.cs` | RNG 流 | `GetStateByKey`(L147)、`RandomRange`(L433/L446)、`ShuffleListPowerSystem`(L575) |

**重要前提**：现有无头模拟器（`src/Lonestar.Engine`）把上述代码原样编译运行，因此"用游戏自己的代码推进"是唯一权威语义。本文档的作用是：(a) 给求解器定义**动作空间与合法性筛选**；(b) 给状态哈希定义**充分字段集**；(c) 在需要"不跑引擎的快速近似模型"时提供可复算公式。

---

## 1. 回合流程（状态机）

`turnStatus`（`TurnStatus.cs`）：`BattleStart, EnemyTurnPre, EnemyTurn, EnemyTurnEnd, PlayerTurnPre, PlayerTurn, PlayerTurnEnd, TurnCaculatePre, TurnCaculate, TurnCaculateEnd, BattleEnd`。

### 1.1 主链路

```
InitBattleGrid (BattleManager.cs:242)
  round = 1; turnCaculateCount = 1; battleOver = false; inBattle = true
  PowerManager.Init(); 建 playerShip/enemyShip; 各自 InitController
  playerShip.shipSkill.OnInitController(playerShip);  treasures/talents OnInitController
  每个单位 skill.OnInitProperty()      // 读单位属性 "PA" 加 PowerAdded
  playerFirstCaculateInfo = (playerShipData.grid.height >= enemyShipData.grid.height)
  → 1 秒进场动画 → BattleBegin()

BattleBegin (431)
  turnStatus = BattleStart
  eventSkill.ShowPanel()（若有战斗事件）
  battleStartPreBeforeBefore / battleStartPreBefore / battleStartPre / battleStart /
  battleStartEnd / battleStartEndAfter / battleStartEndAfterOver   （defineValue.doubleStart 或 doubleStartTimes>0 时整组重播）
  if (enemyShip.AIController.surrender) → ShipDownHandler(enemyShip)   // 直接胜利：敌方投降
  YellStart()
  if (round == 0) PlayerTurnPre() else EnemyTurnPre()      // 实际 round 恒为 1 ⇒ 首回合是敌方

EnemyTurnPre (489)
  if (enemyShip.breakSystem != null) breakSystem.BreakRecover()
  turnStatus = EnemyTurnPre
  enemyTurnInit / enemyTurnPre / enemyTurnPreAfter / enemyTurnCount
  if (surrender) ShipDownHandler(enemy) else EnemyTurn()

EnemyTurn (515)
  turnStatus = EnemyTurn
  enemyTurnStart / enemyTurn / enemyTurnAfter / enemyTurnAfterOver
  yield AISequence.SequenceStartCo()     // 见 §7
  EnemyTurnEnd()

EnemyTurnEnd (529)
  turnStatus = EnemyTurnEnd
  enemyTurnEndBefore / enemyTurnEnd / enemyTurnEndAfter
  PlayerTurnPre()

PlayerTurnPre (541)
  turnStatus = PlayerTurnPre
  playerTurnInit / playerTurnPre / playerTurnPreAfter
  round==0: PowerManager.CreatePower()             // 正常不会发生
  round==1: PowerManager.CreatePowerFirstTurn()    // 4 张（defineValue.powerInitNum）
  否则:      PowerManager.CreatePowerEveryTurn()   // 3 张（defineValue.powerNumEveryTurn）
  playerTurnCount          // 触发 ShipMoveCell.MoveReset()、Buff_Times.countLimit 归零等
  PlayerTurn()

PlayerTurn (571)
  turnStatus = PlayerTurn
  playerTurnStart / playerTurn / playerTurnAfter / playerTurnAfterOver
  turnStartMoveCount = playerShip.shipData.currentMove
  —— 玩家自由行动（§3）——
  玩家点"结束回合" → BattleManager.EndTurn() (1030)

PlayerTurnEnd (582)          // 由 EndTurn 启动的协程
  turnStatus = PlayerTurnEnd
  PowerController.lastLoad = null; turnCaculateCount = 1
  playerTurnEndBefore / playerTurnEnd / playerTurnEndAfter
  if (enemyShip.AISequence.breakTurn > 0) enemyShip.AISequence.breakTurn--
  TurnCaculatePre()

TurnCaculatePre (600)
  turnStatus = TurnCaculatePre; turnCaculatePre 事件
  TurnCaculate()

TurnCaculate (610)           // 核心结算，详见 §6
  CaculateUpdaterByTurnEnd()                  // 重算所有行的 playerTotal/enemyTotal/result
  turnStatus = TurnCaculate
  refreshText / turnCaculate
  逐行结算伤害（win/lose/equal）→ DeadCheck(num, num2)
  TurnCaculateEnd()

TurnCaculateEnd (908)
  turnStatus = TurnCaculateEnd; clearingPower = false; DeadEndEffect()
  if (!battleOver) {
      turnCaculateEndPre / turnCaculateEnd / turnCaculateEndAfter / turnCaculateEndAfterOver
      if (IsColumnClash) yield break                  // 列冲撞：不进下一回合
      turnCaculateCount--; if (>0) { TurnCaculatePre(); yield break; }   // 同一回合再次结算
  }
  if (!IsColumnClash) {
      clearingPower = true
      clearLoad / clearLoadEnd                        // ★ 清空所有已装填 power、每回合状态归零
      clearingPower = false
      UpdateInfo()
      if (!battleOver) { round++; RestThePostion(); EnemyTurnPre(); }
  }

RestThePostion (955)
  船/网格视觉复位（-offset），RecreateBattleMessage()  ← 重置 battlePlayerGridOffest/battleEnemyGridOffest 并清空 battleResultInfos
  1 秒；resetShipPos 事件
```

**结论要点**

1. **敌方先手**：`round` 在 `InitBattleGrid` 里被设为 1（L245），`BattleBegin` 的 `if (round == 0)` 分支永不成立，所以第一回合是 `EnemyTurnPre → EnemyTurn → PlayerTurnPre`。玩家第一次看牌时敌方已经装好第一轮的 power。
2. 玩家第一回合抽 4 张（`powerInitNum`），之后每回合抽 3 张（`powerNumEveryTurn`），在 `PlayerTurnPre` 立即抽牌（不是回合结束）。
3. 一回合**固定只结算一次 clash**（`turnCaculateCount = 1`，`PlayerTurnEnd` L588），例外是"列冲撞"与 `turnCaculateCount` 被技能改写（见 §3.7）。
4. `clearLoad` 发生在**每次 clash 结束后**（正常回合即 `TurnCaculateEnd`），它清掉所有槽里的 power，并清掉"仅本回合"的 buff/技能计数（`IClearPower`）。**玩家一回合内装填的 power 只对当回合结算有效**。
5. 移动/网格偏移在 `RestThePostion → RecreateBattleMessage` 时归零：**移动不影响下一回合的初始对位**，只影响当回合 clash 的行配对。

### 1.2 战斗结束（胜负）

```
DeadCheck(playerHitSide, enemyHitSide)  (834)
  playerShip.DeadCheck(playerHitSide); enemyShip.DeadCheck(enemyHitSide)
  if (playerShip.isDead || enemyShip.isDead) { battleOver = true; ...; return true }
```
- `isDead` 只在 `ShipController.TakeDamage` 里被设置：`currentHP <= 0` 时（L284-287）。HP 由 `ShipData.SubCurrent` 钳到 0。
- `TakeDamage(damage, checkDead=true)`（L260）会在扣完血后**立刻**再触发 `DeadCheck` + `DeadEndEffect`；但 `TurnCaculate` 里的 `DamageMaker.Damage` 传的是 `checkDead: false`（`DamageMaker.cs:57`），所以 clash 伤害是**逐行扣、最后统一 `DeadCheck`**（L782）。
- 死亡后：`TurnCaculateEnd → DeadEndEffect → DestroyEffectEnd → ShipDownHandler(死船)`（L1109）→ `BattleEndStopCoroutine()`（`turnStatus = BattleEnd`，停主协程）→ LoadPanel → `BattleEndClear()` → `endCallBack` → `WantedManager.BattleEndCallback`（`WantedManager.cs:306`）。
- **胜负定义**（`WantedManager.cs:306-367`）：玩家死 → 失败（`WantedProcessStatus.Lose`）；敌方死且玩家未死 → 胜利（`Win`）并进入奖励流程。玩家优先判定：双方同归于尽时玩家先死 → 判负。
- 敌方"投降"（`EnemyShipData.shipAsset` 数据 `AIController.surrender`）会在一开局直接判胜，不进入 clash。
- 战斗结束后 `BattleEnd` 还可能有 `BattleChoice`（战后选项）等 UI 流程；`BattleEnd` 之后不再接受玩家动作。

---

## 2. 对象模型（状态载体）

```
BattleManager (单例)
├─ round, turnStatus, battleOver, inBattle, turnCaculateCount, turnStartMoveCount
├─ battlePlayerGrid / battleEnemyGrid : GridMap<int>（世界坐标基准，SetGridWorldPosition 可平移）
├─ battlePlayerGridOffest / battleEnemyGridOffest : int（本回合累计移动量，回合末清零）
├─ battleResultInfos : Dictionary<int /*battle row*/, BattleResultInfo>
├─ playerShip : PlayerShipController(= PlayerShipController, 实战为 PlayerExchangeShipController)
├─ enemyShip  : EnemyShipController
├─ playerFirstCaculateInfo : bool（先算玩家行还是敌方行）
└─ pendingColumnClashes / columnClashPlayerY / columnClashesRunning / endTurnAfterColumnClashes

ShipBaseController
├─ shipData : ShipData, playerflag, isDead, dying
└─ shipUnitControllers : List<ShipUnitController>       // 顺序重要

ShipController : ShipBaseController
├─ powerDataLineContainers : Dictionary<int /*local y*/, PowerDataLineContainer>
├─ unitSetDataLine : Dictionary<ShipUnitController, PowerDataLineContainer>   // 技能注册的行伤害容器覆盖
└─ laserControllers[localY], staticDamage*（预览用）

ShipData
├─ shipCenter, shipSize, grid : GridMap<ShipCell>
├─ currentHP/maxHP, currentShield/maxShield
├─ currentMove/maxMove, currentExchange/maxExchange
└─ colorSlotListBattle : List<int>（战斗期颜色槽库存）

ShipUnitController
├─ unitData(id, lv, slotRefitTypes, args, simulatorLv, order), shipCell
├─ shipUnitTag, powerable（Cannon=1 才计入行能量）, shipUnitActive, destroyed/destroying, enemyLoadable
├─ unitSlots : List<ShipUnitSlot>                        // 顺序重要：装填按此顺序找第一个可用槽
├─ powerDataContainer : PowerDataContainer
├─ buffs : List<Buff>, skill : ShipUnitSkill
└─ coor => shipCell.coor

ShipUnitSlot
├─ rare_(1 White / 2 Blue / 3 Purple；预览期读 rarePreview)
├─ powerData : PowerData（当前装填）
├─ emptyDatas : List<PowerData>（技能塞进"已满槽"的额外 power）
├─ emptyPreCount : int（UI 计数缓存）
└─ isDisturbed : bool

PowerDataContainer
├─ powerDatas : List<PowerData>（本单位的全部 power：槽装填 + 技能直装 + PowerAdded/PowerExtra）
├─ clearDatas / powerDatasPreviewAdd / powerDatasPreviewRemove
└─ 缓存字段 _totalPowerLast

PowerData : { _power, _powerExtra, _powerAdded, powerDataType(Power/PowerExtra/PowerAdded), slot, from(EnergyData), isClearBySlot, isExtra, endTurn, isFromAuraEffect }

BattleResultInfo : { y(battle row), playerShipController, enemyShipController, playerLine, enemyLine, playerTotal, enemyTotal, playerContainer, enemyContainer, damageMaker, result }

EnemyShipController : { AISequence, AIController, breakSystem : BreakSystem, enemyType }
BreakSystem : { breakCount, breakLimit, breakTurn, breakCountPreview }
AISequence : { sequnce : Dict<EnemyShipUnitController, AIProcess>, sequnceChache, sequnceMoves : MoveProcess, sequnceNotices : NoticeProcess, breakTurn }
AIProcess : { AIPowers : List<AIPower>, AIOrder, process, loadPreview }
AIPower : { aiPowerDatas : List<AIPowerData{power, powerColor}>, useSkill, loadCallback }

PowerManager : { handPowers : List<EnergyData>, powerCore(牌库), powerColorCore(颜色库), powerCountLimit=10, turnUsedPowerDatas }
```

---

## 3. 玩家动作空间（求解器动作定义）

一个"回合"内玩家可执行的动作只有下面 6 类；步进语义 = **执行一个动作 → 跑到引擎静止（无协程/无 Tween）→ 检查是否到了下一个 `PlayerTurn` 或战斗结束**（即现有 `ReplayDriver.Apply` + `HeadlessGame.Settle` / `WaitForPlayerTurn` 的做法）。

| # | 动作 | 动作参数 | 官方入口方法 | 录制类型 |
|---|---|---|---|---|
| A1 | 装填一张手牌 | 手牌下标 + 目标单位 | `hand[i].powerController.PowerLoad(unit)` | `act_load` |
| A2 | 上下移动 | `y ∈ {+1,-1}` | `ship.shipVisual.barPanel.shipMoveCell.OnMove(y)` | `act_move` |
| A3 | 交换/瞬移两个格子 | 起始格 + 目标格 | `PlayerExchangeShipController.StartTeleport/TeleportPreview/TeleportEnd/DoTeleportAtEnd` | `act_exchange` |
| A4 | 点击单位技能（一步） | 单位 | `unit.shipUnitSkillActiveHandler.Click()`（内部 `skill.Clicked()`） | `act_click` |
| A5 | 单位技能二段选靶 | 单位 + 目标(单位/格子) | `skill.Clicked()` → `ShipDirPanel.Init(...)` 的 action 回调 | `act_click` + `act_target` |
| A6 | 结束回合 | — | `BattleManager.EndTurn()` | `act_end_turn` |
| A7 | 战斗事件选项（可选） | 选项下标 | `BattleChoiceButton.Click()`（`option.Do()`） | `act_choice` |

> 现有 `ReplayDriver` 实现了 A1/A2/A6（`src/Lonestar.Engine/ReplayDriver.cs:67-109`），A3/A4/A5/A7 记录了但回放未实现。求解器要完整建模必须补上这四类入口（语义见下）。

### 3.1 A1 装填能量（最核心动作）

入口链：`PowerController.PowerLoad(unit)`（`PowerController.cs:811`）→ `unit.LoadEnergy(energyData, onUsed)`（`ShipUnitController.cs:666`）→ `unit.LoadSlot(...)`（L215）→ 逐槽 `ShipUnitSlot.Load(...)`（`ShipUnitSlot.cs:105`）。

#### 合法性（精确定义）

```
PowerLoad(unit):
  power = energyData.power                        // 失败时用于恢复
  if LoadEnergy(...) → true: 成功（能量从手牌移除）
  else: energyData.power = power; 返回 false（手牌不变）

LoadEnergy(energyData):
  if (!shipUnitActive) → false                    // 被睡眠等效果禁用
  if (energyData == null) → false
  if (destroyed || shipController.shipUnitControllersRemove.Contains(this)) → false
  energyData.energyDataLoadInfo = new EnergyDataLoadInfo()   // loadable=true, 其余 false
  pd = LoadSlot(energyData)
  if (pd != null):
      pd.shipUnitController = this
      lastLoad = this（若来自手牌）
      loadPowerPre / loadPower / powerLoading / powerLoadingAfter / callback(→移除手牌) /
      powerLoaded / powerLoadedAfter / (loadInfo.loadOver = true) / powerLoadedAfterOver
      → true
  else → false

LoadSlot(energyData):
  if (!loadInfo.loadable) → null                  // 已被 skill 否决
  for i in 0..unitSlots.Count-1:                  // ★ 顺序 = unitSlots 列表顺序
      if unitSlots[i].Load(energyData, ref slot) == false → continue
      创建 PowerData(power = energyData.power)     // ← 用"事件处理后"的 power
      loadInfo.loadSucc = true
      if (!loadInfo.emptySlot) unitSlots[i].powerData = pd
      else unitSlots[i].emptyDatas.Add(pd)        // 仅当某技能置 emptySlot=true
      pd.from = energyData; pd.slot = slot
      LoadPower(pd)                               // 进 powerDataContainer；PowerAdded 类型额外 AddBuff<Buff_PowerAdded>
      return pd
  return null

ShipUnitSlot.Load(e):
  if (powerData != null) return false             // ★ 槽已被占用则不能覆盖
  slot = this
  return LoadCheckColor(e) && LoadCheckDisturb(e) && LoadCheckEvent(e)
      && (触发 tryLoadPower / tryLoadPowerAfter 后 loadInfo.loadable 仍为 true)

LoadCheckColor(e):  (int)e.powerColors.GetHighestColor() >= rare
                    // White=1 Blue=2 Purple=3；颜色列表降序，GetHighestColor() = colorList[0]
                    // ⇒ 白牌(1)只能进 rare=1；蓝牌(2)可进 rare 1/2；紫牌(3)可进任意
LoadCheckDisturb(e): ★ 粘性判定（见下）
LoadCheckEvent(e):   loadColorCheck 默认 true，由 loadCheckEvent 事件（ITryLoadPower 等）改写
```

**槽顺序规则**：`unitSlots` 是 `InitShipUnitSlot()`（L121）按 `UnitData.GetSlotsRares(unitData)` 的返回顺序创建，随后可被技能整体重排（如 `Skill_ExchangeSlotTarget.Do` 交换两个单位的 `unitSlots` 列表）。**装填永远填"第一个通过检查的空槽"**，因此槽的顺序与每个槽的 `rare` 都是状态的一部分。

**disturbed 粘性判定【待验证】**：`LoadCheckDisturb`（`ShipUnitSlot.cs:204`）只把 `energyDataLoadInfo.disturbPass` 置 true、从不清零：

```csharp
if (energyData.powerController == null) loadInfo.disturbPass = true;  // 非手牌来源
if (!isDisturbed)                        loadInfo.disturbPass = true;  // 本槽未被打扰
return loadInfo.disturbPass;
```
`energyDataLoadInfo` 是**每次 LoadEnergy 新建一次、整个槽循环共享**，所以一旦某个"颜色检查已通过"的槽把 `disturbPass` 置 true，后续被 `isDisturbed` 的槽也会通过。真正的阻断条件是：从第一个颜色通过的槽起，之前所有槽都 `isDisturbed`。绝大多数盘面 `isDisturbed=false`，此时该检查恒真；只有在技能（`Skill_Disturb*`）制造了 disturbed 槽时才需精确复现。

**其它合法性细节**

- **没有"每回合装填次数"上限**：只要手牌、槽位、颜色允许，一回合可以连续装填任意多张。
- **LoadEnergy 本身不检查 `turnStatus`**。玩家侧的 `turnStatus == PlayerTurn` 门禁在 UI 层（`PowerController.OnBeginDrag:599`、`ShipUnitController.StartPreview:855`、`ExchangeCell` 等）。**求解器必须自己强制 `turnStatus == PlayerTurn`**，否则会出现游戏里做不到的动作。
- 装填成功才会把手牌移除：`LoadEnergy → loadSuccessCallback → PowerController.OnUsed → DestroyPowerController → onUsed(energyData) → PowerManager.RemoveFromHandPowers`（`PowerController.cs:830-847`、`PowerManager.cs:410`）。
- 装填失败**不产生任何状态变化**（除了 `energyDataLoadInfo`、lastSelect 等临时字段和"失败提示"），手牌保留 → 属于"空动作"，求解器应从动作集合中剪掉。
- 技能可在 `tryLoadPower` 阶段改写 `energyData.power` 或颜色，从而改变最终装填入的数值。
- **`emptySlot` 分支**（技能置 `loadInfo.emptySlot=true`：`Skill_FirstLoadUnlimited.cs:66,124`、`Skill_LoadHandPowerExtraLoad.cs:73,81`、`Skill_LoadOneNoSlotPowerUp.cs:71,80`）：power 不走 `powerData` 而进 `emptyDatas`，实现"同一槽叠多张"。其中的 `emptyPreCount + 50 <= emptyDatas.Count` 保护是 UI 帧序 hack，正常逻辑不会触发【待验证】。

### 3.2 A2 移动

入口：`ShipMoveCell.OnMove(y)`（`ShipMoveCell.cs:126`），`y=+1` 上、`-1` 下。

```
OnMove(y):
  if (turnStatus != PlayerTurn) return                       // L128
  if (shipData.currentMove == 0 && !IsFreeMove(y)) { 提示; return }   // L132
  if (moveCount <= 0) { 提示; return }                        // L147（每回合移动次数上限）
  moveCount--
  if (isPreviewing) OnMovePreviewEnd()
  if (!IsFreeMove(y)) shipData.SetMove_Battle(-1)             // 消耗 1 点移动力；自由移动不消耗
  shipController.OnMove(y)                                    // ShipController.cs:88
      → BattleManager.AdjustPlaperGridPosition(y)   // battlePlayerGrid 原点 += y; battlePlayerGridOffest += y
      → shipData.grid.SetGridWorldPosition(0, y)    // 船自身网格同样平移
      → moved / movedAfter 事件
```

- `moveCount`（`ShipMoveCell.cs:43`）：**每回合**的移动次数，`playerTurnCount` 事件时 `MoveReset()` 重置为 `Define.defineValue.moveCount`；默认 `DefineValue.moveCount = 1`（`DefineValue.cs:93`）。天赋 `TA_Init_ExtraMoveAndTwice` 会改成 2（`TA_Init_ExtraMoveAndTwice.cs:5`）。
- `shipData.currentMove`：**跨回合的移动力资源**（`maxMove`/`currentMove`，战斗开始 = `DataShip.Move`），`SetMove_Battle(-1)` 才会减；0 时只能靠 `freeMove`（由 `IMoveCheck` 接口的技能每帧写入 `ShipMoveCell.freeMoveUp/Down`）。
- 效果：**改变当回合 clash 的行配对**（§5.4），不改变单位内容。回合末 `RestThePostion` 把偏移归零。

### 3.3 A3 交换 / 瞬移（Exchange）

入口：`ShipUnitVisual.DragBeginActionExchange`（`ShipUnitVisual.cs:735`）/ `DragEndActionExchange`（L765）/ `PlayerExchangeShipController`。

```
开始: DragBeginActionExchange()
  if (isDragging || PowerController.draggingPowerController != null) return false
  if (turnStatus != PlayerTurn) return false
  if (shipData.currentExchange <= 0) return false                       // ★ 资源限制
  if (unit.skill is IClick && unit.GetBuff<Buff_Click>().isPlayer && CheckClickable())
      return false                                                     // 该单位当前"可点击"，拖拽改为点击技能
  StartTeleport(unit.shipCell)                                          // isExchanging = true, 记录 startCoor

悬停: TeleportPointerEnterShipCell(cell) → StartPreview + TeleportPreview(cell)
       TeleportPreview 内部会先 TeleportPreviewOver() 回滚上一次悬停，再交换两格（视觉+gridArray+coor）

确认: TeleportEnd()                                                        // PlayerExchangeShipController.cs:84
  - isExchanging 且 startCell != null
  - turnStatus == PlayerTurn
  - startCell.coor != startCoor（没有真的移动过 → 取消）
  - enterCell != null（鼠标必须停在 startCell 上，否则回滚取消）
  - 返回 true
DragEndActionExchange():
  if (TeleportEnd()):
      shipData.SetExchange_Battle(-1)        // ★ 消耗 1 点 exchange
      DoTeleportAtEnd()                      // exchangePre / exchange 事件、清理预览
```

- **任意两格**都可以交换（包括空格），没有"只能相邻"或"只能同色"的限制；单位 `coor` 改变，`shipUnitControllers` 列表顺序不变。
- 交换可以改变某条 local y 上的单位集合 → 直接影响行能量与 clash。
- 无头推荐序列（与 UI 等价，**待验证**）：`StartTeleport(cellA)` → `TeleportPreview(cellB)` → `DoTeleportAtEnd()` + `SetExchange_Battle(-1)`；注意 `TeleportPreview` 会先回滚旧预览，且 `DoTeleportAtEnd` 依赖 `PreviewManager.startCoor`，需要保证预览状态一致。更"纯逻辑"的做法是 `ShipController.Exchange(coorA, coorB)`（L559，等价 swap）+ `SetExchange_Battle(-1)`，但会漏掉 `exchangePre/exchange` 事件（只有依赖这些事件的天赋/宝物才有区别）。

### 3.4 A4/A5 单位主动技能

入口：`ShipUnitSkillActiveHandler.Click()`（`ShipUnitSkillActiveHandler.cs:114`）→ `ShipUnitSkill.Clicked()`（`ShipUnitSkill.cs:479`）。

```
Clicked():
  buff = GetBuff<Buff_Click>()                                  // ★ 若为 null 会 NRE（代码没有判空）
  if (isPlayer && turnStatus != PlayerTurn) return false
  if (!buff.useable || !shipUnitController.shipUnitActive) return false
  if (!buff.CheckClickable()) return false
  if (this is ITwoStepClick):
      (this as ITwoStepClick).OnTwoStepClick()                  // 打开选靶面板；不消耗次数
      return true
  OnClickTrigger()                                             // Buff_Click.OnTrigger()
  return true
```

**只有 IUse / ITimes 技能可以被点击**：`ShipUnitSkill.InitBuffs()`（L138-182）只在这两种接口下 `AddBuff<Buff_Use>()` / `AddBuff<Buff_Times>()`，并创建 `ShipUnitSkillActiveHandler`；而 `Buff_Click` 的子类只有这两个（`Buff_Use.cs:4`、`Buff_Times.cs:4`）。枚举"本单位现在能不能点"时应用 **`skill.Clickable()`**（L461，内部先判 `GetBuff<Buff_Click>() == null → false`），不要直接调 `Clicked()`（后者不判空会 NRE）。

两种"次数"语义（都是 `Buff.count`/`countLimit` 这对 `DynamicValue<int>`）：

| | 来源属性 | 初始值 | 每回合变化 | CheckClickable | 使用后 |
|---|---|---|---|---|---|
| `Buff_Use`（`IUse`） | `"US"` | `count = countLimit = US` | `ITurnCount`（`playerTurnCount` 事件）→ `OnTurnCount`：`if (once.Value) count--`（钳 ≥0） | `count == 0` | `count = countLimit`（回到初始） |
| `Buff_Times`（`ITimes`） | `"TI"` | `count = TI`, `countLimit = 0` | `IPlayerTurnInit`（`playerTurnInit` 事件）→ `countLimit = 0` | `count > 0 && countLimit < Define.defineValue.timesPerTurnLimit`（默认 1） | `count--; countLimit++` |

（`Buff_Use.OnTrigger` 里还有 `if (!once.Value) return false`；`once` 默认 true。）

- **A5 二段选靶**（`ITwoStepClick.cs`，实现者只有 8 个技能：`Skill_BurnSlot`、`Skill_DestroyUnitCreatePower`、`Skill_ExchangeSlotTarget`、`Skill_ITBFlameThrower`、`Skill_ITBHeatEngines`、`Skill_ITBTeleporter`、`Skill_Printer`、`Skill_ReEquipt`、`Skill_ReplaceHand`）。
  流程：`OnTwoStepClick()` 自己构造候选列表 → `UIManager.ShowPanel(shipDirPanel, panel.Init(list, Do, Preview))` → 玩家点某个候选 → `ShipDirPanel.Init` 里的回调先 `EndPreview()` 再 `action(target)`（`ShipDirPanel.cs:30-33`）= 技能的 `Do(target)`。
  **候选列表由各技能自己决定**（例如 `Skill_ExchangeSlotTarget` 的候选 = 己方所有 `unitSlots.Count > 0` 且不是自己的单位；`Skill_ITBTeleporter` 的候选 = 己方除自己以外的所有单位）。`OnTwoStepClick()` 可能返回 false（无合法目标：如 `Skill_ExchangeSlotTarget` 候选为空 → `return false`，`ShipDirPanel` 仍会被 `Hide`）。**次数在 `Do()` 内 `OnClickTrigger()` 时消耗**，不同技能略有差异【待验证：逐个技能确认是否每次 Do 都消耗】。
- 技能效果千差万别（见 §8 的分类），但都是通过 `LoadPower / RemovePower / LoadEnergy / SetDisturb / SetRare / CreatePower / TakeDamage / Exchange ...` 等公共 API 改状态。**对求解器而言：`act_click`/`act_target` 是"离散动作 + 效果由引擎执行"**，不需要（也不应）在启发式层重写效果。
- 敌方也走同一入口（`AISequence.LoadToShipUnit` 里 `useSkill → Click()`），此时 `isPlayer == false`，不检查 turnStatus。

### 3.5 A6 结束回合

```
EndTurn()  (BattleManager.cs:1030)
  if (columnClashesRunning && inBattle && !battleOver) { endTurnAfterColumnClashes = true; }
  else {
    if (turnStatus != PlayerTurn) return                       // 非法时静默忽略
    if (pendingColumnClashes.Count > 0) { endTurnAfterColumnClashes = true; return; }
    取消拖拽/交换/预览状态
    启动 PlayerTurnEnd() 协程
  }
```
- **唯一的"结束玩家回合"合法途径**；无其它条件（手牌不清空、power 不要求装满）。
- 注意 `EndTurnNextFrame()`（L1064）是给技能用的（下一帧再结束）。

### 3.6 A7 战斗事件选项（可选，出现即需建模）

`BattleChoiceGroup.BattleChoice(...)`（由 `Skill_Final`、战斗事件 `BattleEventSkill`、GM 触发）会生成一组 `BattleChoiceOption`（`BCO_*`），只有 `option.check == true` 的才可点选；点击 → `option.Do()` → `BattleChoiceGroup.Next()`（`BattleChoiceButton.cs:78-92`）。选项可能连锁下一组。选项效果同样是引擎执行。**待验证**：选项面板出现在战斗内时是否阻塞 `EndTurn`（代码未见阻塞，UI 上通常强制玩家先选）。

### 3.7 动作之间的自动推进：列冲撞（Column Clash）

天赋 `TA_FullColumnClash`（`TA_FullColumnClash.cs:55`）在**每次装填完成**后检查 `BattleManager.IsPlayerColumnFull(y)`（`BattleManager.cs:145`：该列所有单位的所有槽都已装填且 `IsLoadedAfterOverCurrent()`），成立则 `QueueColumnClash(y)`。
`BattleManager.LateUpdate`（L412）在 `turnStatus == PlayerTurn && pendingColumnClashes.Count > 0` 时启动 `RunColumnClashes()`（L182）：

```
RunColumnClashes:
  turnCaculateCount 暂存后置 1
  while pending 非空: 若 IsPlayerColumnFull(y) → columnClashPlayerY = y → TurnCaculatePre()（只结算该行！）
  恢复 turnCaculateCount；turnStatus = PlayerTurn
  若 endTurnAfterColumnClashes → PlayerTurnEnd()
```
- `GetClashResultInfos()`（L128）在 `IsColumnClash` 时只返回该行 → **同一玩家回合可发生多次"部分 clash"**。
- 对求解器：只要按"动作→Settle"推进，这个流程自动发生（引擎自己跑）；但**动作序列必须允许"装填触发中途结算"**，且此时 `turnStatus` 会短暂离开 `PlayerTurn` 再回来，不能简单地认为"回合结束前 turnStatus 恒为 PlayerTurn"。

---

## 4. 能量体系（手牌 / 牌库 / 抽牌）

### 4.1 数据结构

- 手牌 `PowerManager.handPowers : List<EnergyData>`，上限 `powerCountLimit = 10`（L34）。
- 牌库：`powerCore.powerWeights`（点数值队列）、`powerCore.powerWeightsCD`（弃牌堆）；颜色同理 `powerColorCore.powerColorWeights` / `...CD`。
- 玩家船的牌库内容来自 `PlayerShipData.powerWeights / powerColorWeights`（`PlayerShipData.ReadFromCSV` 里 `InitCore(dataShip.PowerCoreWeight, dataShip.PowerColorWeight)`，L168）。

### 4.2 初始化与抽牌

```
PowerManager.InitCore()  (L75)          // PlayerShipController.InitController (L22) 调用
  powerCore.ClearWeight(); powerColorCore.ClearWeight()
  foreach w in playerShipData.powerWeights:      powerCore.InitAddWeight(w.power)
  foreach w in playerShipData.powerColorWeights: powerColorCore.InitAddWeight(w.powerColors)
  powerCore.RanmdomOrder(powerWeights)            // = ShuffleListPowerSystem(key="Shuffle_Deck")
  powerColorCore.RanmdomOrder(powerColorWeights)  // 同一个 key，紧接着的下一个状态
```

```
DrawPower:  GetPowerNumByPowerCore()  (L159)
  若 powerWeights 为空: ShuffleListPowerSystem(powerWeightsCD, "Shuffle_Deck") → 搬回 powerWeights，清空 CD
  取 powerWeights[0]，移除，push 到 CD；返回其 power
DrawColor:  GetPowerColorByPowerCore() (L181) — 同上，为空时返回 White

CreatePower(bool refresh=true) (L260)
  = CreatePower(GetPowerNumByPowerCore(), GetPowerColorByPowerCore(), refresh, fromCore:true)
    ↑★ 先抽点数和颜色，再进 Create()
Create(power, colors, ...) (L318)
  if (handPowers.Count >= 10) return null      // ★★ 抽牌结果被丢弃（点数和颜色已从牌库消耗）
  ... 创建 PowerController + EnergyData(power, colors) → handPowers.Add
```

- **手牌满时抽牌仍然消耗牌库**：`Create` 的早退在抽取之后，因此"爆牌"是真实的资源损失（3 张/回合的抽牌里有几张被浪费是可以规划的）。
- `EnergyData.power` 被钳在 `[1, Define.powerLimit=9]`（`EnergyData.cs:29`）；`PowerData.power` 同样（`PowerData.cs:56`）。
- `EnergyData.round` = 创建时的 `BattleManager.round`（`EnergyData.cs:63/74`）。
- 每张手牌对应一个 `PowerController`（UI），`handPowers` 的顺序 = 手牌从左到右的顺序；很多技能对"第 N 张/最后一张/手牌数"敏感（`TA_HandPowerUp`、`TS_LastPowerAllCannon`、`TA_CleanOver`、`TS_Hand4Damage` 等）→ **手牌顺序必须在状态里**。

### 4.3 RNG 细节

见 §9.3。与本 § 直接相关的只有 `"Shuffle_Deck"` 流：
- `InitCore` 消耗 2 次（点数牌库、颜色牌库各 1 次）。
- 每次"抽牌时牌库为空"消耗 1 次（点数与颜色各自独立，共用同一 key 的顺序流）。
- `ShuffleListPowerSystem`（`UtilsClass.cs:575`）算法：对 `i in 0..n-1` 做 `index = Range(0, n); swap(list[index], list[0])`（**不是标准 Fisher-Yates**），因此必须原样复现。

---

## 5. 行能量汇总：unit → line → BattleResultInfo

### 5.1 触发时机

`BattleManager.UpdateInfo()`（L1200）在每帧 `LateUpdate` 被调用，条件为 `!battleOver && inBattle && turnStatus ∉ {BattleEnd, TurnCaculate}`（注意：**`TurnCaculate` 期间不刷新**），流程：

```
CaculateUpdater() (L1176)
  ClearGridPowers()                     // 把所有 BattleResultInfo 的两侧清空
  if (playerFirstCaculateInfo) { CaculatePlayerGridPower(); CaculateEnemyGridPower(); }
  else                         { CaculateEnemyGridPower(); CaculatePlayerGridPower(); }
  RefreshResultPower()                  // 逐行 GetResult + RefreshDamage + 删空行
```
`TurnCaculate` 开头还会再强制调一次 `CaculateUpdaterByTurnEnd()`（L1210 → `UpdateInfo`），保证结算用的是最新盘面。

> 副作用：`RefreshResultPower()`（L1304）会**删除**两侧都为 null 的 `battleResultInfos` 条目；`Dictionary` 的枚举顺序在 .NET 上等于"首次插入顺序"（无删除后重插的语义保证），而 clash 的扣血顺序正是这个枚举顺序 → **需要精确复现插入顺序**（见 §6 与 §11 风险）。

### 5.2 行 → 单位 → 总能量

```
CaculatePlayerGridPower() (L1232)
  for i in 0 .. battlePlayerGrid.GetHeight()-1:
      if GetPlayerLine(i, out line):                                  // line = 船 local y
          SignPlayerBattleResultInfo(i + battlePlayerGridOffest, playerShip, line,
                                     playerShip.GetPowerDataLineContainerByY(line))

GetPlayerLine(i) / GetEnemyLine(i)  (L1334 / L1345)
  worldY = battleXxxGrid.GetWorldY(i)
  shipy  = ship.grid.GetY(worldY + battleXxxGrid.cellSizeCenterOffset.y)   // +0.5
  合法 iff 0 <= shipy < ship.grid.GetHeight()
```

```
PowerDataLineContainer.GetLineTotalPower() (L183)
  total = 0
  shipController.FindShipUnitControllerByLine(y) 得到的单位（shipData.grid 中 coor.y == y 的所有单位）
  对每个 item != null && item.powerable:  total += item.powerDataContainer.TotalPower()
  EventTrigger(getLineTotalPower, totalLine, shipController, y)     // 技能可改写 total
  return total
```

```
PowerDataContainer.TotalPower() (L196)
  // 1) 破防：敌方单位在 breakCount == 0 时输出恒为 0
  if (unit is EnemyShipUnitController)
      if (isPreviewing && breakSystem.breakCountPreview.Value == 0) return 0
      if (breakSystem.breakCount.Value == 0) return 0
  // 2) 单位被禁用（睡眠等）
  if (!shipUnitController.shipUnitActive) return 0
  // 3) 累加
  powerTotal = Σ powerData.power
  powerExtraTotal = Σ powerData.powerExtra
  powerAddedTotal = Σ powerData.powerAdded
  flag = 任一 powerData.slot != null
  if (flag || (unit is PlayerShipUnitController && playerflag && Define.defineValue.autoPower))
      if (Define.defineValue.addedNegaNoSub && playerflag) powerAddedTotal = max(0, powerAddedTotal)
      powerTotal += powerAddedTotal
  num = (powerTotal + powerExtraTotal) * (1 + (GetBuff<Buff_PowerRate>() != null ? 1 : 0))
  if (num < 0) num = 0
  _totalPowerLast = num（预览期走 preview 分支）
  return _totalPowerLast
```

**结论**

- **只有 `powerable == true`（`ShipUnitTag.Cannon`）的单位贡献行能量**（`InitShipUnitSlot`：`shipUnitTag = Type; powerable = (Type == Cannon)`）。支援/设备单位即使装了 power 也不计入 clash（但可触发技能）。
- 单位总能量 = (槽内 power 之和 **+ PowerExtra**) **× (1 或 2，取决于是否有 `Buff_PowerRate`)**，**再加 PowerAdded**（`PowerDataType.PowerAdded`，来自单位属性 `"PA"`、`slotRefitTypes` 的 `AddPowerAdded`、以及各种"加成"类技能）。PowerAdded 只在"该单位至少有一个槽装填（slot != null）"或"玩家侧且 `defineValue.autoPower`"时才计入。
- 破防期间敌方所有单位输出 0（`breakCount == 0`）。

### 5.3 BattleResultInfo 的构造

```
SignPlayerBattleResultInfo(y, ship, line, container) (L1256)
  playerTotal = container?.GetLineTotalPower() ?? 0
  battleResultInfos[y] = (无则新建) → SetPlayerInfo(ship, line, playerTotal, container)

SetPlayerInfo(...) (BattleResultInfo.cs:81)
  this.playerShipController = ship; this.playerLine = line; this.playerTotal = playerTotal
  playerContainer = container
  if (container == null) { playerShipController = null; playerLine = -1; }   // ★ 该行不参与对撞
  else 若 ship.unitSetDataLine 中某单位的 GetBattleGridYByPlayerShipY(coor.y) == y
       → playerContainer = 那个单位注册的容器（★ 伤害倍率可能被替换）
```
敌方同构（`SignEnemyBattleResultInfo` L1272 / `SetEnemyInfo` L105；敌方 `SetEnemyInfo` 里的循环是反编译残留，实际行为 = 不做覆盖）。

- `container == null` 的来源：`ShipController.GetPowerDataLineContainerByY`（L656）只在 `powerDataLineContainers[y].GetCaculateResult()` 全为 true 时返回容器；`Skill_IgnoreDevice` 等通过 `AddCaculateResult` 可以**让某行整行失效（不参与对撞）**。
- 行的 `result` 见 §6.1。

### 5.4 行配对公式【推导】（关键，决定"移动"的意义）

设 `H = BattleManager.gridScale.y`，由**玩家船**高度奇偶决定（`InitBattleGrid` L280-295）：`H = 9`（玩家船高为奇数）或 `H = 8`（偶数）；`W` 同理按玩家船宽度。

由 `GetPlayerLine / GetBattleGridYByPlayerShipY / GetPlayerShipYByBattleGridY`（L1334-1444）与 `GridMap` 的坐标公式可推出（`⌊·⌋` 为整除）：

```
玩家网格 battle row i 对应的玩家 local line:   playerLine(i) = i - ⌊H/2⌋ + centerP.y   (centerP = playerShipData.shipCenter.y)
该行的 key:                                  key = i + offsetP           (offsetP = battlePlayerGridOffest)
⇒ key k 上的玩家 line:                       Lp(k) = k - offsetP - ⌊H/2⌋ + centerP.y

敌方同理:  Le(k) = k - offsetE - ⌊H/2⌋ + centerE.y          (offsetE = battleEnemyGridOffest)

有效条件: 0 <= Lp(k) < playerShip.grid.height, 0 <= Le(k) < enemyShip.grid.height
只有两侧都有效（BattleResultInfo 两侧 controller 都非空）的 key 才结算 clash。
```

**只要两船 `shipCenter.y` 相同**（记为 `C`），就有 `Lp(k) - Le(k) = offsetE - offsetP`：

```
⇒ 玩家的 line L 与敌方的 line L + offsetP - offsetE 对撞
   当 C == ⌊H/2⌋ 时即: Lp(k) = k - offsetP, Le(k) = k - offsetE
```

即：**玩家上移 1 格（`offsetP=+1`），玩家 local line L 改为撞敌方 line L+1；下移则撞 L-1。** 敌方 `Moves.Up → OnMove(1)` 时同理反向。`H` 的奇偶会改变可用行数（8 或 9）并影响 `⌊H/2⌋`，`shipCenter.y != ⌊H/2⌋` 的船（或双方尺寸不同、`shipCenter.y` 不同）必须用上面的通式，不能只记相对平移。

> 复核方式：把 `playerShip.shipData.grid.origionPosition.y`、`battlePlayerGrid.origionPosition.y`、`center.y`、`H` 代入上面的通式，再与 `BattleManager.GetPlayerShipYByBattleGridY(key)` 的输出对比。

---

## 6. 回合结算：伤害 / 护盾 / 破坏 / 胜负

### 6.1 单行结果 `result`（`BattleResultInfo.GetResult` L127）

```
d = playerTotal - enemyTotal
if d > 0:
    mulOnEnemy = 1
    if enemyContainer != null && enemyContainer.GetDamagedMuls().Count > 0:
        mulOnEnemy = enemyContainer.GetDamagedMulsTotal()          // = 1 + Σ 各项
    raw = d * (playerContainer?.damageMul ?? 1) * mulOnEnemy        // 注意 damageMul 默认 1
    if raw < 0: raw = 0
    if enemyContainer 有 GetDamagedReplaces()（"减伤/替换"）:
        result = enemyContainer.GetDamagedTotal(raw)                // 取各替换函数的最小值（首项为基）
    else if playerContainer 有 GetDamageReplaces()（"增伤/替换"）:
        result = playerContainer.GetDamageTotal(raw)                // 取各替换函数的最大值
    else:
        result = raw
elif d < 0:
    mulOnPlayer = 1
    if playerContainer != null && playerContainer.GetDamagedMuls().Count > 0:
        mulOnPlayer = playerContainer.GetDamagedMulsTotal()
    raw = (-d) * mulOnPlayer * (enemyContainer?.damageMul ?? 1)
    if raw < 0: raw = 0
    if playerContainer 有 GetDamagedReplaces():  result = -playerContainer.GetDamagedTotal(raw)
    else if enemyContainer 有 GetDamageReplaces(): result = -enemyContainer.GetDamageTotal(raw)
    else: result = -raw
else: result = 0
```

- `result > 0` = 玩家赢该行（敌方掉血）；`< 0` = 玩家输该行；`0` = 平（`playerTotal/enemyTotal` 都为 0 时该行不产生任何事件）。
- 伤害修正的来源是 `PowerDataLineContainer` 上的四组委托（`Skill_Defence`/`Skill_ColorShield`/`Skill_FatalWeakness`/`TA_DoubleDamage`/`TA_StoneHeart`/`TS_LowDamageProtect`/`Skill_Ghost`/`Skill_StopFight`/`Skill_PanShot`/`Skill_ReactionDefence` …）；**委托闭包在技能对象里，哈希时必须把技能对象的字段一起算**（见 §10）。
- `damageMul` 由技能 set（`PowerDataLineContainer.damageMul`）。

### 6.2 `TurnCaculate` 的核心循环（去掉动画等待后的逻辑等价伪代码）

```
TurnCaculate():                                     // BattleManager.cs:610
  CaculateUpdaterByTurnEnd()                        // 重算 lines（§5.1）
  turnStatus = TurnCaculate
  refreshText / turnCaculate 事件

  playerHitSide = 0; enemyHitSide = 0
  foreach (y, info) in battleResultInfos (按 Dictionary 枚举顺序):
      if (info.playerShipController == null || info.enemyShipController == null) continue
      pLine = GetPlayerShipYByBattleGridY(y); eLine = GetEnemyShipYByBattleGridY(y)

      if (info.result > 0):                         // 玩家赢
          enemyHitSide += enemyCenterY - eLine      // 累加（仅用于死亡动画方向）
          battleResult 事件(info, ResultStatus.win)          // ★ 在伤害之前
          turnCaculate_DamagePre 事件(info.damageMaker)
          dealt = info.damageMaker.Damage(effect:false)     // → enemyShip.TakeDamage(result, checkDead:false)
          turnCaculate_Damaged 事件(info.damageMaker, dealt)
          info.enemyShipController.breakSystem.BreakCount(info.damageMaker)   // 破防 -1（见 §6.4）
          win 事件(info)
      elif (info.result < 0):                       // 玩家输
          playerHitSide += playerCenterY - pLine
          battleResult 事件(info, ResultStatus.lose)           // ★ 在伤害之前
          turnCaculate_DamagePre 事件(info.damageMaker)
          dealt = info.damageMaker.Damage(effect:false)     // → playerShip.TakeDamage(-result, checkDead:false)
          turnCaculate_Damaged 事件(info.damageMaker, dealt)
          lose 事件(info)
      elif (info.playerTotal != 0 || info.enemyTotal != 0):  // 平
          battleResult(equal) / equal 事件(info)
          if (Define.defineValue.equalBreak) info.enemyShipController.breakSystem.BreakCount(info.damageMaker)

  DeadCheck(playerHitSide, enemyHitSide)            // → isDead/battleOver
  TurnCaculateEnd()
```

**伤害落地**（`ShipController.TakeDamage` L260）：

```
TakeDamage(damage, checkDead=true):
  if damage <= 0: 只播放受击效果，返回 0
  if currentShield > 0:
      if (currentShield - damage) > 0:  currentShield -= damage; return   // 全被盾吃
      damage -= currentShield; currentShield = 0                        // 破盾
  if damage > 0:  currentHP = max(0, currentHP - damage)
  if currentHP <= 0: isDead = true
  if checkDead: DeadCheck(0,0) + DeadEndEffect()                        // clash 路径不会走这里
```

- **护盾优先吸收，逐行结算且顺序依赖**：同一回合多行伤害时，前几行可能吃掉护盾，后面的行直接打 HP。**行处理顺序 = `battleResultInfos` 的枚举顺序**（.NET 上为插入顺序；见 §11 风险 R1）。
- 实际扣掉的血量由 `DamageMaker.Damage` 的返回值给出（`-dealt` 显示在飘字）；技能的 `IPowerLoaded`/`IHPDamaged`/`IShieldDamaged`/`IShieldBreak` 等会在过程中修改数值（例如护盾相关 buff 在 `SetCurrentShield_Battle` 内被 `shieldDamaged` 事件改）。

### 6.3 行内事件的技能钩子

结算一行时的触发顺序（`EventName`，参见 §8 的接口表）：

```
battleResult(info, ResultStatus)                  // 先宣告该行胜负（IWin/ILose/IEqual 系 buff 在此生效）
→ turnCaculate_DamagePre(info.damageMaker)        // 可改 damage
→ [TakeDamage 内部] shieldDamaged / shieldBreak / hpDamagePre / hpDamaged
→ turnCaculate_Damaged(info.damageMaker, dealt)   // 实际扣血量
→ （仅赢行）enemyBreakSystem.BreakCount(damageMaker) → breakTrigger/breakTriggered/breakHurt
→ win / lose / equal 事件(info)
```
这些是"伤害前/后修正"的唯一入口。

### 6.4 break（破防）系统（`BreakSystem.cs`）

```
BreakSystem 初始化: breakLimit = enemyShipData.breakLimit; breakCount = breakLimit; breakTurn = false

BreakCount(damageMaker) / BreakCount(int num):         // 只在 num > 0 且 breakCount > 0 且 !breakTurn 时生效
  breakCount = max(0, breakCount - num)
  if (breakCount == 0):
      enemyShipController.AISequence.breakTurn = 1
      breakTurn = true
      breakTrigger / breakTriggered 事件
  breakHurt 事件(enemyShip, num)

BreakRecover():                                        // EnemyTurnPre 调用
  if (breakCount == 0 && AISequence.breakTurn == 0 && !enemyShip.isDead):
      breakTurn = false; breakCount = breakLimit; breakRecover 事件
```

- 每次"赢一行"扣 1 点（`DamageMaker.breakCount` 在 `Refresh` 里恒置 1，`DamageMaker.cs:39`）；平局行只有在 `Define.defineValue.equalBreak` 为真时才扣 1。
- `breakCount == 0` 时敌方**所有单位输出 0**（§5.2），且下个敌方回合 `SequenceStartCo` 走 `breakTurn > 0` 分支（不移动、不装填、不放大招，只推进 process）。
- 时序推演（重要）：
  - 回合 T 的 clash 中打空 breakCount → `AISequence.breakTurn = 1`。
  - 回合 T 的 `EnemyTurnPre`（clash 之后）：`BreakRecover` 因 `AISequence.breakTurn == 1` 不恢复 → 敌方回合空过（`SequenceStartCo` 里 `breakTurn--` → 0）。
  - 回合 T+1 的 clash：敌方总能量全 0 ⇒ 玩家几乎必然全胜所有行。
  - 回合 T+1 的 `EnemyTurnPre`：`breakCount == 0 && AISequence.breakTurn == 0` → `BreakRecover` 恢复满值。
  → **破防收益 = 1 次敌方回合空过 + 下一回合 clash 敌方输出归零**（同一 clash 内已算好的行不受影响，因为 totals 在伤害前算完）。
- 玩家侧没有 BreakSystem（`BreakSystem` 只由 `EnemyShipController.InitController` L26 创建）。

### 6.5 可复算的单回合"伤害收益"估算（给启发式用）

若只想估"这回合能打掉多少血"，可用：

```
for each key where both sides valid:
    pT = Σ_{unit.powerable, unit.coor.y == Lp(key)} unitPower(unit)
    eT = Σ_{unit.powerable, unit.coor.y == Le(key)} unitPower(unit)      // breakCount==0 → 0
    d  = pT - eT
    dmg = |d| 经 §6.1 的倍率/替换修正
敌方总伤害 = Σ_{d<0} dmg，玩家总伤害 = Σ_{d>0} dmg（盾/HP 分解见 §6.2）
```
注意这只在"没有跨行/打破产技能、没有伤害事件修正"时精确；有技能时必须走引擎。

### 6.6 初始数值从哪来（构造初始状态的清单）

| 数值 | 来源 |
|---|---|
| 玩家 `maxHP` / `currentHP` 初值 | `DataShip.HP`（`PlayerShipData.ReadFromCSV` L164-165） |
| 玩家 `maxMove` / `currentMove` 初值 | `DataShip.Move`（L166-167） |
| 玩家 `maxShield` | **船技能** `SH_Shield.OnProcessInited`（`SH_Shield.cs:7-11`）：`maxShield = currentShield = args[0]`；`args` 来自 `DataShip.Args`。没有该技能的船 `maxShield = 0` |
| 玩家 `maxExchange` | 同理，`SH_Exchange.OnProcessInited`（`SH_Exchange.cs:7`） |
| 战斗开始时的归一化 | `ShipBaseController.InitController` → `shipData.InitOnBattle()`（`ShipData.cs:104`）：`currentShield = maxShield`、`currentExchange = maxExchange`、`colorSlotListBattle = colorSlotList`（**注意：HP 不在此重置——玩家 HP 在同一局 run 内跨战斗延续**） |
| 敌方 `maxHP` | `DataEnemyShip.HP`（`processAdvance.advance_hp` 时取 `HP_1`）、`currentHP = maxHP`（`EnemyShipData.cs:55-73`）；`currentShield = maxShield`（默认 0） |
| 敌方 `breakLimit` | `DataEnemyShip.BreakCount`（`advance_break` 时取 `BreakCount_1`，`EnemyShipData.cs:60-64`） |
| 玩家牌库 | `DataShip.PowerCoreWeight[]` / `PowerColorWeight[]` → `PlayerShipData.powerWeights / powerColorWeights`（`InitCore`，L190-221） |
| 单位槽颜色 | `DataShipUnit.PowerSlot[]` + `slotRefitTypes`（`UnitData.GetSlotsRares`，`UnitData.cs:581`） |
| 单位技能/属性 | `DataShipUnit.SkillPath` / `Propertys`（`PA/US/TI/CH/CD/OC/SU/...`）/ `Type` / `Args` / `EquiptLimit` |
| 敌方 AI 数据 | `EnemyShipAsset`（`moves` / `notices` / `controller`）+ 每格 `EnemyShipCellAsset.AIPowers/AIOrder/loadPreview` |

如需完全从存档之外的"数据层"构造状态，这些就是唯一的初始值来源（现有无头引擎走的是"存档 + 游戏自己的 SaveManager.Load"路径，天然一致）。

---

## 7. 敌方 AI（确定性来源）

### 7.1 数据来源

- `EnemyShipData.ReadFromCSV`（`EnemyShipData.cs:20`）：从导出的资源 `EnemyShipAsset`（`FilePath.enemyScriptPath + dataShip.ShipDataPath`）加载：
  - `controller`：`AIController` 子类名（`AIC_Math` 等，可空），仅用于**演出/事件**，不参与决策；
  - `moves : Moves[]`（`Moves.Null/Up/Down`）、`notices : bool[]`；
  - 每个格子的 `EnemyShipCellAsset`：`AIPowers : List<AIPower>`、`AIOrder : int`、`loadPreview : bool`。
- 单位创建时 `EnemyShipUnitController.InitShipUnit`（L9）把该格的 `AIProcess(AIPowers, AIOrder, loadPreview)` 注册进 `AISequence.sequnceChache`。

### 7.2 每回合决策流程（`AISequence.SequenceStartCo` L42）

```
SequenceStartCo():
  if (breakTurn <= 0) 显示"敌方回合"面板
  wait 1s
  if (sequnceChache.Count > 0): 合并进 sequnce; sequnce = sequnce.OrderBy(AIOrder)   // OrderBy 稳定排序
  if (sequnceNotices.notices.Count > 0):
      idx = sequnceNotices.process % count
      if (notices[idx]) 显示警告（纯视觉）
      sequnceNotices.process++

  if (breakTurn > 0):                        // 被破防
      breakTurn--
      sequnceMoves.process++
      foreach (unit, proc) in sequnce:
          proc.process++
          if (proc.loadPreview && proc.AIPowers.Count > 0
              && proc.AIPowers[proc.process % proc.AIPowers.Count].aiPowerDatas.Count > 0)
              unit.AddBuff<Buff_ReadyToLoad>()         // 仅视觉提示
  else:
      if (sequnceMoves.moves.Count > 0):                 // ★ 先移动
          idx = sequnceMoves.process % moves.Count
          Moves.Up  → shipController.OnMove(1)
          Moves.Down→ shipController.OnMove(-1)
          sequnceMoves.process++
      foreach (unit, proc) in sequnce (按 AIOrder):
          if (unit.shipUnitActive) yield LoadToShipUnit(unit, proc)
          proc.process++
          if (proc.loadPreview && AIPowers[proc.process % count].aiPowerDatas.Count > 0)
              unit.AddBuff<Buff_ReadyToLoad>()
  SequenceEndCo()

LoadToShipUnit(unit, proc):                  // AISequence.cs:123
  order = proc.process % proc.AIPowers.Count
  foreach apd in proc.AIPowers[order].aiPowerDatas:
      if (apd.power > 0):
          unit.LoadEnergy(new EnergyData(apd.power, apd.powerColor))   // 结果忽略（可能失败）
  if (proc.AIPowers[order].useSkill):
      unit.shipUnitSkillActiveHandler.Click()                        // 走 §3.4 的 Clicked
  if (proc.AIPowers[order].loadCallback != null): proc.AIPowers[order].loadCallback()
```

### 7.3 确定性结论

- **AI 决策完全不使用随机数**：全部是 `process` 计数器对数据列表取模 + `AIOrder` 稳定排序 + 数据内容。给定状态（各 process、各 unit 的 active/destroyed、AI 数据）即可完全预测。
- **没有"AI 评估/搜索"**：`AIController` 及其 `AIC_*` 子类只做演出、事件响应（`AIController.cs` 全文无决策代码）。
- 影响 AI 的非数据因素只有：单位是否 `shipUnitActive`、是否被 `ReplaceUnit` 换了 `AIProcess`（`EnemyShipController.ReplaceUnit` L54 可传入新 `AIProcess`，否则用 `new AIProcess(null,0,false)`）、`breakTurn`、`sequnce` 的插入序（`Dictionary` 插入序 + 稳定排序）、`sequnceChache` 合并顺序。
- 敌方装填同样受颜色/槽/disturbed 规则约束（`LoadEnergy` 是共享实现），失败则这次装填**静默丢弃**（数据里写的 power 就浪费了）。
- 敌方也走 `Buff_Use/Buff_Times` 的可用性判定（`Clicked()` 里对 `isPlayer == false` 不检查回合状态，但 `buff.useable`/`CheckClickable` 仍然生效——`Buff_Use.count` 的 `IPlayerTurnInit` 对敌方绑定的是 `enemyTurnInit`）。

### 7.4 敌方回合能做的操作（等价动作空间）

- 移动 1 格（若 `moves` 非空）；
- 每单位按 AIPowers 循环装填 0..N 张（每张独立 `LoadEnergy`）；
- 每单位按 `useSkill` 触发一次技能（`Clicked`）；
- 数据驱动的 `loadCallback`（例如 `AIController.ReplaceNextAIPowers/InsertNextAIPowers` 的临时改写）。

---

## 8. Buff / 技能触发体系

### 8.1 事件总线

`EventCenter`（`EventCenter.cs`）用 `Dictionary<string, EventInfo>` + `Delegate.Combine` 实现多播，**回调顺序 = 注册顺序**。技能/buff 在 `BattleSkill.InitBattleSkill()`（`BattleSkill.cs:264-705`）里按"自己实现了哪些接口"注册监听；因此**同一事件的处理顺序取决于对象创建顺序**（船 → 单位按 `shipUnitControllers` 顺序 → 每个单位 skill → buffs → 宝物/天赋 → 战斗事件）。

### 8.2 主要接口 → 事件映射（`isPlayer` 会把玩家/敌方回合事件镜像）

| 接口 | 触发事件 | 语义 |
|---|---|---|
| `IPlayerTurnInit` | `playerTurnInit`（敌方侧技能听 `enemyTurnInit`） | 回合初始化（抽牌前） |
| `IPlayerTurnPre` / `IPlayerTurnPreAfter` | `playerTurnPre` / `playerTurnPreAfter` | 玩家回合前 |
| `ITurnCount` | `playerTurnCount`（敌方 `enemyTurnCount`） | 每回合计数（`MoveReset`、`Buff_Times.countLimit=0`） |
| `IPlayerTurnStart` / `IPlayerTurn` / `IPlayerTurnAfter` / `IPlayerTurnAfterOver` | `playerTurnStart` / `playerTurn` / `playerTurnAfter` / `playerTurnAfterOver` | 玩家回合正式开始 |
| `IPlayerTurnEndBefore` / `IPlayerTurnEnd` / `IPlayerTurnEndAfter` | `playerTurnEndBefore` / `playerTurnEnd` / `playerTurnEndAfter` | 回合结束 |
| `IEnemyTurnPre/PreAfter/Start/Turn/After/AfterOver/EndBefore/End/EndAfter` | 对应 `enemyTurn*`（玩家侧技能听 `playerTurn*`） | 敌方回合（镜像） |
| `ITurnCaculatePre` / `ITurnCaculate` / `ITurnCaculateEnd` | `turnCaculatePre` / `turnCaculate` / `turnCaculateEnd` | 结算 |
| `IClearPower` | `clearLoad` | **每回合清空 power 时的重置点（最重要）** |
| `IClearLoadEnd` | `clearLoadEnd` | 清空结束 |
| `ILoadPower*` / `IPowerLoaded*` / `IPowerLoading*` / `IPowerToContainer` | `loadPower*` / `powerLoaded*` 等 | 装填流水线（`loadPower` 是"成功进容器"） |
| `ITryLoadPower` | `tryLoadPower` / `tryLoadPowerAfter` | **装填前否决/改写**（`loadable=false` 即失败） |
| `IPowerChange` / `IPowerPointUp` / `IPowerColorUp/Down/Add` | `updatePowerShow` / `powerPointUp` … | 数值变动 |
| `ITurnCaculate_DamagePre` / `ITurnCaculate_Damaged` | `turnCaculate_DamagePre` / `turnCaculate_Damaged` | 单行伤害前/后 |
| `IWin` / `ILose` / `IEqual` / `IWinListener`/`ILoseListener`/`IEqualListener` | `win` / `lose` / `equal`（+`battleResult`） | 单行胜负 |
| `IBreak` / `IBreaked` | `breakTrigger` / `breakTriggered` | 破防 |
| `IShieldHealed` / `IShieldDamaged` / `IShieldBreak` / `IHPDamagePre` / `IHPDamaged` | `shield*` / `hpDamage*` | 数值修正 |
| `IUse` / `ITimes` / `IClick` | 点击/次数（见 §3.4） | 主动技能 |
| `IExchange` / `IExchangePre` | `exchange` / `exchangePre` | 交换发生 |
| `IDestroyUnit*` / `ICreatePower*` / `ILoadFull` / `ICharge*` / `ISum*` / `ITriangle` / `ITriColor` / `ICountDown` / `IOverclock*` / `IDisturb*` / `ISleep`/`IWake` / `IPreviewStart/End` / `IBattleStart*` / `IBattleEnd*` / `IClearPower` … | 同名事件 | 各种机制钩子 |

### 8.3 buff 体系

- `Buff : BattleSkill`（`Buff.cs:6`）：核心是 `count` / `countLimit`（`DynamicValue<int>`，带 `callback`/`setLimitCallback`）+ `resultCheck/resultActionDic`（`IWin/ILose/IEqual` 用）。`Init` 被 `AddBuff` 调用；同一类型重复 `AddBuff<T>()` 会复用已有实例（`ShipUnitController.cs:1086`）。
- `BattleSkill.once/oncePreview`（`BattleSkill.cs:19/22`）默认 true，被 `Buff_Use.OnTurnCount` 等当作"是否生效"开关。
- 与数值直接相关的 buff：
  - `Buff_PowerAdded`（`Buff_PowerAdded.cs`）：不直接改数值，只是把 `powerDataContainer.GetUnitPowerAddedTotal()` 同步到 `count` 并抛 `powerAddedChange`，用于 UI/技能联动。真正计入能量的是 `PowerData(powerDataType=PowerAdded)` 本身（§5.2）。
  - `Buff_PowerRate`（`Buff_PowerRate.cs`）：**只要该单位有它，`TotalPower` 的 `(powerTotal + powerExtra) × 2`**；它靠 `sources` 列表存活，`IClearPower→OnClearPower` 会移除来源（`sources.Remove(this)`），来源清空即自动移除。多个 Rate 不叠乘，只是 2x。
  - `Buff_ReadyToLoad`：纯视觉（`Buff_ReadyToLoad.cs`）。
- 单位属性 `"PA"` = 常驻 PowerAdded（`ShipUnitSkill.OnInitProperty` L60-96：`GetProperty("PA")` → `LoadPower(new PowerData(PowerDataType.PowerAdded, property))`；`slotRefitTypes` 里的 `AddPowerAdded` 也会加）。
- 其它 buff 家族（`Buff_Charge/Overclock/Sum/Triangle/TriColor/CountDown/Sleep/Grip/Taunt/Burn/...`）不直接进 `TotalPower`，而是作为技能可用性/触发条件的计数载体 → **它们的 `count` 必须进哈希**。
- 技能体系：`ShipUnitSkill`（单位技能，`Skill_*`）、`ShipSkill`（船技能，`SH_*`：`SH_Shield` 设 `maxShield/currentShield = args[0]`、`SH_Exchange`、`SH_Paint`）、`TalentSkill`（`TA_*`，驾驶员天赋）、宝物技能（`TS_*`，210 个文件）、`BattleEventSkill`（`BE_*`，战斗事件）。它们都通过上面的接口介入。

---

## 9. 状态字段清单（状态哈希 / 去重）

### 9.1 必选：决定"下一动作后结果"的字段

> 分层给出。**H1 层**足够判定"下一回合 clash 结果与胜负"；**H2 层**才能保证"复制状态后继续跑引擎会得到完全一致的历史"。求解器状态哈希建议用 H1 + H2 中与当前盘面有关的项（技能/天赋/宝物是否参与战斗而定）。

**H0：战斗全局**

| 字段 | 位置 | 说明 |
|---|---|---|
| `round` | `BattleManager.round_` | 1 起 |
| `turnStatus` | `BattleManager.turnStatus` | 判定合法性 |
| `battleOver`, `inBattle` | `BattleManager` | |
| `turnCaculateCount` | `BattleManager` | 结算次数（列冲撞相关） |
| `battlePlayerGridOffest`, `battleEnemyGridOffest` | `BattleManager` | 本回合移动偏移（回合末清零） |
| `pendingColumnClashes`(队列内容)、`columnClashPlayerY`、`columnClashesRunning`、`endTurnAfterColumnClashes` | `BattleManager` | 列冲撞 |
| `playerFirstCaculateInfo` | `BattleManager` | 只影响计算/插入顺序 |
| `gridScale`(由玩家船尺寸决定)、`battlePlayerGrid/battleEnemyGrid.origionPosition` | `BattleManager` | 坐标基准 |
| `turnStartMoveCount` | `BattleManager` | 小 |

**H1：结算相关盘面**

| 字段 | 位置 |
|---|---|
| `playerShip.shipData`：`currentHP/maxHP`、`currentShield/maxShield`、`currentMove/maxMove`、`currentExchange/maxExchange`、`colorSlotListBattle` | `ShipData` |
| `playerShip.isDead`、`enemyShip.isDead` | `ShipBaseController` |
| 双方每条 local y 的容器：`damageMul`、`damagedMuls.Count`、`damagedReplaces.Count`、`damageReplaces.Count`、`resultCaculates.Count`（**委托指向的技能状态另算**） | `PowerDataLineContainer` |
| 每个单位的：`coor`、`powerable`、`shipUnitTag`、`shipUnitActive`、`destroyed/destroying` | `ShipUnitController` |
| 每个槽的：`rare_`(rare)、`powerData_power`、`powerData_DataType`、`emptyDatas`(每项的 power/powerExtra/powerAdded/类型)、`isDisturbed` | `ShipUnitSlot` / `PowerData` |
| 每个单位 `powerDataContainer.powerDatas` 的逐项：`_power`、`_powerExtra`、`_powerAdded`、`powerDataType`（决定是否计入/是否 Buff_PowerAdded）、`slot != null`、`from` 是否非空、`from.powerController` 是否为 null（区分"手牌来源"与"技能/AI 来源"）、`isClearBySlot`、`endTurn` | `PowerDataContainer` |
| 每个单位的 buff 集合：类的多重集 + 每个 buff 的 `count`/`countLimit`（`Buff_PowerRate` 存在性、`Buff_Use.count`、`Buff_Times.count/countLimit`、`Buff_Charge`、`Buff_Overclock`、`Buff_Sum`、`Buff_Triangle`、`Buff_TriColor`、`Buff_CountDown`、`Buff_Sleep` …） | `ShipUnitController.buffs` |
| 每个单位 `shipUnitTag`/`unitData.id,lv`/`slotRefitTypes`/`props`（决定槽颜色、PowerAdded、技能） | `UnitData` |
| `unitSlots` 的**顺序**（装填优先级） | `ShipUnitController.unitSlots` |
| 敌：`breakSystem.breakCount`、`breakLimit`、`breakTurn`、`AISequence.breakTurn` | `BreakSystem` / `AISequence` |
| 敌：每个单位的 `AIProcess.process`、`AIOrder`、`loadPreview`、`AIPowers`(数据，恒定)、`sequnceMoves.process`、`sequnceNotices.process`、`sequnce` 顺序 | `AISequence` |
| 手牌 `handPowers`：顺序 + 每张 `_power` + `powerColors`(排序后的颜色集) | `PowerManager` |
| 牌库：`powerCore.powerWeights`（有序 list）、`powerCore.powerWeightsCD`、`powerColorCore.powerColorWeights`、`...CD` | `PowerCore`/`PowerColorCore` |
| `powerCore.powerWeightsRemovePreview`、`powerColorCore...RemovePreview`（预览残留，正常应为空） | 同上 |
| `PowerManager.powerCountLimit`(10)、`powerInitNum`、`powerNumEveryTurn` | `PowerManager` |
| `Define.defineValue`(战斗相关项)：`moveCount`、`powerInitNum`、`powerNumEveryTurn`、`equalBreak`、`autoPower`、`addedNegaNoSub`、`timesPerTurnLimit`、`loseTwice`、`chargeNoWast`、`overclockPurple`、`noColorNotice`、`sumCareless`、`battleToFirstRoundTrigger`、`noLimitEquip` … | `Define.defineValue` |
| `Define.powerLimit`(9) | 常量 |
| 玩家船技能/天赋/宝物/战斗事件是否已注册 | 通过 `playerShip.shipData.shipSkill`、`ShipDatasManager.currentPilotData.talentSkills`、`ItemManager.treasures`、`BattleManager.eventSkill` |

**H2：技能/buff 内部状态（保证"继续跑引擎"完全一致）**

| 字段 | 说明 |
|---|---|
| 每个 `ShipUnitSkill` 的全部普通字段（`args`、`dynamicValues`、`loaded` 之类的 `[CopyThis]` 字段、私有计数器） | 反编译里每个 `Skill_*` 私有字段都可能是状态（如 `Skill_ExchangeSlotTarget.datas_1/datas_2`、`Skill_Final` 计数） |
| 每个 `Buff` 的 `count/countLimit` + 子类私有字段（如 `Buff_PowerRate.sources`、`Buff_Sum` 的内部 `buff_Sums`、`Buff_Overclock` 的 `buff_Overclocks`） | |
| `ShipUnitSkillActiveHandler.buff`、`PowerController` 上的 `locked/destroying/hideNum` | 主要是 UI，但 `locked` 会影响可点击 |
| `ShipController.unitSetDataLine` 的键集合 | 影响 `playerContainer` 覆盖 |
| `PowerData.powerFunc/powerAddedFunc/powerExtraFunc` 是否非空（委托到技能时 power 值不是常量） | `PowerData.cs:40/65/84` |
| `BattleChoiceGroup.groups`（若出现） | 待选事件选项 |

**不要进哈希（UI/预览/时序）**

- 所有 `*Preview` 字段（`rarePreview`、`powerDataPreview`、`emptyDatasPreview`、`isDisturbedPreview`、`powerDatasPreview*`、`breakCountPreview`、`staticDamage*Preview`、`previewCurrent*`、`previewColorSlotList`、`buffAddPreviews/buffRemovePreviews`）。
- `isPreviewing` / `isDragging` / `isExchanging` / `dragStatus` / `PowerController.lastLoad` / `lastSelect` / `ShipUnitVisual.targetShipCell` / tween 位置。
- 反直觉但重要：`isPreviewing` 虽然"只是 UI"，但它会改变 `ShipUnitSlot.rare`、`Buff_Click.useable`、`PowerDataContainer.TotalPower` 的返回值 → **哈希时应当把它归一化为 false**（并断言运行时它确实是 false，否则说明有预览残留，状态已污染）。
- `Dump.Fields`（`src/SolverLogger/Dump.cs`）已按名字过滤 `preview` 字段，可直接复用来生成 H2 的"技能/buff 字段"部分。

**RNG 状态（重放用）**

| 字段 | 说明 |
|---|---|
| `WantedProcess.game_seed`、`world_seed`、`seeded_run` | 种子 |
| `wantedPhases.currentSmallPhase / currentBigPhase`、`onBossSuccessive`、`HistoryBossFightProcess.current` | 决定流名的阶段串 |
| `BattleTempData.battle_states : Dictionary<string,int>` | **战斗内**每个 key 的计数器（TempSave 时重置为空） |
| `WantedProcess.state_dic : Dictionary<string,int>` | 非战斗期的计数器 |
| 各 key → 已消耗次数（即上面两个字典的全部内容） | 每次 `UtilsClass.RandomRange` 都按 key 重播一次 Xorshift128 → 纯函数 |

### 9.2 建议的哈希规范化

1. 所有 `List` 按原顺序；所有 `Dictionary` 按**插入顺序**（不是排序！`AISequence.sequnce`、`battleResultInfos` 的插入顺序都有语义）。若用排序后的哈希，必须另存一个"顺序签名"。
2. `PowerColors.colorList` 是降序集合（`RefreshSort`），可直接当作有序 tuple 序列化。
3. 委托（`damagedMuls` 等）不可哈希 → 用"技能类的多重集 + 其字段"替代：容器哈希 = (类型名, damageMul, damagedMuls.Count, damagedReplaces.Count, damageReplaces.Count, resultCaculates.Count) + 单位/H 层里对应技能的状态。
4. 数值钳制已在 setter 中，读 `DynamicValue.Value` 即可（不要读私有 backing field）。

### 9.3 战斗内会用到随机数的完整清单（`UtilsClass.RandomRange` 的 key）

| key | 出现位置 | 何时消耗 |
|---|---|---|
| `"Shuffle_Deck"` | `ShuffleListPowerSystem`（`PowerCore.RanmdomOrder`/`PowerColorCore.RanmdomOrder`/抽牌时洗 CD） | 开局 2 次；之后每次某牌库抽空时各 1 次 |
| `"key_PowerCore"` | `PowerCore.AddWeight`（向点数牌库插入新权重的随机位置） | 技能/事件加点数时 |
| `"key_PowerColorCore"` | `PowerColorCore.AddWeight` | 技能/事件加颜色时 |
| `"key_TA_LuckyEnergy"` | `TA_LuckyEnergy.OnPowerLoaded`（按百分比额外生 1 张） | 每次成功装填（若装了该天赋） |
| `"key_Skill_Final"` | `Skill_Final`（随机分支） | 该技能触发时 |
| `"key_Skill_TempAdded"` | `Skill_TempAdded`（随机加值） | 该技能触发时 |
| `"key_Skill_KingsTreasures"` | `Skill_KingsTreasures` | 该技能触发时 |
| `"key_BE_FreeMoveStrom"` / `"key_BE_SpaceChaos"` | 战斗事件技能 | 事件触发时 |
| 其它 `"key_" + 技能类名` | 各技能内的随机（`Skill_*`/`TS_*`/`TA_*` 内 `UtilsClass.RandomRange(..., "key_" + GetType().Name)`） | 对应技能触发时 |

（`OC_*`/`poll_*` 属于局外流程，战斗内不会消耗。）

- `UtilsClass.RandomRange`（`UtilsClass.cs:433/446`）的语义：**用 `GetStateByKey(key)` 作为种子重置 `UnityEngine.Random`，取一次值，然后把该 key 的计数器 +1**。所以"随机"完全是 (key, 该 key 历史调用次数) 的纯函数。
- `GetStateByKey`（`WantedProcess.cs:147`）：
  - 战斗外/无 `battle_states`：`"MathTide_Normal_" + key + "_" + game_seed` 的 `string.GetHashCode()`；
  - 战斗内（`inBattle && BattleTempData.battle_states != null`）：首次用 `"MathTide_Battle_" + key + "_" + game_seed + "_" + (smallPhase + 100*bigPhase)`；Boss 连战用 `"MathTide_Boss_" + key + "_" + game_seed + "_" + HistoryBossFightProcess.current`；之后每次 +1。
  - 反编译工程的 `src/Lonestar.Game/Rewritten/Assembly-CSharp/WantedProcess.cs:172/192` 把 `string.GetHashCode()` 换成了 `Lonestar.Rng.MonoHash.Hash`（Mono 的确定性哈希），**这是复现 RNG 的前提**（.NET 的字符串哈希是随机化的）。
- 无种子的 `UtilsClass.SystemRandom`（`System.Random`）只用于视觉偏移/飘字/随机语音（`ShipBaseController.GetCenterByRandomRange`、`DamageMaker.DamagePosRandom`、`AIController.Yell` 等），逻辑不依赖【待验证：可全仓库确认无逻辑分支依赖 `SystemRandom` 的返回值】。
- `UnityEngine.Random` 在无头环境由 `Lonestar.Rng.UnityRandom`（Xorshift128，`InitState(seed)` = S0=seed，S1=S0*1812433253+1，S2/S3 递推）实现，`Range(int,int)` = `NextUInt() % (max-min)`，`Range(float,float)` 的浮点转换见 `UnityRandom.ToFloat`（`Mantissa23Div`）。

---

## 10. 对求解器最重要的结论（动作空间与状态压缩）

1. **动作空间 = (手牌 × 可装填单位) ∪ 移动 ±1 ∪ 交换格对 ∪ (可点击单位技能 × 选靶) ∪ EndTurn**，且每个动作的合法性判定在本文 §3 已给全；其中"装填"的合法性完全由 `unitSlots` 顺序 + 颜色阈值 + `isDisturbed` + `loadable` 决定，是主要的分支来源。
2. **一个"玩家回合"是局部回合制**：抽牌固定、移动/交换/装填/技能共享资源（`currentMove`、`currentExchange`、`Buff_Use/Times` 次数），只有 `EndTurn` 才触发 clash。搜索树的分支因子主要在"装填顺序"上——但因为装填只是"填第一个可用槽"，**同一张牌装到不同单位/不同槽的顺序会产生不同结果**（PowerAdded/Rate/技能触发），需要保留顺序信息。
3. **最重要 3 个可利用点**：
   - **RNG 极稀疏且可解析**：战斗内只有 `Shuffle_Deck`（洗牌）等少数 key，且每次调用是"种子哈希 + 计数器"的纯函数 ⇒ 可以直接把 RNG 从搜索中"冻结成一个确定序列"，或用 `(牌库内容, 颜色库内容, 已消耗计数)` 精确预测未来抽牌，无需模拟随机。
   - **敌方完全确定性**：敌方每回合行为 = 数据表 + 少量 process 计数器（§7）⇒ 可以把敌方回合整体折叠成"确定的状态转移函数"，用于 2-ply/对抗式评估，甚至预计算敌方未来 K 回合。
   - **行配对是纯几何**（§5.4）：clash 结果 = 玩家各 line 能量 vs 敌方各 line 能量，移动只是把 line 索引整体平移 ⇒ 状态可压缩为"每 line 能量 + 偏移 + 敌方每 line 能量"，使得剪枝/上界估计（例如"本回合最多能打掉多少血"）有闭式近似。
4. **最重要 3 个难点**：
   - **`clearLoad` 的每回合重置语义**：所有槽 power 清零、大量技能/buff 的 per-turn 计数归零；很多"这一回合看起来能打出的能量"必须叠加"哪些是持久 power（`PowerData` 不在槽里、`isClearBySlot=false`、`PowerAdded`）"。
   - **行结算顺序**：护盾吸收使"先打哪一行"影响实际掉血；顺序来自 `Dictionary` 插入顺序（§6.2/§11-R1），任何自己重写的快速模型都必须复现这个顺序，否则会出现"同样的盘面，不同的血量"。
   - **技能生态的规模**（~1000 个 `Skill_*`/`TS_*`/`TA_*`/`BE_*` 类，全靠事件总线介入）：状态增量分散在技能对象私有字段里，无法用少量统一字段描述；正确做法是**在引擎内推进 + 复用 `Dump.Fields` 的反射快照做哈希**，而不是手写第二套规则。

---

## 11. 风险与【待验证】清单

- **R1. 结算顺序依赖 `Dictionary` 枚举顺序**：`battleResultInfos` 的插入顺序由 `CaculatePlayerGridPower/CaculateEnemyGridPower` 的循环顺序与 `RefreshResultPower` 的删除/重插决定；clash 扣血顺序 = 该枚举顺序，护盾存在时影响最终 HP。**在 .NET 上它是插入序，但一旦发生删除-重插，顺序会变；必须原样复现（用引擎跑或用相同的插入/删除序列）。**【待验证：用录制构造"多行 + 有盾"的关键局面，比对不同遍历顺序的掉血】
- **R2. disturbed 粘性**（§3.1）：`loadInfo.disturbPass` 跨槽共享且只置 true。需要用 `Skill_Disturb*` 造出 disturbed 盘面验证。
- **R3. 手牌满时抽牌仍消耗牌库**（§4.2）：需要实测确认（例如 9 张手牌时抽 3 张）。代码路径明确，风险低。
- **R4. `Buff_Use.count` 的"冷却"语义**（§3.4）：`US` 属性到底是"冷却回合"还是"可用次数"取决于数据；代码行为是"每回合 -1，归 0 可点，点后回到 US"。需用某个 `IUse` 单位实测。
- **R5. `Buff_Click == null` 的空引用**：`ShipUnitSkill.Clicked()`（L481）、`PowerActionCheck/DragBeginActionExchange` 里 `GetBuff<Buff_Click>().isPlayer` 都不判空。对"不是 IUse/ITimes 的单位"调用这些入口会抛 `NullReferenceException`。枚举"可点击单位"时必须先 `GetBuff<Buff_Click>() != null`（或直接用 `skill.Clickable()`）。
- **R6. `emptySlot` / `emptyPreCount + 50` 分支**：只由少数技能触发，正常盘面不触发；若求解器要覆盖这些单位需单独验证。
- **R7. 列冲撞对动作空间的打断**：装填可能触发中途 clash（`turnStatus` 短暂离开 `PlayerTurn`），且 `EndTurn` 会被延期（`endTurnAfterColumnClashes`）。以"跑到静止"的步进方式可自动处理，但不能假设"动作后仍是 PlayerTurn"。
- **R8. 交换的无头入口**（§3.3）：`TeleportEnd` 依赖鼠标悬停位置（`enterCell`），录像回放需绕开；推荐的 `StartTeleport→TeleportPreview→DoTeleportAtEnd` 需要验证事件序列（`exchangePre/exchange`）与 UI 路径一致。
- **R9. `BattleChoice`（A7）**：出现在战斗中时对回合流程的影响未验证（是否阻塞 EndTurn）。
- **R10. 敌方 `surrender`**：数据驱动的一开局胜利；求解器需要在"进入战斗"时就检查，避免把必赢局面当普通战斗搜索。
- **R11. 预览态泄漏**：`isPreviewing` 会改变 `TotalPower`/`rare`/`useable` 的取值。无头调用入口必须避免 `*Preview` 路径，或在动作后断言 `!isPreviewing`。
- **R12. `RestThePostion` 之后 `battleResultInfos` 被清空、偏移归零**：如果求解器要在回合末保存"快照"，必须等到 `PlayerTurn` 稳定后再取，否则会拿到结算期/复位期的中间态。
- **R13. `PowerDataContainer.TotalPower` 的 `_totalPowerLast` 缓存 / `PowerData` 的函数型字段（`powerFunc` 等）**：委托存在时 `power` 不是常量，状态哈希必须包含委托持有者的字段。

---

## 12. 附：现有录制/回放框架的接口对照（便于扩展动作空间）

| 录制文件字段 | 回放实现 | 需要补的入口 |
|---|---|---|
| `act_load { hand_index, target, energy, by }` | `handPowers[hand_index].powerController.PowerLoad(unit)`（`ReplayDriver.cs:84`） | ✅ 已有 |
| `act_move { y, ship_is_player, before, after }` | `ship.shipVisual.barPanel.shipMoveCell.OnMove(y)`（L91） | ✅ 已有 |
| `act_end_turn { status, by }` | `bm.EndTurn()`（L102） | ✅ 已有 |
| `act_click { unit, skill, two_step }` | — | `unit.shipUnitSkillActiveHandler.Click()` |
| `act_target { kind, target, options }` | — | 二段选靶的回调（`ShipDirPanel.Init` 的 action） |
| `act_exchange { start_coor, start_cell }` | — | `StartTeleport / TeleportPreview / DoTeleportAtEnd` + `SetExchange_Battle(-1)` |
| `act_choice { option, fields, check }` | — | `BattleChoiceButton.Click()` |
| `battle_retry` | `BattleTempData.TempLoad()` 由游戏自身触发 | — |
| `snap`（`Snapshot.Write`） | 全状态快照（`src/SolverLogger/Snapshot.cs`），含 `battleResultInfos` 全字段、单位槽/power/buff/skill 字段、AI 游标、手牌、双牌库 | 可直接作为"状态哈希"的参考实现 |

`Snapshot.Write` 里已经包含的字段就是本节 §9.1 的 H0+H1 的超集（外加 `Dump.Fields` 的 H2 部分），建议求解器直接复用其序列化逻辑作为哈希输入，并额外补上：`handPowers` 顺序（已在 `Snap.Hand`）、`powerCore/colorCore` 双队列（已在 `Snap.Deck`）、`Define.defineValue` 的战斗相关项、`breakCount/breakTurn`（已在 `Ship()` 的 `break`）、以及本文 §9.3 的 RNG 计数器。
