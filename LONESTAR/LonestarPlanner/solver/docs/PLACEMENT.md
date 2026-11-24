# LONESTAR 战前物品摆放 / 船体与单位数据模型分析

> 任务：task-2（T2）。作者：placement。数据来源：`data/export/csv/`、`data/export/scriptableassets/`、
> `decompiled/Assembly-CSharp/`，以及 `SolverLogger` 录制 `*.jsonl` 的 `battle_begin` 记录。
> 统计脚本：`scripts/analysis/shipunit_stats.py`（只读 data/，可重复运行，数字即本文档引用值）。
> 结论均来自反编译代码与实际导出数据；需要游戏内实测才能确认的点见 §6.4。

---

## 0. 关键结论（先看这里）

1. **玩家确实可以在战前自由摆放单位到船体格子上**，不是在战斗里“抽到再放”。
   战前流程是：`ShipSelectPanel`（选船/选飞行员/难度/种子）→ `WantedPanel`（看本轮通缉目标）→
   **整备（Refit）界面**：`ShipRefitController` 生成可拖拽的船体 + `InventoryPanel`（背包）→
   鼠标/手柄/触屏**拖拽**单位在格间移动、交换、装/卸。底层调用链见 §1.2。
2. **摆放是“免费且可反复”的**：战前把单位从背包拖到格子、格子之间互换、拖回背包，都不消耗金币/次数，只受
   **船体格子可用性**、**LoadLimit（装载上限）**、单位**占用值 EquiptLimit** 约束。没有“每回合只能摆一个”的限制。
3. 船体格**不是任意大小**：由 `PlayerShipAsset` 的 `coors` 列表 + 每格 `unitID` 决定；`unitID==-1` 的格子
   `ShipCellStatus=None`，**不可放单位**（如 Mega 5x3 有 3 格不可用，BossHunter 3x3 有 1 格）。
   可玩船体目前是 **3x3（9 格）** 和 **5x3（15 格）** 两种（8 行 ship.csv 中 InGame=true 的 3 艘为 3x3）。
4. **战中也能改行对齐**：每回合 1 次整船上下平移（`ShipMoveCell.OnMove`，消耗 1 点 Move，`moveCount=1/回合`），
   以及有 Exchange 点时用拖拽瞬移/交换（`PlayerExchangeShipController`）。所以摆放优化器的最优解未必等于
   真人“摆一次就锁死”的解，评估时要把这两个动作留给战斗内求解器（T1/T3/T4），战前只优化初始布局。
5. **摆放会影响的东西**：单位所在 **行（y）** 决定它参与哪一条 line 的威力对撞（`BattleManager.CaculatePlayerGridPower`
   → `BattleResultInfo.GetResult`：`damage = (playerTotal - enemyTotal) * damageMul`），
   以及大量“同行/相邻/中心/边线”类技能（`Skill_*` 中 `LineAllDouble`、`NearbyOne`、`CenterLines`、`SideDefence` 等）。
   同一行内的左右位置对“行总威力”等价，但对技能（相邻、左右、列）不等价。
6. 核心（powerCore/powerColorCore）在 **ship.csv** 里是每艘船固定权重，**不是**战前可自由配的点数池：
   HongDong 等全部是 `PowerCoreWeight=[1,1,1,1,1]`（威力 1~5 各一张洗袋）、`PowerColorWeight=[3,2,1]`
   （白 3/蓝 2/紫 1 洗袋）。战前能改核心的途径只有**宝藏/事件/天赋加权重**（`PowerCore.AddWeight`、
   `PlayerShipData.AddPowerWeight`），属于 run 内成长，不是摆放优化器的自由变量。

---

## 1. 战前如何摆放单位（UI 路径、底层调用、限制）

### 1.1 UI 路径

