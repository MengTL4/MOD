# LONESTAR 最优策略求解器（MOD 项目）

> 把游戏自己的战斗代码原样搬进无头环境，在真实引擎里搜索最优操作。
> **2026-10-22 起主力是 `tools/Brain`**：进程内快照（恢复一次约 2ms）+ 常驻工作进程池 + 两回合前瞻，
> 每个决策 1~8 秒；游戏内由同仓库的 [mod](../mod) 接入
> （棋盘标注、一键/自动执行、船坞真实模拟摆位）。下文第 1~9 节是早期 `tools/Solve` 的记录，仍然有效但已不是推荐入口。

游戏安装目录：`F:\SteamLibrary\steamapps\common\LONESTAR`（Unity 2021.3.30f1 + Mono）。
本项目目录：`F:\LonestarPlanner\solver`（仓库 `F:\LonestarPlanner` 的求解器部分，mod 在旁边的 `mod\`）。

## 0. Brain（推荐）

```powershell
dotnet build tools\Brain\Brain.csproj -c Release

# 求解某场录制的第 N 回合
dotnet tools\Brain\bin\Release\net10.0\Brain.dll <battle.jsonl> --decide --turn 2 [--fast|--deep] [--jobs 12]
# 从第 1 回合起自己打完整场（--enemy ID：沙盘里换成别的敌人）
dotnet tools\Brain\bin\Release\net10.0\Brain.dll <battle.jsonl> --play [--enemy 403]
# 纯贪心基线 / 列出敌舰 / 战前摆位优化
dotnet tools\Brain\bin\Release\net10.0\Brain.dll <battle.jsonl> --greedy [--enemy 403]
dotnet tools\Brain\bin\Release\net10.0\Brain.dll <battle.jsonl> --list-enemies
dotnet tools\Brain\bin\Release\net10.0\Brain.dll <battle.jsonl> --loadout [--enemy 403]
# 游戏 MOD 用的常驻服务（127.0.0.1:47321，JSON 行协议，见 tools/Brain/Service.cs）
dotnet tools\Brain\bin\Release\net10.0\Brain.dll --serve [--jobs 12]
```

| 组件 | 位置 | 说明 |
|---|---|---|
| 进程内快照 | `src/Lonestar.Engine/WorldSnapshot.cs` | 从游戏/shim/RNG 程序集的全部静态字段（含闭合的 `Singleton<T>`）遍历对象图，逐对象浅拷贝；恢复时用生成的 IL 把字段原地写回（保留 GC 写屏障）。约 2.5 万对象，捕获 ~10ms、恢复 ~2ms；`tools/SnapTest` 验证恢复后未来逐回合指纹一致 |
| 前缀世界 | `src/Lonestar.Engine/SearchWorld.cs` | 用"动作令牌前缀"寻址状态，LRU 快照缓存 |
| 完整动作集 | `src/Lonestar.Engine/BattleActions.cs` | `loadp/move/swap/skill[>target]/choice/end` 令牌、合法动作枚举（含交换与二段技能目标）、录制里全部动作类型的回放 |
| 实时同步 | `BattleSession.SyncRecording/CheckSync` | 跟随游戏正在写的战斗文件，并与最新的游戏内快照逐字段比对 |
| 搜索 | `tools/Brain/Search.cs` | 回合内 beam（游戏对撞预览排序）→ 候选真实结算 + 贪心打完 → 最好几个再搜下回合；贪心那一手和上回合前瞻计划永远在候选里（预测一定可兑现，永不劣于贪心）；目标：胜利 ≫ 剩余 HP ≫ 回合数 |
| 摆位 | `tools/Brain/LoadoutSearch.cs` | 恢复战前快照 → 改摆位 → 进战斗；贪心续战爬山（移动/互换/换上背包/卸下），前几名用完整搜索逐回合打完定胜负 |
| 服务 | `tools/Brain/Service.cs` | solve / loadout；每场新战斗换新的工作进程 |

引擎层这次还修了两个影响确定性的问题：游戏里几个 `async void` 的 UI 特效 `await Task.Delay` 会在线程池上续跑、和帧循环抢数据
（偶发崩溃）——现在 `Task.Delay` 被改写成虚拟时钟上的 `HeadlessAsync.Delay`，主线程装了帧同步上下文；以及"无 tween"的
静止判定永远不成立（UI 每帧重启短 tween）导致每个动作空跑 300 帧——现在只等有回调的 tween / 协程 / Invoke / 延迟销毁。
模拟整体快了约 6 倍，3 份录制回放依旧 100% 一致。

---

## 1. 已经实现的东西

| 模块 | 位置 | 状态 |
|---|---|---|
| 无头精确模拟器（真实游戏逻辑 + Unity shim） | `src/Lonestar.Game`、`src/Lonestar.UnityShim`、`src/Lonestar.Engine` | ✅ 已验证 |
| 游戏内录制器（状态快照 + 玩家操作 + RNG 流 + 战前存档） | `src/SolverLogger`（游戏内 MOD） | ✅ 已在游戏中跑过 |
| 可编程战斗会话 API（状态 / 合法动作 / 执行 / 结算） | `src/Lonestar.Engine/BattleSession.cs` | ✅ |
| 战斗回合求解器（多进程并行 rollout） | `tools/Solve/SearchMode.cs` | ✅ |
| 主动技能动作（点击 / 二段选靶） | `src/Lonestar.Engine/SkillRuntime.cs`、`BattleSession.cs` | ✅ 已进搜索 |
| 战前"物品摆放"优化器 | `tools/Solve/LayoutSearch.cs`、`src/Lonestar.Engine/LayoutEditor.cs` | ✅ 第一版 |
| 战前能量核心配置优化 | `tools/Solve/CoreSearch.cs`、`LayoutEditor.SetCore` | ✅ 第一版 |
| 命令行工具 | `tools/Solve` | ✅ |
| 游戏内实时建议 HUD（MOD） | `src/SolverLogger/Advisor.cs` | ✅ 已编译部署，待游戏内实测 |

## 2. 为什么"精确"：无头模拟器

- `scripts/decompile.ps1` 把 `Assembly-CSharp.dll` 反编译到 `decompiled/`；
  `src/Lonestar.Game` 原样编译这份反编译代码（`decompiled/` 不做手改）；
- `src/Lonestar.UnityShim` 是自写的 Unity 运行时替身（GameObject/Transform/协程/Tween/
  Resources/序列化等），时间虚拟化，可无限快进；
- `src/Lonestar.Engine` 负责：恢复录制里的战前加密存档 → `SaveManager.Load()` →
  `WantedManager.EnterTheBattle()` → 用真实 UI 入口执行玩家操作；
- 游戏内 MOD 在每场战斗记录：`battle_begin`（含战前存档、牌库、敌人、define）、
  每次玩家操作、每个阶段的全状态快照（双方单位/槽位/能量/行对撞/手牌/牌库/敌方 AI 游标）。

**验证**（`tools/Replay`）：把游戏内录制的操作重新喂给无头引擎，逐快照对比：

| 录制 | 快照数 | 完全一致 |
|---|---|---|
| `20261018_185417_995_b0s0` | 36 | **36/36** |
| `20261018_185614_915_b0s1` | 43 | **43/43** |
| `20261018_185754_920_b0s2` | 45 | **45/45** |

RNG 也一致（牌库洗牌、抽牌、随机 key 全部复现），所以同一操作序列在同一录制上
**每次模拟结果完全相同**——这是所有搜索和验证的基础。

## 3. 求解器怎么工作

一场战斗里玩家的合法动作（来自 `docs/MECHANICS.md`）：
装填能量（手牌 × 单位）、上/下移动 1 行（每回合 1 次）、交换/瞬移、点击技能、
结束回合、战斗事件选项。当前搜索覆盖 **装填 + 移动 + 主动技能 + 结束回合**
（交换/瞬移与战斗事件选项仍待补，见局限）。

回合求解流程（`tools/Solve/SearchMode.cs`）：

1. 枚举候选摆放：手牌每张能量 → 可装填单位（按槽位颜色规则），得到全部"牌→单位"分配；
2. **预览排序**：每个候选在独立进程里重放到当前决策点、执行该摆放序列，读取游戏自己的
   行对撞预览（`BattleResultInfo` 的 `playerTotal/enemyTotal/result`）打分；
3. **技能变体**：若场上有可点击主动技能，对前几名方案追加 `click:<单位>`（一步技能）或
   `click:<单位>;target:<序号>`（二段技能，候选目标由技能自己的面板回调枚举）的变体；
4. **真实结算复核**：对预览前几名（含不同装牌顺序、移动变体、技能变体）再跑一次真实回合结算
   （结束回合 → 伤害/护盾/单位破坏 → 敌方回合 → 下一回合），按实际 HP 变化打分；
5. **续战评估**：对前几名用贪心策略把整场打完，按"是否胜利 / 剩余 HP / 回合数"给出最终排序；
6. 输出推荐动作序列、预测结果，并把结果写入 `out/solve/search_*.json`。

单进程 = 单次 roll-out（游戏全局单例无法进程内重置），搜索用多进程并行；
实测 8 核 16 线程下，一个决策点约 **10–40 秒**（取决于候选数与到该回合的重放长度）。

## 4. 实测结果（3 场录制，求解器自己打完整场）

| 战斗 | 人类玩家 | 求解器（`--play`，max-plans=64） |
|---|---|---|
| b0s0（敌 19 HP） | 3 回合，胜，剩 26 HP | 3 回合，胜，剩 26 HP |
| b0s1（敌 16 HP） | 4 回合，胜，剩 23 HP | 5 回合，胜，剩 23 HP |
| b0s2（敌 35 HP） | 4 回合，胜，剩 21 HP | 4 回合，胜，剩 21 HP |

结论：静态搜索 3/3 通关、剩余 HP 与人类持平（b0s1 多用 1 回合）。注意求解器的行动会改变
后续抽牌/手牌轨迹，所以"回合数"不是严格同盘对比；但"通关 + 剩余 HP"是可比的。

**后续审计（`docs/AUDIT.md`）**：对 b0s1 第 3 回合做全枚举后发现，静态搜索在自己的动作空间内已到回合最优
（gap=0），但动作空间缺了"技能当场造出的能量"；补上**动态状态搜索**（`--dynamic`）后：
b0s1 变为 4 回合/剩 26 HP（人类 4/23，严格更优），b0s0 2 回合/24 HP，b0s2 4 回合/20 HP。
动态搜索仍是有界 beam search，**不是可证明最优**。

补充验证（这 3 场录制里没有主动技能，所以用沙盘场景单独测技能/核心接口）：

- **二段技能**：用 `--force-units` 往船上放 `31837`（`Skill_ExchangeSlotTarget`），
  `--probe-skills` 正确枚举出 4 个可选目标；`click:0,0;target:1` 执行后，31837 的槽位
  从 `[1,1]` 变成交换后的 `[3,1]`，与技能 `Do()` 的预期一致。
- **技能入搜索**：同一场景 `--search` 生成并真实结算复核了 **20 个技能变体**（39 个 finalists）；
  最优解不含技能是因为该支持单位的换槽对空槽没有收益，属正常结果。
- **核心优化**：`--optimize-core` 对默认布局/敌人比较 11 种核心（贪心策略整场模拟），
  默认核心 4 回合/剩 23 HP，高威力核心 `[0,0,2,2,1]` 2 回合/剩 26 HP（贪心评估，仅作参考）。
- **MOD 加载**：实际启动游戏一次，`Player.log` 显示 `Loaded mod: LonestarSolver.Logger`、
  `[SolverLogger] advisor solver: ready`、`Patched`，无异常；战斗内 HUD 的显示效果仍待实测。

回合级示例（b0s0 第 2 回合，人类操作也在候选里）：

```
RECOMMENDED: move:-1;load:2@2,2;load:0@2,2;load:1@0,1;load:0@1,1;load:0@1,1
  projected: VICTORY with greedy follow-up, 2 turns, playerHp=26
  this turn: battle continues  enemyHpLost=12 playerHpLost=0
