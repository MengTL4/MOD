# 战术求解器 | Tactics Solver

《寰宇孤星 / LONESTAR》的**战斗求解 MOD**。当前版本：**1.1.0**（创意工坊版：自带求解器、中英双语界面）

它不再用手写公式去"估"战斗，而是把局面交给 **Brain**——一个把游戏自己的战斗代码原样跑在后台的求解服务
（源码在同仓库的 [solver](../solver)，打包时随 mod 一起发布，玩家什么都不用装），在真实规则下把各种打法都打一遍，再告诉你这一回合怎么走。

| 功能 | 说明 |
|---|---|
| 战斗求解 | 每个玩家回合自动求解：装能量 / 移动 / 交换 / 主动技能（含二段选靶）/ 战斗事件选项，全部在真实引擎里结算（护盾、减伤、破防、部件被毁、敌方回合…都是游戏自己算的），并往后多看一回合 |
| 目标 | 先保证赢，再尽量少掉血，再尽量少回合 |
| 棋盘标注 | 下一步直接画在船上：要用的手牌 → 目标部件（金色连线）、交换（紫色）、技能（橙色）、移动箭头，带步骤序号 |
| 执行 | `F5` 下一步、"执行本回合"、`F7` 自动接管（每回合自己求解并执行，直到战斗结束） |
| 船坞：真实模拟摆位 | 针对下一场悬赏的敌人，试遍"谁上船、放哪格"，每种都实际打一遍，选最好的，一键摆好 |
| 后备 | 求解服务不可用时自动退回"本地估算"（旧的游戏预览探针，只看本回合） |

---

## 效果（无头引擎实测）

同一份存档、同一个敌人，求解器自己打完整场：

| 对手 | 人类 / 贪心基线 | Brain |
|---|---|---|
| 录制 b0s0（敌 19 HP） | 人类：3 回合，剩 26 HP | **2 回合，剩 26 HP（满血）** |
| 录制 b0s1（敌 16 HP） | 人类：4 回合，剩 23 HP | **2 回合，剩 26 HP（满血）** |
| 录制 b0s2（敌 35 HP） | 人类：4 回合，剩 21 HP | 4 回合，剩 21 HP（不掉血） |
| 第一阶段 9 种敌人 | 贪心：有掉血 | **全部满血**，平均少 1 回合 |
| 第二阶段 403 | 贪心：7 回合，剩 6 HP | **6 回合，剩 16 HP** |
| 第二阶段 411 / 412 / 417 | 贪心：剩 19 / 12 / 6 HP | **剩 20 / 21 / 17 HP**，回合也更少 |
| 精英 203 / 204 / 205 | 贪心：剩 19 / 15 / 19 HP | **全部满血（21）** |
| **Boss 301** | 贪心：**第 9 回合战败** | **第 5 回合胜利，剩 18 HP** |
| 第三阶段 701、精英 501（开局船明显打不过） | 贪心：敌人剩 62 / 60 HP | 仍然输，但敌人只剩 39 / 31 HP |

（同一份 b0s2 存档，沙盘里换敌人；"贪心"＝每张牌就近补缺口的简单策略。另外沙盘里放上主动技能部件，
它会自己用一步技能、二段选靶技能，并把技能当场造出来的能量装上去。）

只有一档搜索强度（原来的 deep）。每回合默认**不限时**：完整深度搜索跑完（同一局面结果固定，一般十几到几十秒），也可以在战斗面板用 -/+ 改成 10 秒～5 分钟的限时（限时模式会把剩余时间用来多看几个方案）。船坞摆位找不到更好的会自己停，另有上限（默认 10 分钟）。两个面板求解中都有"立即出结果"：马上用目前最好的方案。时间设置会记住。后台进程默认用满全部 CPU 线程（低优先级，不抢游戏）。

---

## 安装 / 打包 / 上传