| 层 | 类 | 作用 |
|---|---|---|
| 选船 | `ShipSelectPanel`（`dataID`、飞行员、难度、种子） | 决定 `currentPlayerShipData`、`currentPilot`、`seedString`（world+pilot）；`StartGame` → `WantedManager.Init` |
| 目标 | `WantedPanel` | 显示本轮敌人（名称/HP/KeyUnit 图标）、奖励预览；`EnterTheBattle` → `BattleManager.InitBattleGrid` |
| 整备 | `ShipRefitController`（`CreateShipRefit`） | 在 `Define.shipOrigin` 生成可拖拽船体；`InventoryPanel` 打开背包；`BaseHandleUnitPanel`/`ChangeUnitPanel` 是升级/改造弹窗 |
| 拖拽 | `ShipUnitVisual`（船上单位） + `ItemCell`（背包格） + `DragManager` | 拖起 → 指向目标格（`ShipUnitVisual.targetShipCell`）→ 放下触发装备/交换 |
| 提示/校验 | `ShipCell` + `ShipUnitVisual.OnDragBegin` | 蓝色=可放、红色=不可放 |

### 1.2 底层调用链（关键代码）

**A. 背包 → 船格（新放一个单位）**
```
ItemCell.OnEndDrag()                       // decompiled/ItemCell.cs
  → ShipCell.CheckEquipableByRealEquip(unitData)     // ShipCell.cs:81
      → CheckEquipable(target)                       // ShipCell.cs:53
          shipData.GetShipUnitsEquiptLimits() + target.GetEquiptLimit() <= shipData.shipLoadLimit
  → targetShipCell.EquipShipUnit(unitData)           // ShipCell.cs:169
      → 写 unitData / shipCellStatus = Equipped；EventName.equiptUnit 刷新 UI 与 LoadLimit
```
**B. 船格 A ↔ 船格 B（换位/交换）**
```
ShipUnitVisual.DragEndActionRefit()       // ShipUnitVisual.cs:708
  → 双方 CheckEquipableByRealEquip(对方单位)          // 交换后仍要满足 LoadLimit
  → draggingShipCell.EquipShipUnit(unitData, update:true, checkover:false)
  → targetShipCell.EquipShipUnit(unitData2)
```
**C. 船格 → 背包（卸下）**
```
InventoryPanel.OnDrop / ShipUnitVisual.RemoveThis
  → CheckEquitedToItem()（卸下后总占用不超限）→ ItemManager.ToItem(unitData) → EquipShipUnit(null)
```
**D. 战中替换/克隆（求解器会用到，但不是战前摆放）**
```
PlayerShipController.ReplaceUnit(UnitData, coor, keepBuff)   // PlayerShipController.cs:103
  → ShipCell.EquipShipUnitIgnoreLimit(...)：绕过 LoadLimit，直接改 unitData
  → 清空已装能量、重建 unitSlots/Skill/buff、刷视觉
```
> 注意：`ReplaceUnit` 走 `EquipShipUnitIgnoreLimit`，**不检查 LoadLimit**；它服务于战斗技能/battle event/敌方 AI。
> 战前摆放走的是 `EquipShipUnit`（**检查 LoadLimit**）。两者不要混用。

**E. 战中改行（等价于“临时重摆行对齐”）**
```
ShipMoveCell.OnMove(y)                    // ShipMoveCell.cs:126
  → 每回合 moveCount（Define.defineValue.moveCount=1）限制 1 次
  → currentMove -1（maxMove=3；IsFreeMove 时不扣）
  → ShipController.OnMove(y) → BattleManager.AdjustPlaperGridPosition(y)
  → 整船 grid worldPosition 平移 y 行 → 所有行与敌方行的对撞关系整体错位
```

### 1.3 限制条件清单（摆放合法性 = 全部满足）

