# 队友 AI 加强 MOD

在游戏自带的队友 AI 上替换几个决策点，其余行为保持原版。AI 托管 MOD 控制主角时同样生效。
尊重游戏原生的「队友自动化设置」：攻击开关、是否用枪、是否使用医疗用品、是否拾取物品、移动方式等。

## 加强了什么

| | 原版 | 加强后 |
|---|---|---|
| 撤退 | 血再少也照样冲上去近战 | HP 低于 40% 不主动近战，撤到僵尸下回合够不着、离你近的地方；站在安全格时用枪补几枪（偏向打腿拖慢追兵） |
| 逃跑 | 挪到最近的「暂时安全」格 | 往离僵尸最远、离你近的方向跑 |
| 打哪 | 永远打头 | 用游戏自己的命中率 / 伤害 / 暴击公式算每个部位的期望值再挑（暴击只有打头才有、伤害 ×5；打头能直接打死时额外加分；打腿降低僵尸行动力，缩小它的攻击范围） |
| 武器 | 只看面板伤害 | 按「这回合能打出的期望伤害」挑：算上命中率、攻击 AP、要不要先走过去 |
| 开枪 | 有枪就开 | 开枪会把附近最多 40 只僵尸引到开枪的位置，还费子弹。近战（或弓）能打出枪一半以上的伤害时不开枪；撤退补射也只用不出声的武器 |
| 目标 | 最近的 | 先打正在威胁你或其他队友的，其中能一下打死的优先，再按距离；最近那只附近 2 格内有头部血更少的就先打它（集火，几个队友会盯同一只） |
| 近战接近 | 走过去后剩不到 5 AP 就停在僵尸攻击范围外等它过来；但路线不按 AP 截断，远了就原地不动 | 保留「等它过来」（让僵尸把行动力花在走路上），路远时按 AP 走一段；僵尸正威胁你或其他队友、或这一下多半能打死（≥60%）时直接冲上去 |
| 远程站位 | 沿路找第一个能开火的格子，不管僵尸够不够得着 | 在这回合走得到、走完还能开火的格子里，优先挑僵尸够不着的 |
| 用药 | HP 低于 5 才吃药，挑最强的；强度超标时会给玩家弹「会昏厥」确认框 | HP 低于 50% 就治疗，药物强度不超过头痛线（9）；HP 低于 25% 时放宽，但永远不到昏厥线（20） |
| 治疗别人 | 不会 | 给相邻或走得到的你和其他队友包扎、用药；医生会用「急救」。不会为此走进僵尸攻击范围 |
| 护卫 | 有僵尸靠近你时照样去搜刮、修东西 | 僵尸进入你周围 8 格内时，队友放下搜刮、升级、清障、修车、种田，先回到你身边 |
| 回合结束 | 站在僵尸旁边结束回合 | 还站在僵尸攻击范围内、又有剩余 AP 时，退到安全格 |
| 捡东西 | 默认只捡价值「高」以上、重量 1.5 以下的（弹药每发价值 2、绷带 13、药 13，都不捡） | 另外捡用得上的：队伍里有对应武器的弹药和箭、药品绷带、比手上明显更好的近战武器 |
| 你丢的东西 | 值钱的会被队友捡回去 | 你和队友拿过的东西放在地上就不捡 |
| 远处的东西 | 一回合走不到就站着不动；只比较最近 6 个格子 | 先走一段，下回合接着捡；比较最近 12 个格子；不进僵尸攻击范围 |

「安全」用的是游戏自己算的僵尸攻击范围（`attack_cells`：僵尸下回合走得到再攻击得到的格子）。

## 配置

编辑 `companion_ai.py` 开头的常量，改完重启游戏生效：