```

## 5. 使用方法

### 5.1 构建

```powershell
cd F:\LonestarPlanner\solver
dotnet build tools\Solve\Solve.csproj -c Release
dotnet build tools\Replay\Replay.csproj -c Release    # 可选：复现验证
```

### 5.2 战斗求解（单回合）

```powershell
# 查看某个回合的决策状态与动作空间
dotnet run --project tools\Solve -c Release -- <battle.jsonl> --turn 2 --dry-run --bench

# 求解推荐动作（并行度 14，最多 256 个候选，复核前 8 名）
dotnet run --project tools\Solve -c Release -- <battle.jsonl> --turn 2 --search --jobs 14 --max-plans 256 --verify 8

# 从头用求解器打完一整场（验证/演示）
dotnet run --project tools\Solve -c Release -- <battle.jsonl> --play --jobs 14 --max-plans 64 --verify 4
```

### 5.3 战前"物品摆放"优化

```powershell
# 查看当前船体格子、可用单位与能量核心
dotnet run --project tools\Solve -c Release -- <battle.jsonl> --layout-info

# 枚举摆放（行分配 + 列变体），用整场贪心模拟排名
dotnet run --project tools\Solve -c Release -- <battle.jsonl> --optimize-layout --jobs 14 --refine-top 4

# 对指定布局单独打一场（验证某个布局）
dotnet run --project tools\Solve -c Release -- <battle.jsonl> --layout "0,0,0;0,1,1;1,1,2;0,2,3" --greedy-play --json
```

布局格式：`x,y,单位序号;x,y,单位序号;...`，单位序号来自 `--layout-info` 的 `available` 列表；
生成/校验时会检查格子可用性、每格 1 个单位、总占用 ≤ `shipLoadLimit`。

### 5.4 战前能量核心（牌库）配置优化

游戏里核心（威力 1–5 的张数、白/蓝/紫张数）由船体固定，只有 run 内事件/奖励能改；
求解器提供"如果可选，哪种核心最好"的评估能力：

```powershell
# 对当前布局/敌人比较候选核心（当前、低威力、高威力、全3、均匀、各颜色偏重等），整场模拟排名
dotnet run --project tools\Solve -c Release -- <battle.jsonl> --optimize-core --jobs 14