| 限制 | 代码位置 | 说明 |
|---|---|---|
| 格子存在且可用 | `PlayerShipData.InitGrid`（`coors` + `unitID`） | `coors` 之外为 null；`unitID==-1` → `ShipCellStatus.None`，**永不可放**；`unitID==0` → Empty；`unitID>0` → Equipped（默认已有单位） |
| LoadLimit | `ShipData.shipLoadLimit`（默认 6）、`GetShipUnitsEquiptLimits()` | 所有格子 `unitData.GetEquiptLimit()` 之和 ≤ LoadLimit；默认单位多是 1，稀有/大型单位可 2~4，个别 -1（负占用） |
| 单位占用值 | `UnitData.GetEquiptLimit()` = CSV `EquiptLimit` + `slotRefitTypes` 里 Add/SubEquiptLimit | 改造/事件可改变占用 |
| 同船已装备则直接允许 | `ShipCell.CheckEquipable` / `UnitData.IsEquipted` | 同一 `UnitData` 实例在船上移动/交换不受 LoadLimit 二次计费 |
| 每格最多 1 单位 | `ShipCell` 结构（`unitData` 单值） | 无堆叠 |
| 单位类型不限格子 | 无 `Type` 与格子类型的绑定 | Cannon/Support 都可以放任意可用格；`Type` 只影响“能否装能量”（Cannon 才 `powerable`） |
| 稀有度/等级不限位置 | 无相关校验 | Rare/Lv 只影响池权重、商店价格、技能数值 |
| `shadowDir` | `ShipCellAsset.shadowDir`、`ShipUnitMod.SetShadowAndPos` | **纯视觉**（影子方向），交换/瞬移时跟着交换；不是战斗变量，不要写进优化器决策空间 |
| 战中额外限制 | `ShipMoveCell.OnMove`、`DragBeginActionExchange` | 每回合 1 次平移、消耗 Move；Exchange 点 >0 才能瞬移/交换 |

### 1.4 是否能“自由摆放”？——结论

- **可以**：战前整备界面就是自由拖拽，任意可用格 ↔ 背包，且不限次数、不花金币。
- 但**不是任意格**：受 coors/None 格、LoadLimit、每格 1 个、默认已有单位（可拖走）约束。
- **不能**在战前改：船体形状/格子数、核心权重、单位等级（升级走商店 Upgrade 事件/`ChangeUnitPanel`）、
  槽位改造（走事件 `slotRefitTypes`）。这些属于 run 内成长决策，不是摆放决策。

---

## 2. 单位（ShipUnit）数据模型与统计

### 2.1 字段含义（`Tool.Database.DataShipUnit`，CSV `shipunit.txt`）

| 字段 | 含义 | 备注 |
|---|---|---|
| `ID` | 单位模板 ID | 同一 ID 多行 = 多等级（Lv1..Lv5） |
| `Lv` | 等级 | 升级改技能数值/槽位；`UnitData.lv` 运行时取对应行 |
| `Rare` | 稀有度 0=白 1=蓝 2=紫 | 影响 unitPool 权重 `3-Rare+CountOffset`、商店价格、奖励稀有度过滤 |
| `Type` | 1=Cannon（炮，`powerable=true`，可装能量） 2=Support（支援，不可装能量） | `ShipUnitTag` |
| `PowerSlot` | 能量槽颜色需求数组，1=白 2=蓝 3=紫 | 槽数 0~6；**装填规则：槽 rare ≤ 能量最高颜色**（`ShipUnitSlot.LoadCheckColor`） |
| `EquiptLimit` | 占用 LoadLimit | 0=不占（白嫖），-1=反向腾空间，1 最常见，2~4 为重型单位 |
| `Pros` | 允许出现的玩家船 ID 列表 | 池过滤用；空=通用 |
| `Genera` | 流派/船 ID 组（1-3 个；-1 通用） | 与 `Pro` 类似的池/事件过滤 |
| `GainType` | 0/1=可进池；2/3=特殊；4=初始自带；5=敌方专用 | 见 `WantedProcess.InitShipUnitPool` |
| `UnlockLV` | 解锁所需存档总等级 | 与 `GainType` 一起决定池 |
| `Propertys` | 技能动态值字典（`PA`=额外威力等） | 影响技能数值 |
| `Args` | 技能参数数组 | 与 `Propertys` 一起格式化描述与数值 |
| `SkillPath` | 技能实现类名（`Cannon_Player/Skill_*`、`Support_Player/Skill_*`） | 战斗行为全部在这里 |
| `SkillName`/`Description` | 本地化 key | 中文在 `data/export/textmap/chinesetextmap_unit.txt` |