- `RETREAT_HP_RATIO`：撤退线，默认 `0.40`
- `HEAL_HP_RATIO` / `CRITICAL_HP_RATIO`：用回血药 / 濒危的 HP 比例，默认 `0.50` / `0.25`
- `MED_STRENGTH_SOFT` / `MED_STRENGTH_HARD`：平时 / 濒危时允许的药物强度上限，默认 `9` / `19`
- `HEAL_OTHERS`、`HEAL_OTHERS_MAX_MOVE_AP`：是否给别人治疗、最多走多少 AP 过去
- `GUARD`、`GUARD_RANGE`、`GUARD_STAY_WITHIN`：护卫开关、触发距离、离你多远才往回走
- `SAFE_STEP`：回合结束前避险
- `BODY_WEIGHT` / `LEGS_WEIGHT` / `LEGS_WEIGHT_RETREATING`：选部位时躯干、腿相对头部的权重
- `KILL_BONUS`：打头能直接打死时加的分（按伤害点数），默认 `3.0`
- `FOCUS_EXTRA_STEPS`：集火范围，最近那只僵尸再远几格以内找血最少的，`0` 关闭
- `CHARGE_TO_SAVE`、`CHARGE_KILL_CHANCE`：什么时候不等僵尸过来、直接冲上去
- `SMART_FIRING_POSITION`：远程武器找僵尸够不着的射击位置
- `PARTING_SHOTS`：撤退后最多补几枪
- `QUIET_COMBAT`、`QUIET_MIN_RATIO`：安静作战开关；不出声的武器达到枪的多少伤害就不开枪，默认 `0.5`
- `PICK_UP_NEEDED`：额外捡用得上的东西；`PICK_UP_AMMO` / `PICK_UP_MEDS` / `PICK_UP_BETTER_MELEE` 分项开关
- `BETTER_MELEE_RATIO`：地上的近战武器比手上最好的强多少倍才捡，默认 `1.25`
- `PICK_UP_NAMES`：额外总要捡的物品内部名，例如 `("tape", "nails")`
- `PICK_UP_MAX_WEIGHT`：单件比这重的不额外捡，默认 `3.0`
- `KEEP_DROPPED`：不捡回你们自己丢下的东西
- `SEARCH_APPROACH`、`SEARCH_CANDIDATES`：走不到时先走一段；每次比较多少个目标格
- `LOG_DECISIONS`：把每次决策写进 `companion_ai.log`

## 安装 / 卸载

- 创意工坊：<https://steamcommunity.com/sharedfiles/filedetails/?id=3826759381>，订阅后在下载目录双击 `install.bat`（不需要 Python）。
- 从源码：双击本文件夹的 `install.bat`（需要 Python 3.12，脚本会自动用 uv 或 py 启动器）。
- 卸载：双击 `uninstall.bat`。如果 AI 托管等其他 MOD 还在，只会停用本 MOD（生成 `DISABLED` 文件），
  然后删除本文件夹即可；否则会还原 `base_library.zip`。
- **游戏更新后**：Steam 会覆盖 `base_library.zip`，MOD 会失效，重新运行一次 `install.bat` 即可。
- 为了认出「你们丢下的东西」，MOD 会给你和队友拿过的物品加一个 `_companion_ai_owned` 记号，随存档保存；
  卸载后不起作用，存档照常可用。

## 原理

和 AI 托管、队伍背包共享共用 `base_library.zip` 里的 V2 MOD 加载器（见上一级 README）。
加载器在 `actions.compaction` / `core.creature.character` 加载后调用本 MOD，替换这些函数：

- `compaction.get_closest_target` → 选目标
- `compaction.get_weapon` → 选武器（只在本 MOD 指挥的攻击里；其他地方仍用原版）
- `compaction.move_and_attack` → 标记本次攻击由 MOD 指挥；打得到时照原版攻击，打不到时按上面的「近战接近」「远程站位」走位
- `character.process_attack`（模块全局）→ 本 MOD 指挥的攻击按期望值换部位；玩家自己的攻击不受影响
- `compaction.handle_attack` → 低 HP 时撤退并补枪，否则走原版
- `compaction.flee`、`compaction.handle_enemy_encounter` → 更聪明的撤退
- `compaction.handle_medication` → 自己和别人的治疗
- `compaction.handle_food_consumption` → 先做回合结束前避险，再走原版吃东西
- `compaction.get_filtered_pickup_items_for_cell` → 原版按价值 / 重量设置筛出的东西，去掉你们丢的，加上用得上的
- `compaction.handle_search_or_pickup` → 流程照原版（走到最近的目标格，捡东西、搜家具 / 尸体，循环到 AP 不够），加上上面三处改动
- 搜刮、升级、清障、修车、种田五个任务 → 套一层护卫判断

每个替换都有兜底：出错时把错误写进 `companion_ai.log`，改用原版逻辑。

## 排错

正常启动后 `companion_ai.log` 里应有：

```
队友 AI 加强 MOD 已加载
已接管队友攻击部位的选择
已替换队友 AI：目标 / 武器 / 部位选择、安静作战、撤退、逃跑、用药与互相治疗、护卫、拾取、回合结束前避险
```

之后每次决策一行，例如「XX 对 僵尸 选用 斧头（预计打 头部，本回合期望 5.8，不开枪以免引来更多僵尸）」「XX 撤退（HP 低于 40%）」「XX 捡起 弹药 ×3、绷带」。