# 指定核心后模拟/求解
dotnet run --project tools\Solve -c Release -- <battle.jsonl> --core "0,0,2,2,1;6,0,0" --greedy-play --json
```

`--core` 格式：`<威力1..5张数>;<白,蓝,紫张数>`，例如默认核心是 `1,1,1,1,1;3,2,1`。

### 5.5 技能动作与调试

```powershell
# 列出当前可点击技能及其二段选靶候选（会点击两段技能并在不选靶的情况下关闭面板）
dotnet run --project tools\Solve -c Release -- <battle.jsonl> --turn 1 --dry-run --probe-skills --json

# 手动执行技能：click:<x,y> + target:<序号>
dotnet run --project tools\Solve -c Release -- <battle.jsonl> --turn 1 --force-units "0,0,31837,1" --actions "click:0,0;target:1" --json
```

`--force-units "x,y,unitId,lv;..."` 用于测试/沙盘：直接往船体格放指定单位（绕过背包限制）。

### 5.6 游戏内实时建议 MOD（HUD）

```powershell
# 构建求解器 + 部署 MOD 并写 advisor.json（默认开启）
powershell -ExecutionPolicy Bypass -File scripts\deploy_advisor.ps1 -Jobs 6 -MaxPlans 32 -Verify 3

# 关闭
powershell -ExecutionPolicy Bypass -File scripts\deploy_advisor.ps1 -Disable
```

MOD 部署在 `%USERPROFILE%\AppData\LocalLow\Shuxi\LONESTAR\Mods\Dev\SolverLogger`。
进入战斗后，每个玩家回合开始时 MOD 会异步调用 `Solve --search`，求解完成后在屏幕左上角
显示"推荐动作"（例如 `装能量[3/紫]→(2,0)`）和预测结果；求解期间显示进度。
`advisor.json` 里可调 `jobs/maxPlans/verify/timeoutSeconds`，追求快就把 `maxPlans` 调小
（如 16），追求质量就调大（128+）。

## 6. 关键机制（详见 `docs/MECHANICS.md`）

- 伤害 = 行能量差 × 伤害倍率 × 受击方倍率，再经过 `DamagedReplaces`（取小）/`DamageReplaces`（取大）；
  护盾按行逐个吸收，因此**行的结算顺序**会影响实际掉血（引擎内已原样复现）；
- 单位输出 =（槽位能量和 + PowerExtra）× 倍率 + PowerAdded，只有 Cannon（powerable）计入；
- 行配对是纯几何：玩家行 L 撞敌方行 L + (玩家偏移 − 敌方偏移)，移动只影响当回合，回合末归零；
- 装填合法性：槽位按顺序找第一个空槽，且能量最高颜色 ≥ 槽位颜色需求（白1/蓝2/紫3），
  不能被 disturb；失败无副作用（求解器可以放心尝试）；
- 敌方 AI 完全确定性（数据 + 计数器），战斗内 RNG 是"种子哈希 + 计数"的纯函数。

## 7. 战前摆放规则（详见 `docs/PLACEMENT.md`）

- 战前整备界面可自由拖拽单位到船体格，免费不限次数；限制：`coors` 内且非 `None` 格、
  每格 1 个、总 `EquiptLimit ≤ shipLoadLimit`（默认 6）；
- 3×3 船体（HongDong，录制样本 4 个单位）摆放空间 `P(9,k)`：k=4 → 3024，k=5 → 15120；
- 能量核心（威力 1–5 各 1 张、白3/蓝2/紫1）在战前不可自由改，是常量输入；求解器支持
  用 `--core` 覆盖它来做"如果可选哪种核心更好"的评估（`--optimize-core`）；
- 单位/船体/敌人的完整字段与统计见 `docs/PLACEMENT.md`。

## 8. 目录结构

```
decompiled/            # ILSpy 反编译的游戏代码（不手改）
data/export/           # 游戏内 MOD 导出的 Resources 资源（prefab/asset/csv）
src/Lonestar.Rng/      # Unity Random / 游戏 RNG 复现
src/Lonestar.UnityShim/# 无头 Unity 运行时替身
src/Lonestar.Game/     # decompiled 代码编译产物（net10）
src/Lonestar.Engine/   # 无头启动、战斗会话、技能选靶、布局/核心编辑、录制回放
src/SolverLogger/      # 游戏内 MOD：录制器 + 资源导出 + 实时建议 HUD
tools/Replay/          # 录制回放 + 逐快照 diff（验证模拟器）
tools/Solve/           # 求解器 CLI（回合搜索 / 整场对局 / 布局优化 / 核心优化）
tools/ShimGen/         # 生成 Unity shim 的辅助工具
scripts/               # 反编译、MOD 部署、数据分析脚本
docs/MECHANICS.md      # 战斗机制规格（1028 行，带代码行号）
docs/PLACEMENT.md      # 战前摆放 / 单位数据模型 / 优化器形式化
docs/AUDIT.md          # 最优性审计、动态搜索、与人类的整场对比
data/recordings/       # 4 份游戏内战斗录制（原始证据）
out/                   # 运行产物（回放、求解报告、布局/核心报告）
```

## 9. 局限与下一步

现在已经能"精确模拟 + 搜索装填/移动/技能 + 给出建议 + 战前布局/核心评估"，但离"理论最优"还有距离：

1. **交换/瞬移与战斗事件选项未进搜索**：`act_exchange`、`act_choice` 仍缺入口
   （`docs/MECHANICS.md` §3.3/§3.6/§12 有推荐调用序列；交换的 `TeleportEnd` 依赖鼠标悬停，
   需要用 `StartTeleport→TeleportPreview→DoTeleportAtEnd` 绕开）。
2. **技能的搜索方式仍是"前缀变体"**：只尝试"先点技能再装填"，没有枚举"装填后再点技能"或
   "多个技能组合"；二段选靶的候选来自技能面板回调（已能枚举），但没有做多技能配对搜索。
3. **回合内新生成的能量**：静态搜索的候选只来自"回合开始的手牌"，会漏"先造牌再装"的线路；
   已用 `--dynamic`（每步重读手牌的状态 beam search）修复，但仍是 beam+depth 有界的启发式，不是穷举。
4. **搜索规模**：候选枚举受 `max-plans` 截断，移动/技能变体只对前几名做；更大预算/更好的剪枝
   （按行能量上界、状态哈希）能更接近最优。
5. **速度**：每进程 roll-out 需要 boot（~0.3s）+ 重放前缀；做到实时建议需要进程内状态快照/恢复
   或 ALC 多实例。目前 HUD 建议延迟约 10–40 秒。
6. **战前布局/核心优化**用贪心策略评分（快），不是用求解器逐回合重搜；对"布局/核心差异是否
   影响胜负"足够敏感，但排序可能与强策略不同。可以对前几名布局用 `--play` 复评。
7. **完整 run 层策略**（商店/事件/路线/买什么）尚未建模；目前聚焦单场战斗与战前摆放/核心。

下一步优先级建议：交换/事件入口 → 技能与装填的顺序/组合搜索 → 造牌动态动作空间 →
进程内快照提速 → 布局/核心优化用强策略复评 → run 层（商店/事件）决策。