### 2.2 统计数据（`scripts/analysis/shipunit_stats.py` 实际输出）

**规模**
- 有效数据行 **866**（另有 54 行整行空白，导出残留，脚本已丢弃）；**唯一单位模板 472 个**。
- 等级：2 级单位 365 个、1 级 94 个、3 级 11 个、4/5 级各 1 个；最高 Lv5。
- 可获得模板（`Lv=1` 且 InGame 且 GainType 0/1）：**245 个**（白 94 / 蓝 97 / 紫 54）。

**稀有度（按行）**：Rare0 **284**（32.8%）、Rare1 **309**（35.7%）、Rare2 **273**（31.5%）。

**类型（按行）**：Cannon 424、Support 442（近似对半）。

**能量槽分布**

| 槽数 | 0 | 1 | 2 | 3 | 4 | 5 | 6 |
|---|---|---|---|---|---|---|---|
| 行数 | 277 | 223 | 220 | 110 | 22 | 7 | 7 |

槽颜色组合 Top（升序拼接）：`11`×105、`111`×58、`13`×42、`2`×39、`12`×34、`3`×30、`123`×25、`23`×21、`1111`×21。
→ 实战含义：**蓝色以上槽位稀缺**，颜色核心（3 白/2 蓝/1 紫洗袋）与紫槽单位天然不匹配，摆放/取舍时“紫槽单位值不值”是核心权衡。

**占用值**：EquiptLimit=1 有 741 行、0 有 65 行、2 有 44 行、3 有 9 行、4 有 2 行、-1 有 5 行。
→ LoadLimit=6 的 3x3 船通常可放 **4~6 个单位**（实际录制样本是 4~5 个）。

**技能**：425 个不同 Skill 实现；`Skill_Null`/空 42 行。全表最高频是敌方专用技能，玩家侧模板几乎一个技能一份
（可获得 245 个模板的技能几乎全为 count=1），说明**技能组合空间大、不能靠“常见套路表”穷举**，必须靠模拟器评估。

**Genera（船流派）**：`-1` 通用 54、船 9 42、船 0 26、船 5/8/1/2 各 24、船 11 16、船 3 12。
→ 同一艘船的池子只包含“Pros 含该船 ID + 对应 Genera”的单位，**不同船的可用单位集合差异明显**，摆放优化器
必须按船分别建池。

### 2.3 升级与强化

- **升级**：`UnitData.LvUp()` 取同 ID 下一 `Lv` 行；数值/槽位/技能参数随行变化（如 11001 Lv1 1 槽 → Lv2 2 槽）。
  升级入口：商店 Upgrade 悬赏事件、`ChangeUnitPanel`、`UnitData.UpgradePlayerUnit`。
- **槽位改造**：`slotRefitTypes`（`SlotRefitType`：AddWhite/AddBlue/AddPurple/AddHighest/AddLowest/RemoveHighest/
  RemoveLowest/DemoteHighest/AddEquiptLimit/SubEquiptLimit/AddPowerAdded/ClearSlot）。只由事件/宝藏/天赋写入，
  运行时 `UnitData.GetSlotsRares` 重算实际槽位，最多 6 槽。**这是“单位成型”的一部分，不是摆放自由度**。
- **复制/替换**：战斗技能可 `ReplaceUnit`/`CloneUnitTo`，只在战斗内发生。

---

## 3. 敌人构成、wanted 选敌、战前可见信息

### 3.1 敌人是谁

- `enemyship.csv`：**90 行**，InGame=true **83** 个；EnemyType 0/1/2 = 普通 34 / 精英 27 / Boss 22。
- `EnemyPhaseType` 决定它进哪个阶段的随机池：1/2/3（前中后期小怪）、11/12/13（精英）、101/102/103（Boss）。
- HP 1~160（中位 46）；`HP_1`/`BreakCount_1`/`ShipDataPath_1/2/3` 是 run 内“难度强化”变体
  （`ProcessAdvance.advance_hp/advance_damage_lv1..3` 生效时替换）。
