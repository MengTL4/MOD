# 战术求解器 | Tactics Solver

《寰宇孤星 / LONESTAR》的战斗求解 mod（内部 ID `LonestarPlanner`，[创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3825028801)）：在真实游戏引擎里把战斗提前打一遍，告诉你每一步怎么走、船怎么摆。

一个仓库，两部分：

| 目录 | 是什么 | 说明 |
|---|---|---|
| [`mod/`](mod) | 游戏内 mod（netstandard2.1） | 战斗面板、棋盘标注、自动执行、船坞摆位；和求解服务通信。见 [mod/README.md](mod/README.md) |
| [`solver/`](solver) | 求解器（.NET 10） | 无头引擎（游戏战斗代码 + 假 Unity 层）+ Brain 搜索服务 + 回归测试。见 [solver/README.md](solver/README.md) |

两部分是绑在一起的：mod 直接编译 `solver/src/SolverLogger` 里的录制器（游戏内和无头端的快照逻辑必须一致），
打包时把 `solver/tools/Brain` 发布成自带运行时的程序放进 mod。

## 常用命令

```powershell
cd F:\LonestarPlanner

# 打包 mod（含求解器）并部署到游戏的 Dev mod 目录（先关游戏）
pwsh mod/tools/package.ps1 -Deploy

# 求解器回归：14 场回放 + 快照恢复 + 全部敌人冒烟 + 自对弈（约 10 分钟；SKIP_SMOKE=1 跳过冒烟）
bash solver/scripts/regress.sh

# mod 的核心模型单元测试
dotnet run --project mod/tests/Planner.Tests -c Release

# mod 的通信代码对真实求解服务的联调（见 solver/tools/ClientTest）
dotnet run --project solver/tools/ClientTest -c Release -- live <mod目录> <数据目录> <战斗文件> <开局文件>
```

## 不进 git 的东西（本机生成）

这些是游戏自己的代码和资源，只在本机用，**不要公开**：

| 路径 | 怎么生成 |
|---|---|
| `solver/decompiled/` | `solver/scripts/decompile.ps1`（ilspycmd 反编译游戏程序集） |
| `solver/src/Lonestar.Game/Rewritten/` | `dotnet run --project solver/tools/ShimGen -- rewrite` |
| `solver/data/export/` | 游戏内 SolverLogger 的资源导出（见 solver/README.md） |

注意：合并前的提交历史里还带着 `data/export` 和 `Rewritten/`（当时一起提交的），如果要把仓库推到公开的地方，
先把这两个目录从历史里清掉。

公开副本：[MengTL4/MOD 的 `LONESTAR/LonestarPlanner/`](https://github.com/MengTL4/MOD/tree/main/LONESTAR/LonestarPlanner)
只放当前代码的快照（不带这里的提交历史），并且不含 `solver/data/recordings/` 和 `solver/data/loadout/`
（里面是带存档的战斗录像，只在本机）。

游戏更新后：重新反编译 → `ShimGen rewrite` → 跑回归 → 打包上传。

## 历史

2026-10-23 由两个仓库合并而来，两边的提交历史都保留：`F:\LonestarSolver`（求解器，现 `solver/`）和
`LONESTAR\lonestar-planner`（mod，现 `mod/`）。