玩家：在创意工坊订阅即可。求解器（Brain）在 mod 目录的 `Brain/` 里，自带 .NET 运行时；第一次求解时 mod 把它复制到
`%LocalLow%\Shuxi\LONESTAR\LonestarPlanner\brain\` 再启动（mod 目录不被占用，工坊更新不冲突），
监听 127.0.0.1:47321，游戏退出（包括崩溃）时自己退出。仅支持 Windows x64。

开发：

```powershell
cd F:\LonestarPlanner\mod
pwsh tools/package.ps1           # 打包到 dist\LonestarPlanner（约 100MB：mod + 自带运行时的求解器 + 游戏资源）
pwsh tools/package.ps1 -Deploy   # 再部署到 %LocalLow%\Shuxi\LONESTAR\Mods\Dev\LonestarPlanner（先关游戏）
python tools/make_preview.py     # 重新生成创意工坊预览图 tools/preview.png（也可以换成游戏截图）
```

上传：部署到 Dev 目录后，在游戏的 Mod 面板里对这个 mod 点上传（游戏把整个 mod 目录传上去，标题、介绍取自
`mod.json`，预览图是 `preview.png`；`config.json` 不会上传）。**上传前确认开发者允许发布**：`Brain/` 里的
`Lonestar.Game.dll` 是用游戏代码编译的，`Brain/data/` 是从游戏导出的资源。

游戏更新后：在 solver 里重新反编译、跑回归，再打包上传。`Brain/brain.json` 记着做它时游戏
`Assembly-CSharp.dll` 的 SHA256，对不上时两个面板都会提示"游戏更新过，方案可能不准"。

数据目录 `%LocalLow%\Shuxi\LONESTAR\LonestarPlanner\`：

| 路径 | 内容 |
|---|---|
| `logs\battles\` | 每场战斗的录制（Brain 靠它同步局面；保留最近 60 场） |
| `logs\brain.log` | 求解服务的输出（每次启动覆盖），反馈问题时附上 |
| `logs\loadout\` | 船坞求解用的开局文件（最近 20 个） |
| `backups\` | 一键摆放前的原布局（最近 30 个） |
| `brain\` | 求解器缓存 |

MOD 设置（游戏内 Mod 设置面板，文字跟随游戏语言）：

| 设置 | 默认 | 说明 |
|---|---|---|
| 界面语言 | Auto | 跟随游戏：中文显示中文，其他语言显示英文 |
| 界面缩放 / 默认显示面板 | 1.0 / 开 | 面板隐藏时屏幕左侧有个小标签，点它或按 F8 打开 |
| 每回合求解时间 | 0（不限时） | 0 或 10~300 秒；战斗面板底部也能 -/+ 调 |
| 在棋盘上标出接下来的操作 | 开 | |
| 每场战斗开始时自动接管 / 自动操作间隔 | 关 / 0.45s | |
| 摆位求解时间上限 | 600 秒 | 60~1800 秒；收敛会提前停；船坞面板也能 -/+ 调 |
| 求解进程数 | 0（自动） | 每个 CPU 线程一个，内存不足 16GB 时按内存减（每个约 350MB）；低优先级运行；重启游戏后生效 |
| 只用本地估算 | 关 | 不启动求解器 |
| 开发者：求解器源码目录 | 空 | 填仓库里的 solver 目录就改用那里编译的 Brain（`dotnet Brain.dll`） |
| 开发者：F9 导出 / GM 控制台 | 关 | |

面板上 -/+ 改的时间和设置面板存在同一个 `config.json` 里。

---

## 战斗里怎么用

- 每个玩家回合开始约 0.6 秒后自动求解，面板显示：
  - **预计结局**：例如"预计第 4 回合获胜 · 剩 23 HP"（绿色）；会输时显示"预计会输：尽量多打伤害"（红色）
  - 本回合预计造成 / 承受的伤害
  - **操作顺序**：已做的打勾，当前步骤高亮
  - **对撞预测**：执行完本回合后每一行"我方 : 敌方"的条形对比与胜负
  - 展开"下回合预案 / 备选方案"能看到前瞻里下回合打算怎么打
- 棋盘上同时画出接下来 3 步（当前步实线高亮，后面的半透明）。
- 求解中显示进度条（启动模拟器 → 搜索本回合 → 推演候选 → 前瞻下回合）和用时，可以"立即出结果"。
- 按键：`F5` 下一步 · `F7` 自动接管开关 · `F8` 显示/隐藏面板；面板可拖动，标题栏 `—` 折叠成只剩结局和按钮。
- 你手动改了局面（装了牌、移动了），方案会自动作废并按新局面重算。
- 某一步执行失败会按当前局面重算（每回合最多 2 次），还不行就停下来交给你。
- 如果面板提示"模拟器和游戏有 N 处不一致"，说明这场战斗里有引擎还没完全复现的东西，方案仅供参考，欢迎把
  `%LocalLow%\Shuxi\LONESTAR\LonestarPlanner\logs\battles\` 里对应的战斗文件和 `logs\brain.log` 发出来排查。

## 船坞里怎么用

1. 打开悬赏面板，选好下一场要打的敌人（面板上会显示"下一场：…"）；
2. 船坞面板点"**求最佳摆位**"：MOD 先存档（和进战斗前游戏自己做的一样），把存档 + 敌人交给 Brain；
3. 进度条显示筛选了多少种摆法、完整对局验证到第几个；大约 20 秒~几分钟后给出推荐摆位和当前摆位各自的结局，以及要做的调整；
4. 点"**按推荐摆放**"（或 `F6`），**要点两次**确认：走游戏自己的装配链路，执行前把原布局备份到数据目录的 `backups\`。

1.0 的"快速估算"（近似模型，不看敌人）在 1.1 去掉了：真实模拟摆位直接在引擎里打，全面取代了它。

---

## 它是怎么工作的

```
游戏（MOD）                                   Brain 服务（solver/tools/Brain）       
───────────                                  ─────────────────────────────────────
录制器把每个动作写进 logs/battles/*.jsonl ─┐
                                            ├→ 12 个工作进程，各自跑一份游戏原版战斗代码（无头）
回合开始：请求求解（TCP）─────────────────┘   · 按录制文件同步到当前局面，并和游戏内快照逐字段比对
                                               · 进程内快照/恢复（约 2ms），在任意"动作前缀"之间跳转
                                               · 回合内 beam 搜索（用游戏自己的对撞预览排序）
                                               · 候选方案：真实结算 + 敌方回合 + 贪心打完整场
                                               · 最好的几个再搜一遍下回合（两回合前瞻）
                                               · 贪心策略这一手和上回合的前瞻计划永远在候选里
                                                 → 预测的结局一定能兑现，也绝不比贪心差
◀────────────── 方案（动作令牌）+ 预计结局 + 对撞预测
按令牌执行：PowerLoad / OnMove / 交换序列 / 技能 Click+选靶 / 选项 Click / BattlePanel.EndTurn
（和玩家操作同一个入口，所以录制器记下的动作 Brain 能原样回放，保持同步）
```

动作令牌：`loadp:P/C@x,y`（点数 P、颜色 C 的牌装到 (x,y) 部件）、`move:±1`、`swap:x1,y1>x2,y2`、
`skill:x,y`、`skill:x,y>u:a,b` / `>c:a,b`（二段技能目标）、`choice:j`、
`paint:P/C@r`（多彩号：把这张牌拖到 r 色涂料上，1 白 2 蓝 3 橙）、`paintpool:r`（多彩号 + 点击涂料天赋）、`end`。

三艘船都支持：护盾号（护盾是被动的）、空间号（`swap` 换位）、多彩号（`paint` 涂色）。

### 目录

```
src/Planner.Mod/
├─ PlannerMod.cs            入口 + 设置；初始化录制器（源码链接自 solver/src/SolverLogger）
├─ PlannerHud.cs            战斗界面：面板 + 棋盘标注
├─ TurnPilot.cs             每回合"求解 → 执行 → 失败重算"状态机（手动 / 本回合 / 自动）
├─ BrainClient.cs           一个求解请求（后台线程；结构化进度 / 错误码 → 界面文字）
├─ BrainService.cs          求解服务进程：找（自带 / 开发目录）、缓存复制、启动、版本握手、退出
├─ Theme.cs / Lang.cs       两个面板共用的外观；中英文（跟随游戏语言）
├─ SettingsText.cs          Mod 设置面板的文字（按游戏现有语言写 Translation/*.csv）
├─ Housekeeping.cs          数据目录：迁移旧日志、只保留最近的记录
├─ ActionExecutor.cs        在游戏里执行动作令牌（入口与无头引擎一致）+ 二段技能选靶捕获
├─ Describe.cs              令牌 / 摆位步骤 / 结局 → 给人看的话
├─ LocalPlanner.cs          后备：旧的预览探针搜索 → 令牌
├─ PlannerOverlay*.cs       船坞：真实模拟摆位 + 一键摆放
└─ EngineOracle / ProbeSearch / GameStateReader / DockExecutor   旧模块（后备与船坞仍在用）
src/Planner.Core/           近似模型（本地估算后备、摆位步骤生成；摆位穷举现在只剩单元测试在用），零 Unity 依赖
tests/Planner.Tests/        Core 的单元测试（dotnet run）
```

---

## 一键摆放怎么执行

船坞里的"挪部件"本质上就是三件事：改 `ShipCell.unitData`、维护 `ItemManager` 的背包、
然后让视觉重建。执行器 `DockExecutor` 走的是和拖拽完全相同的那条链路：

| 操作 | 游戏调用 |
|---|---|
| 船内移动 | `from.EquipShipUnit(null)` → `to.EquipShipUnit(unitData)` |
| 两件互换 | `a.EquipShipUnit(unitB)` → `b.EquipShipUnit(unitA)`（和 `DragEndActionRefit` 一致） |
| 从背包上船 | `to.EquipShipUnit(item.unitData)` + `ItemManager.ToCell(item)` |
| 卸回背包 | `cell.EquipShipUnit(null)` + `ItemManager.ToItem(unitData)` |

视觉不用管：`ShipBaseVisual` 给每个格子挂了 `grid` 变更监听，`EquipShipUnit` 触发变更后
会自己销毁/重建那一格的控制器。

防丢件措施：

1. 执行前把当前布局写到 `backups\dock-before-*.txt`
2. 每一步执行前用 `CheckEquipable` 预检、执行后核对 `cell.unitData` 是否真的变成目标
3. 移动/互换失败会自动回滚
4. 全部做完后再读一次船坞，核对"目标布局是否真的到位"（面板会显示 `校验通过 N/N`）

## 开发者模式（游戏自带 GM 控制台）

游戏里本来就有一个 GM 控制台（`GameMasterManager`），只是被 `Define.gameMaster = false` 关着。
它是一个 `static readonly bool`（不是 `const`），所以 mod 可以在 `OnLoad` 里反射打开：

> mod 设置 →「开启游戏内置 GM 控制台」→ 重启游戏 → 战斗中按 `Enter`

打开后左上角出现输入框，按 `Enter` 执行，`Esc` 关闭。命令是反射匹配 `Command_<名字>`，
参数用空格分隔。对我们最有用的几条：

| 命令 | 作用 |
|---|---|
| `power <能量> <颜色1白2蓝3紫>` | 造一枚指定 token |
| `power3 <能量>` | 造一枚三色 token（白+蓝+紫） |
| `powers` | 清空手牌，发 10 枚紫色 9 点 |
| `powercore <n>` | 按能量核心权重造 n 枚 |
| `clearpower` | 清空手牌 |
| `playermove <n>` / `enemymove <n>` | 直接移动船 n 行（不走移动次数限制）——用来验证车道对齐 |
| `exchange <n>` / `addmove <n>` | 给交换次数 / 移动次数 |
| `pause` / `resume` | 暂停/恢复（`Time.timeScale`） |
| `win` / `lose` | 直接结束战斗 |
| `additem <id> <lv>` | 发部件 |
| `unitnum <n>` / `setlv <lv>` | 改部件数量 / 等级 |

完整命令列表见 `docs/mechanics.md` 末尾。

### 用 GM 验证求解器（推荐流程）

1. 进任意一场战斗，面板 `F8` 打开
2. `Enter` → `powers`：手牌变成 10 枚紫 9
3. 看面板给出的能量顺序，按 `F9` 导出
4. 检查 `PlannerDumps\<时间>-battle.probe.txt`：里面有**逐行对账**
   （模型预测 vs 引擎实测）
5. `Enter` → `playermove 1`，再看面板：移动建议和行对齐应该跟着变

## 游戏内部结构对照（反编译得到，供维护）

| 概念 | 游戏类型 |
|---|---|
| 飞船网格 | `ShipData.grid`（`GridMap<ShipCell>`，`shipSize` / `shipCenter`），`ShipCellStatus.None` 为不可用 |
| 部件实例 | `ShipUnitController`（`unitData`, `unitSlots`, `powerable`, `powerDataContainer`） |
| 部件定义 | `Tool.Database.DataShipUnit`（`PowerSlot` = 槽位稀有度数组，`EquiptLimit` = 载重） |
| 槽位 | `ShipUnitSlot`（`rare`, `powerData`, `isDisturbed`） |
| 能量 token | `EnergyData`（`power`, `powerColors`，颜色是集合） |
| 手牌 | `PowerManager.GetHandPowers()`，上限 `powerCountLimit = 10`，跨回合保留 |
| 行战力 | `PowerDataLineContainer.GetLineTotalPower()`（只统计 `powerable`） |
| 行结果 | `BattleResultInfo.result = playerTotal - enemyTotal`（乘倍率） |
| 战斗内交换 | `shipData.currentExchange` / `maxExchange`，一次交换扣 1 点 |
| 船坞 | `InventoryPanel`（拖拽装配）+ `ItemManager.itemUnits`（背包） |
| 商店价格 | `ShopPrice`（白 8 / 蓝 12 / 紫 18 / 升级 +6 / 宝物 10·16·22） |
| mod 入口 | `Mods.UserMod`，`OnLoad()` → `harmony.PatchAll()` |