- **敌方布局**来自 `data/export/scriptableassets/enemyshipassets/*.asset.json`（393 个）：
  每格 `unitID/unitLv/shadowDir` + 每格 `AIPowers/AIOrder/loadPreview` + 全局 `moves/notices/controller(AI 类名)`。
  例如 `ship_basic` 每格带 3~4 组 `aiPowerDatas`（威力 5~8 的敌人能量），AI 完全确定性。
- `KeyUnitIDs/KeyUnitLVs` 只是**展示用**（`WantedPanel.CreateKeyUnits` 在战前 UI 显示敌方关键单位图标），
  不参与生成布局。

### 3.2 wanted 如何选敌

```
WantedPhases.RandomEnemyID(enemyPool, phase)
  → DataReward.GetDataByPhase(phase).EnemyPhaseType 过滤 enemyPool（同类型里随机）
  → UtilsClass.RandomRange(..., "poll_enemy")   // 受种子/战斗状态 hash 控制
```
- `enemyPool` 在 `WantedProcess.InitEnemyPool` 构建：InGame + `UnlockLV ≤ 存档总等级` + `OnlyPro` 含该船（或空）
  + **排除飞行员天赋 `BanEnemyList`**。
- 种子：`seedString = world_seed + "-" + pilot_seed`，`world_seed` 8 位、`pilot_seed` 由飞行员决定
  （`UtilsClass.PilotToSeedFormat`）；同一 seed + 同一船/飞行员/难度 ⇒ **整条 run 的敌人序列、商店、事件可复现**。
- 每场战斗还会挂 `battleEventID`（`InitBattleEvents`，基于 `poll_BE` 随机挑选 2~4 场），
  对应 `battleevent.txt`/`BattleEventSkill`，会在战斗中改布局/加 buff（如 `BCO_EquiptByCenterLines` 系）。

### 3.3 战前玩家能看到什么

- 敌人：名称、立绘、HP、BreakCount、**KeyUnit 图标+等级**（战前可见！）、背景、开场/胜利台词。
- 奖励预览：星币、单位三选一/二选一、宝藏、扩展（LoadLimit+1）、Move 回复、修理、传奇；
- 自己的：船体、单位、等级、槽位、LoadLimit、金币、宝藏、飞行员天赋、本 run 种子（`totalLV ≥ 1` 可设种子）。

→ **对求解器友好**：战前即可拿到“给定种子下的敌人完整配置”（EnemyShipAsset + 强化变体 + battleEvent），
  因此“给定布局算整场战斗”是**确定性的单场规划问题**，不需要对抗随机敌人。

---

## 4. 一趟 run 的资源循环（与战斗数值相关的部分，详见 §4 表）

| 资源/系统 | 获取/消耗 | 对战斗数值的影响 | 代码 |
|---|---|---|---|
| 星币 starCoin | 开局 12；每场胜利奖励（`DataReward.StarCoin`）；事件 | 商店买单位/宝藏/买血/买 Move | `WantedProcess`、`ShopPrice_Normal`（白 8/蓝 12/紫 18，Lv>1 +6，宝藏 10/16/22） |
| 单位 unitPool | 奖励二选一/三选一、商店 4 个单位、事件 | 直接进入背包，可战前摆放 | `WantedProcess.InitShipUnitPool`（`3-Rare+CountOffset` 加权）、`Wanted.unitChoiceIDs` |
| 宝藏 treasure | 奖励、商店 3 个、事件 | **被动技能**（`TreasureSkill`），232 个中 152 个 `BattleRelate=true` 直接改战斗 | `ItemTreasure.AddSkill`、`Wanted.treasureIDs` |
| 扩展 expansion | 奖励 `Expansion` | **LoadLimit +1**（`ShipData.SetLoad`）→ 能多摆单位 | `BountyPanel`/奖励 |
| Move | 奖励 `Move` | maxMove 提升（平移次数上限） | `ShipData.SetMaxMove` |
| 修理 | 奖励 `Repaire`（ItemRestore/Half） | 回 HP，影响“剩余 HP”目标 | `ItemRestore` |
| 飞行员 pilot | 开局选择（固定天赋 + 0~2 随机天赋） | `TalentSkill` 被动（加威力/改核心/禁敌/商店折扣等） | `PlayerPilotData.InitTalent`、`talent.csv` Type 1/2 |
| 核心权重 | 事件/宝藏/天赋 `AddPowerWeight/AddPowerColorWeight` | 改手牌威力/颜色分布 | `PowerCore.AddWeight`、`PlayerShipData.AddPowerWeight` |
| 难度 | `selectDifficulty`（0..9） | 敌人强化/奖励变化 | `ShipSelectPanel`、`ProcessAdvance` |

**能量/战斗节奏常数（`DefineValue` 默认值，录制里一致）**：
`powerInitNum=4`（首回合手牌 4）、`powerNumEveryTurn=3`（每回合 +3）、手牌上限 10、`powerLimit=9`；
`moveCount=1`（每回合 1 次平移）、船 `maxMove=3`、`maxExchange` 由船技能给（HongDong 样本为 0）。
→ 一局典型 3~5 回合，每回合可装 3~4 张能量，**总装填次数 10~20 次**，手牌总量有限，所以“哪些槽先喂、
喂什么颜色”是战内战，而“哪些单位、放哪行”是战前主决策。

---

## 5. “摆放优化器”的形式化定义与搜索空间

### 5.1 输入

```
给定：
  ship      = PlayerShipData：cellSet C（可用格 (x,y)，|C|=9 或 15）、shipLoadLimit L（默认 6，可涨）
  units     = 可用单位多重集 U = {u_i = (id_i, lv_i, slotRefitTypes_i)}（背包 + 船上默认）
  enemy     = EnemyShipData：敌方每格 (unitID, lv)、AI 序列（AIPowers/AIOrder/moves/notices）
              + HP/BreakCount（按 ProcessAdvance 选强化变体）
  battleEvent = 本场 battleEventID（可 0）及其 BattleEventSkill
  pilot/talents/treasures = 被动技能集合（TalentSkill/TreasureSkill 实例，作用于 playerShip）
  core      = PowerCore/PowerColorCore 洗袋内容（ship.csv 权重 + run 内增益）
  seed      = world_seed/pilot_seed（决定洗袋顺序与随机 key 流）
  battleState = 进战时的 HP/shield/coin 等
```

### 5.2 决策变量（战前）

```
place:  C → U ∪ {∅}            单射，且 Σ_{c∈C} equiptLimit(place(c)) ≤ L
level:  每个 u_i 的 lv（若背包里有可升级的，受升级资源限制）
refit:  每个 u_i 的 slotRefitTypes（若 run 内已获得改造道具）
```
- `shadowDir` **不是变量**（纯视觉，见 §1.3）。
- `powerCore/powerColorCore` 在战前**不可自由配**（ship.csv 固定 + run 内事件增益），只能作为常量输入。
- 方向/朝向：本作没有“单位朝向”机制，只有行/列/相邻关系与 `shadowDir` 视觉影方向，**不要引入朝向变量**。

### 5.3 目标

```
maximize  J = P(win | place, level, refit, enemy, seed) * W_win
         + E[remainingHP | ...] * W_hp
         - E[turns] * W_turn  (可选)
s.t.      合法性约束（§1.3）
```
其中 `win/remainingHP/turns` 由**无头精确模拟器**一次完整战斗给出（lead 已验证 100% 复现），
若求解预算允许，可先按 `BattleResultInfo` 的单回合伤害公式做启发式预筛，再用模拟器定胜负。

### 5.4 搜索空间估算

只算“有序单射”（单位→格子的位置分配）：

| 船体格 | k=3 | k=4 | k=5 | k=6 |
|---|---|---|---|---|
| 9 格（3x3） | 504 | **3,024** | **15,120** | **60,480** |
| 12 格 | 1,320 | 11,880 | 95,040 | 665,280 |
| 15 格（5x3） | 2,730 | 32,760 | 360,360 | 3,603,600 |

- 实际还要乘：单位多副本/等级的等价类（若只关心“哪个模板放在哪”，可按模板去重）；
- `slotRefitTypes` 组合（最多 6 槽、十余种改造）量级 10^2~10^3；
- **化简**：行内左右交换对“行总威力”等价 → 先按“每个单位分到哪一行 + 行内顺序对技能的影响”两级搜索；
  又因 `CaculatePlayerGridPower` 是**按行求和**，可先求每行的最优单位集合（子集和/背包），
  再在行内按技能（相邻/左右/列）细化。
- 单场模拟耗时（lead 提供）：一次完整战斗可精确模拟，但若 1 场 ≈ 0.1s，则 3x3、k=5 的 1.5 万布局 ≈ 25 分钟，
  可行；15 格 k=6 的 360 万布局必须靠剪枝/beam/并行或代理模型。

---

## 6. 建议的第一版目标与评分函数，以及数据缺口

### 6.1 第一版目标（建议）

**对固定敌人配置（用 seed + wanted 序列还原），最大化“模拟器跑完整场战斗”的：**

```
score(layout) = 1000 * [win ? 1 : 0]
              + 10  * clamp(remainingHP, 0, maxHP)
              + 1   * (maxHP - remainingHP 等价…)  ← 已由上式覆盖
              - 0.5 * turns
```
更工程化的写法（避免胜负被小 HP 差淹没）：

```
primary   : winRate            （先只比胜负，胜负是硬约束）
secondary : E[remainingHP]     （同胜率下比残血）
tertiary  : E[turns]           （残血相同比速度，可选）
```
- 第一版**先用单场确定性模拟**（不跑期望）：固定种子、固定战术策略（第一版用“贪心装填 + 固定行对齐”或直接
  交给 T4 回合求解器），得到确定性的 win/HP；这样 `J = 1000[win] + 10*remHP - 0.5*turns` 无随机噪声。
- 需要“胜率”时再对 `battle_states`/种子做 K 次采样（手牌洗袋 + random key 流），取 K 场平均。
- 硬约束：LoadLimit 合法、coors 合法单元、每格 1 个；建议再加一条“至少放 1 个 Cannon，否则无来源装能量”（软惩罚）。

### 6.2 第一版实现建议

1. 输入直接从录制 `battle_begin` 的 JSON（`player_ship`、`pilot`、`treasures`、`wanted`、`define_value`）取，
   或从 `ship.json`/`wantedProcess.json`（加密，需解密）取；推荐用 `battle_begin`（可读、字段完整）。
2. 布局枚举：9 格小空间先做**全枚举 P(9,k)**；15 格用 beam search/行聚合。
3. 每个候选布局喂给无头模拟器跑整场，取 `win / remainingHP / turns`；
   单场失败=0 分，胜场按残血排序，简单稳定，便于和人类录制对拍。
4. 人工验证：用 4 份录制（`20261018_1854/`、`1856/`、`1857/`）的初始布局作为 baseline，
   求解器结果必须 ≥ baseline（同种子、同战术）。

### 6.3 已验证可直接用的数据

- `battle_begin` 记录含完整 `player_ship.grid`（每格 unitData id/lv/slotRefitTypes/order）、
  `powerWeights/powerColorWeights`、`treasures`、`pilot.randomTalentIDs`、`wanted.enemyData`、
  `bounty_events`、`coin`、`define_value`、`process_advance`。
- 4 份录制实测布局：都是 HongDong（ID 7，3x3，LoadLimit 6），4~5 个单位（Lv2），
  如 `{11001@2, 11801@2, 11002@2}` + 第 4 个 11001@2，位置分散在 (2,0)/(0,1)/(1,1)/(2,2)。
  说明真人玩家也只在 9 格里放 4~5 个（LoadLimit 与单位占用共同决定）。

### 6.4 数据不足 / 需在游戏内实测的点

1. **`Propertys` 各键的精确语义**（PA/US/TI/CD/OC/CH/SU/DY...）：CSV 只有键和值，具体含义需读 `Skill_*` 实现，
   建议由 T1（战斗机制）给出权威解释；摆放优化器若需要“预估每单位贡献”，必须先把这些键映射成数值模型。
2. **同 ID 多副本的合法性**：代码允许（LoadLimit 内可放多个同模板），但需要游戏内实测确认 UI 允许拖多个同 ID 副本
   （录制里 4 个位置有 2 个是 11001，说明至少 2 个可同时在场，但仍建议实测 3+ 个）。
3. **slotRefitTypes 的获取渠道/上限**（事件/宝藏哪些能改槽、能否重复）未在数据里完全枚举；
   需要读 `Talent/Treasure/BattleEvent` 相关 Skill 或在游戏内实测。
4. **`battleevent.txt` 的 37 个战斗事件**对布局的影响（如 `BCO_EquiptTo`、`BCO_EquiptByCenterLines`）需要
   按事件 id 逐一验证其“改哪些格/哪一方”，第一版建议直接排除带 battleEvent 的场次或强制模拟。
5. **难度（0~9）与 `ProcessAdvance` 的强化组合**：`advance_hp/advance_damage_lv1..3/advance_break` 同时影响敌 HP/
   BreakCount/布局变体，具体映射要从 `ProcessAdvance` 全量枚举（本次只确认了字段）。
6. **敌方 AI 的确定性来源**：`AISequence` + `AIPowers` 已给出足量数据，但 `UtilsClass.RandomRange` 的 key 流与
   `battle_states` 的组合需由 T1/T3 用模拟器验证；本任务未深入（属 T1 范围）。
7. **非 InGame 船（JX_001/JX_002/Mega 等）**：数据存在但是否可通过 mod/成就解锁需实测；
   若可用，则摆放问题还包括“选哪艘船”（多一个离散变量，9 格 vs 15 格，空间放大 100 倍）。
8. **真实“摆完再打”的完整输入**：目前 4 份录制都是 HongDong + 少量单位；需要更多船/更高难度/带宝藏的录制来验证
   目标函数与数据模型，尤其验证 §1.3 的 LoadLimit 计算在稀有重型单位下是否一致。

---

## 附 A. 统计脚本与复现

```powershell
python scripts/analysis/shipunit_stats.py
```
- 只读 `data/export/csv/*.txt`、`data/export/scriptableassets/{shipassets,enemyshipassets}/*.json`；
- 输出本文档 §2.2 / §3.1 / §5.4 的全部数字（可与本文档逐项对照）。

## 附 B. 主要代码索引

| 主题 | 文件 |
|---|---|
| 战前船体/拖拽 | `ShipRefitController.cs`、`ShipUnitVisual.cs`（`DragEndActionRefit`）、`ShipCell.cs`、`DragManager.cs` |
| 背包/装卸 | `InventoryPanel.cs`、`ItemCell.cs`、`ItemManager.cs` |
| 升级/改造 | `BaseHandleUnitPanel.cs`、`ChangeUnitPanel.cs`、`UnitData.cs` |
| 单位数据/技能 | `Tool.Database/DataShipUnit.cs`、`ShipUnitController.cs`、`ShipUnitSlot.cs`、`Skill_*.cs` |
| 能量/核心 | `PowerManager.cs`、`PowerCore.cs`、`PowerController.cs`、`EnergyData.cs`、`PowerColors.cs`、`PlayerShipData.cs` |
| 战斗行结算 | `BattleManager.cs`（`CaculatePlayerGridPower` / `SignPlayerBattleResultInfo`）、`BattleResultInfo.cs` |
| 敌人/wanted | `WantedProcess.cs`、`WantedPhases.cs`、`WantedManager.cs`、`WantedPanel.cs`、`EnemyShipData.cs`、`EnemyShipController.cs` |
| 数据导出资产 | `data/export/scriptableassets/shipassets/*.json`、`enemyshipassets/*.json` |
| 真实布局样本 | `%LOCALAPPDATA%Low\Shuxi\LONESTAR\Mods\Dev\SolverLogger\logs\battles\*.jsonl`（`battle_begin`） |
