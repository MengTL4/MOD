# -*- coding: utf-8 -*-
"""
Terminus: Zombie Survivors - 队友 AI 加强 MOD

在游戏自带的队友 AI（actions/compaction.py）上替换几个决策点，其余行为保持原版：
  - 撤退：HP 低于阈值时不再冲上去打，撤到僵尸下回合够不着、离你近的地方；
    撤退后如果站在安全格、手里有枪，顺手打腿拖慢追兵
  - 逃跑：原版只挪到最近的「暂时安全」格，这里改成往离僵尸最远、离你近的方向
  - 选部位：按游戏自己的命中率 / 伤害 / 暴击公式算每个部位的期望值再挑
    （原版永远打头；暴击只有打头才有、伤害 ×5；打腿会降低僵尸行动力，缩小它的攻击范围）
  - 选武器：按「这回合能打出的期望伤害」挑武器，不再只看面板伤害
  - 选目标：先打正在威胁你或其他队友的僵尸，其中能一下打死的优先，再按距离；
    附近有头部血更少的就先打它（集火）
  - 接近：近战够不着时，原版停在僵尸攻击范围外等它过来，但路远时原地不动；这里按 AP 走一段。
    僵尸正威胁队伍、或这一下多半能打死时直接冲上去。远程武器找僵尸够不着的射击位置
  - 用药：按 HP 百分比提前治疗；药物强度不超过头痛线（濒危时不超过昏厥线）
    （原版 HP 低于 5 才吃药，强度超标时还会给玩家弹「会昏厥」的确认框）
  - 互相治疗：给相邻或走得到的你和其他队友包扎、用药；医生会用「急救」
  - 护卫：附近有僵尸靠近你时，队友不再跑去搜刮、修东西、种田，先回到你身边
  - 回合结束前还站在僵尸攻击范围内、又有剩余 AP 时，退到安全格
  - 拾取：地上用得上的东西（弹药、药品绷带、更好的近战武器）不论价值都捡；
    不捡回你们自己丢下的东西；一回合走不到的先走过去，下回合接着捡；不进僵尸攻击范围
    （原版默认只捡价值「高」以上的东西，弹药、绷带、药都不够格；够不着时原地不动）
  - 安静作战：开枪会把附近最多 40 只僵尸引过来，近战够用时不开枪

同样作用于 AI 托管 MOD 控制的主角（它复用的就是这些队友 AI 函数）。
尊重游戏原生的「队友自动化设置」（攻击开关、是否用枪、是否使用医疗用品等）。

本文件由 base_library.zip 中的 MOD 加载器在游戏启动时加载。每个替换都有兜底：
出错时写入同目录的 companion_ai.log 并改用原版逻辑，不会让游戏崩溃或卡住回合。
"""

import math
import os
import threading
import time
import traceback

# ----------------------------------------------------------------------------
# 可调整的配置
# ----------------------------------------------------------------------------

# HP 低于 最大 HP × 这个比例时撤退，不主动近战
RETREAT_HP_RATIO = 0.40

# 撤退只针对这么多格以内的僵尸（太远的不算威胁）
RETREAT_TRIGGER_RANGE = 12

# HP 低于 最大 HP × 这个比例时用回血药（自己和队友）
HEAL_HP_RATIO = 0.50

# HP 低于 最大 HP × 这个比例算濒危：用药时允许药物强度超过头痛线（但永远不到昏厥线）
CRITICAL_HP_RATIO = 0.25

# 药物强度：超过 9 会头痛，达到 20 会昏厥（游戏规则）
MED_STRENGTH_SOFT = 9
MED_STRENGTH_HARD = 19

# 给你和其他队友治疗
HEAL_OTHERS = True

# 去给队友治疗时最多愿意走多少 AP 的路
HEAL_OTHERS_MAX_MOVE_AP = 6

# 护卫：有僵尸进入你周围这么多格以内时，队友放下搜刮等杂事回到你身边
GUARD = True
GUARD_RANGE = 8
GUARD_STAY_WITHIN = 2       # 离你超过这么多格才往回走

# 回合结束前还站在僵尸攻击范围内、又有剩余 AP 时，退到安全格
SAFE_STEP = True

# 选部位时非致命部位的权重（头部伤害直接算击杀进度，权重为 1）
BODY_WEIGHT = 0.3           # 打躯干：降低僵尸攻击力
LEGS_WEIGHT = 0.5           # 打腿：降低僵尸行动力（够不着人）
LEGS_WEIGHT_RETREATING = 1.2  # 自己在撤退时更想打腿拖慢追兵
KILL_BONUS = 3.0            # 打头这一下能打死时额外加的分（按伤害点数算；普通僵尸头部 1~5 血）

# 集火：离最近的僵尸多走这么几格以内，先打头部剩余血量最少的（0 = 关闭）
FOCUS_EXTRA_STEPS = 2

# 近战够不着、这回合走过去后剩不到 5 AP 时，原版会停在僵尸攻击范围外等它过来（这是对的：
# 让僵尸把行动力花在走路上，下回合满 AP 先手）。以下情况改成直接冲上去：
CHARGE_TO_SAVE = True         # 这只僵尸正威胁你或其他队友
CHARGE_KILL_CHANCE = 0.6      # 走过去后这一下打死它的概率至少这么高

# 远程武器打不到时，在这回合走得到的格子里找能开火、僵尸够不着的位置（原版沿路找第一个能开火的格子）
SMART_FIRING_POSITION = True

# 撤退时最多补几枪（安静作战打开时只用不出声的武器，比如弓）
PARTING_SHOTS = 2

# 安静作战：开枪会把附近最多 40 只僵尸引过来，还费子弹。
# 不出声的武器（近战、弓、弹弓）的期望伤害达到枪的这个比例时，用不出声的
QUIET_COMBAT = True
QUIET_MIN_RATIO = 0.5

# 拾取地上的东西（需要原版「队友自动化设置」里的「拾取物品」开着）。
# 原版只捡价值、重量符合设置的东西，默认价值「高」以上，弹药、绷带、药都不够格。
# 打开后，下面这些用得上的东西不论价值都捡：
PICK_UP_NEEDED = True
PICK_UP_AMMO = True           # 队伍里有对应武器的弹药、箭
PICK_UP_MEDS = True           # 药品、绷带
PICK_UP_BETTER_MELEE = True   # 比这个队友手上最好的近战武器还好这么多倍的近战武器
BETTER_MELEE_RATIO = 1.25
PICK_UP_NAMES = ()            # 额外总要捡的物品内部名，例如 ("tape", "nails")
PICK_UP_MAX_WEIGHT = 3.0      # 单件比这重的不额外捡

# 不捡回你们自己丢在地上的东西（包括原版本来会捡的值钱货）
KEEP_DROPPED = True

# 去捡远处的东西时一回合走不到，先走一段，下回合接着（原版原地不动）
SEARCH_APPROACH = True
SEARCH_CANDIDATES = 12        # 每次比较离得最近的多少个目标格的路程（原版 6 个）

# 把每次决策写进日志（选了什么目标 / 武器 / 部位，为什么撤退等）
LOG_DECISIONS = True

# ----------------------------------------------------------------------------

MOD_DIR = os.path.dirname(os.path.abspath(__file__))
LOG_PATH = os.path.join(MOD_DIR, "companion_ai.log")

PATCH_TARGETS = (
    "core.creature.character",
    "actions.compaction",
)

PARTS = ("head", "body", "legs")
PART_NAMES = {"head": "头部", "body": "躯干", "legs": "腿"}

# 状态异常 → 对症的医疗用品（与原版 handle_medication 一致）
AILMENT_ITEMS = (
    ("bleeding", ("bandage", "makeshift_bandage")),
    ("sprain", ("compression_bandage",)),
    ("disease", ("antibiotic",)),
    ("infection", ("antibiotic",)),
    ("burn", ("ointment",)),
)

# 这些原版任务会让队友走开去干杂事；护卫时跳过
GUARDED_TASKS = (
    "handle_search_or_pickup",
    "handle_upgrade",
    "handle_clearing_debris",
    "handle_vehicle_repairs",
    "handle_plant_care",
)

# 在你和队友拿过的物品上做的记号（随存档保存，不影响原版逻辑）：带记号的东西在地上就是你们丢的
OWNED_ATTR = "_companion_ai_owned"


class _State:
    local = threading.local()   # attacker / target：本线程正在由本 MOD 指挥的一次攻击
    orig = {}                   # 被替换的原版函数
    ca = None                   # actions.compaction 模块
    logged = set()


STATE = _State()


# ----------------------------------------------------------------------------
# 工具函数
# ----------------------------------------------------------------------------

def log(msg):
    try:
        with open(LOG_PATH, "a", encoding="utf-8") as f:
            f.write(time.strftime("[%H:%M:%S] ") + msg + "\n")
    except OSError:
        pass


def log_exc(where, once=False):
    if once:
        if where in STATE.logged:
            return
        STATE.logged.add(where)
    log("%s 出错，改用原版逻辑:\n%s" % (where, traceback.format_exc()))


def decision(msg):
    if LOG_DECISIONS:
        log(msg)


def dt():
    from data import dt as module
    return module


def name_of(obj):
    name = getattr(obj, "localized_name", None)
    if callable(name):
        try:
            name = name()
        except Exception:
            name = None
    if name:
        return name
    try:
        from system.lang import tr
        return tr(obj.name)
    except Exception:
        return str(getattr(obj, "name", obj))


def ailment_name(ailment):
    try:
        from system.lang import tr
        return tr("status_ailment_" + ailment)
    except Exception:
        return ailment


def settings(char):
    return getattr(char, "automation_settings", None) or {}


def dist(cell_a, cell_b):
    return STATE.ca.get_cell_dist(cell_a, cell_b)


def hp_ratio(char):
    max_hp = getattr(char, "max_hp", 0) or 0
    return char.hp / max_hp if max_hp > 0 else 1.0


def is_low_hp(char):
    return hp_ratio(char) < RETREAT_HP_RATIO


def threats_of(char):
    """看得见或听得见的僵尸（和原版选攻击目标的条件一致）。"""
    return [z for z in char.loc.zombies if z.cell.just_seen or z.cell.can_hear]


def danger_cells(threats):
    cells = set()
    for z in threats:
        cells.update(getattr(z, "attack_cells", ()) or ())
    return cells


def party(char):
    """和 char 在同一地点、活着的主角和队友（含 char 自己）。"""
    game = dt().game
    members = [game.player, *game.companions]
    return [m for m in members if getattr(m, "is_alive", True) and m.loc is char.loc]


def player_of(char):
    """char 所在地点的主角（char 自己是主角时返回 None）。"""
    player = dt().game.player
    if player is char or player.loc is not char.loc:
        return None
    return player


def wait_animation(char):
    scene = dt().scene
    for _ in range(100):
        busy = (char.spr is not None and char.spr.is_animating) or scene.effect.is_animating
        if not busy:
            return
        time.sleep(0.1)


# ----------------------------------------------------------------------------
# 战斗：部位 / 武器 / 目标
# ----------------------------------------------------------------------------

def part_values(char, weapon, ammo, zombie):
    """{部位: (期望值, 命中率, 平均伤害)}，期望值已乘部位权重；打不了的部位不出现。"""
    legs_weight = LEGS_WEIGHT_RETREATING if is_low_hp(char) else LEGS_WEIGHT
    weights = {"head": 1.0, "body": BODY_WEIGHT, "legs": legs_weight}
    armor = getattr(zombie, "armor", None) or {}
    result = {}
    for part in PARTS:
        hp = zombie.hp.get(part, 0)
        if hp <= 0:
            continue
        acc = max(0.0, min(100.0, char.get_accuracy(weapon, ammo, zombie, part))) / 100
        low, high = char.get_damage(weapon, ammo, zombie, part)
        avg = (low + high) / 2
        remaining = hp + armor.get(part, 0)
        if part == "head":
            # 超出剩余血量的伤害不算；头部这一下能打死时另加 KILL_BONUS（打死就不用再挨它的攻击）
            def head_value(dmg):
                return min(dmg, remaining) + (KILL_BONUS if dmg >= remaining else 0)
            crit = 0.0
            if char.entity_type != "npc":
                crit = max(0.0, min(100.0, char.get_critical_hit_chance(weapon, ammo, zombie, part))) / 100
            # 游戏先判暴击（×5 伤害），没暴击再按命中率判命中
            ev = crit * head_value(avg * 5) + (1 - crit) * acc * head_value(avg)
        else:
            ev = acc * min(avg, remaining)
        result[part] = (ev * weights[part], acc, avg)
    return result


def best_part(char, weapon, ammo, zombie):
    values = part_values(char, weapon, ammo, zombie)
    if not values:
        return "head", (0.0, 0.0, 0.0)
    part = max(values, key=lambda p: values[p][0])
    return part, values[part]


def ammo_for(char, weapon):
    if weapon.atktype != "ranged":
        return None
    return next((a for a in weapon.ammos if char.get_nearby_items(a)), None)


def candidate_weapons(char):
    """与原版 get_weapon 相同的筛选：有伤害的物品；关掉「用枪」时排除枪；远程武器要有弹药。"""
    weapons = [i for i in char.items if hasattr(i, "damage")]
    if not settings(char).get("attack_gun"):
        weapons = [i for i in weapons if i.subcategory != "firearm"]
    return [w for w in weapons if w.atktype != "ranged" or any(char.has_item(a) for a in w.ammos)]


def weapon_value(char, weapon, zombie):
    """这把武器这回合对 zombie 大约能打出的期望伤害（已按部位权重）。"""
    from core.creature.combat import get_attack_ap
    ca = STATE.ca
    ammo = ammo_for(char, weapon)
    if weapon.atktype == "ranged" and ammo is None:
        return -1.0, None
    part, (ev, _, _) = best_part(char, weapon, ammo, zombie)
    attack_ap = 1 + get_attack_ap(char, weapon, zombie, part)
    reach = weapon.reach if weapon.atktype == "melee" else weapon.range
    d = ca.cy.caldist([*char.cell.coord, *zombie.cell.coord])
    if d <= reach and ca.can_attack_with_weapon(weapon, char.cell, zombie.cell):
        move_ap = 0
    else:
        pathdic = ca.get_pathdic_comp(char, zombie.cell)
        if not pathdic:
            return -1.0, part
        move_ap = pathdic["ap"] if weapon.atktype == "melee" else pathdic["ap"] * 0.5
    # 只要还剩 1 AP 就能出手（AP 扣到 0 为止），所以按向上取整算次数；这回合打不到也算在往前推进
    spare = char.ap - move_ap
    attacks = math.ceil(spare / attack_ap) if spare >= 1 else 0.25
    condition = getattr(weapon, "condition", 0) or 0
    return ev * attacks + condition * 1e-4, part


def is_noisy(weapon):
    """开火会出声（游戏里会把附近最多 40 只僵尸引到你的位置）。"""
    try:
        return (getattr(weapon, "noise", 0) or 0) > 0
    except Exception:
        return False


def pick_weapon(char, zombie):
    scored = []
    for weapon in candidate_weapons(char):
        try:
            value, part = weapon_value(char, weapon, zombie)
        except Exception:
            log_exc("评估武器 %s" % getattr(weapon, "name", weapon), once=True)
            continue
        if value >= 0:
            scored.append((value, weapon, part))
    if not scored:
        return None
    value, best, part = max(scored, key=lambda s: s[0])
    note = ""
    if QUIET_COMBAT and is_noisy(best):
        quiet = [s for s in scored if not is_noisy(s[1])]
        if quiet:
            q = max(quiet, key=lambda s: s[0])
            if q[0] > 0 and q[0] >= value * QUIET_MIN_RATIO:
                value, best, part = q
                note = "，不开枪以免引来更多僵尸"
    decision("%s 对 %s 选用 %s（预计打 %s，本回合期望 %.1f%s）" % (
        name_of(char), name_of(zombie), name_of(best), PART_NAMES.get(part, part), value, note))
    return best


def threatens(zombie, cells):
    attack_cells = getattr(zombie, "attack_cells", None) or ()
    return any(c in attack_cells for c in cells)


def killable_now(char, zombie):
    weapon = STATE.orig["get_weapon"](char)
    if not weapon:
        return False
    ammo = ammo_for(char, weapon)
    if weapon.atktype == "ranged" and ammo is None:
        return False
    low, high = char.get_damage(weapon, ammo, zombie, "head")
    return (low + high) / 2 >= head_remaining(zombie)


def head_remaining(zombie):
    return zombie.hp.get("head", 0) + (getattr(zombie, "armor", None) or {}).get("head", 0)


def focus_target(obj, closest_target, group):
    """集火：最近目标附近 FOCUS_EXTRA_STEPS 格内，换成头部剩余血量最少的（受过伤的、最快能打死的）。
    几个队友算出来的是同一只，自然会集中火力。"""
    if FOCUS_EXTRA_STEPS <= 0 or closest_target.entity_type != "zombie":
        return closest_target
    limit = dist(obj.cell, closest_target.cell) + FOCUS_EXTRA_STEPS
    near = [z for z in group if z.entity_type == "zombie" and dist(obj.cell, z.cell) <= limit]
    weakest = min(near, key=lambda z: (head_remaining(z), dist(obj.cell, z.cell)), default=closest_target)
    if weakest is not closest_target and head_remaining(weakest) < head_remaining(closest_target):
        decision("%s 集火 %s（头部剩余 %.1f）" % (name_of(obj), name_of(weakest), head_remaining(weakest)))
        return weakest
    return closest_target


def choose_target(obj, targets):
    """替换 compaction.get_closest_target：先打威胁队伍的，其中能一下打死的优先，再按路程。"""
    closest = STATE.orig["get_closest_target"]
    try:
        cells = [m.cell for m in party(obj)]
        zombies = [t for t in targets if t.entity_type == "zombie"]
        urgent = [z for z in zombies if threatens(z, cells)]
        for tier in (urgent, targets):
            if not tier:
                continue
            killable = [t for t in tier if t.entity_type == "zombie" and killable_now(obj, t)]
            for group in (killable, tier):
                if not group:
                    continue
                target = closest(obj, group)
                if target:
                    target = focus_target(obj, target, group)
                    if target in urgent or target in killable:
                        decision("%s 优先攻击 %s（%s）" % (
                            name_of(obj), name_of(target),
                            "威胁到队伍" if target in urgent else "能一下打死"))
                    return target
        return False
    except Exception:
        log_exc("选择攻击目标", once=True)
        return closest(obj, targets)


# ----------------------------------------------------------------------------
# 撤退
# ----------------------------------------------------------------------------

def retreat(char, why):
    """走到僵尸下回合够不着、离僵尸远、离主角近的格子。没有更好的格子时不动。返回是否移动了。"""
    ca = STATE.ca
    threats = threats_of(char)
    if not threats:
        return False
    char.update_paths()
    pathdics = char.get_pathdics_within_ap()
    if not pathdics:
        return False
    danger = danger_cells(threats)
    player = player_of(char)

    def score(cell, ap):
        s = min(dist(cell, z.cell) for z in threats) * 10 - ap * 0.5
        if cell not in danger:
            s += 1000
        if player is not None:
            s -= dist(cell, player.cell) * 2
        return s

    here = score(char.cell, 0)
    cell, pathdic = max(pathdics.items(), key=lambda kv: score(kv[0], kv[1]["ap"]))
    if cell is char.cell or score(cell, pathdic["ap"]) <= here:
        return False
    char.intention = "flee"
    decision("%s 撤退（%s）：HP %d/%d，%s" % (
        name_of(char), why, char.hp, char.max_hp, "到安全位置" if cell not in danger else "暂时找不到完全安全的位置，尽量远离"))
    ca.move(char, pathdic["path"])
    return True


def parting_shots(char):
    """撤退后站在安全格时，用远程武器补几枪（期望值最高的部位，撤退时偏向打腿）。"""
    ca = STATE.ca
    threats = threats_of(char)
    if not threats or char.cell in danger_cells(threats):
        return
    for _ in range(PARTING_SHOTS):
        if char.ap < 1:
            return
        options = []
        for weapon in candidate_weapons(char):
            if weapon.atktype != "ranged" or (QUIET_COMBAT and is_noisy(weapon)):
                continue
            ammo = ammo_for(char, weapon)
            if ammo is None:
                continue
            for z in threats:
                d = ca.cy.caldist([*char.cell.coord, *z.cell.coord])
                if d <= weapon.range and ca.can_attack_with_weapon(weapon, char.cell, z.cell):
                    _, (ev, _, _) = best_part(char, weapon, ammo, z)
                    options.append((ev, weapon, ammo, z))
        if not options:
            return
        _, weapon, ammo, z = max(options, key=lambda o: o[0])
        decision("%s 撤退后用 %s 射击 %s" % (name_of(char), name_of(weapon), name_of(z)))
        attack_with_plan(char, z, lambda: char.attack_for_companion(z, weapon, ammo))
        wait_animation(char)
        threats = threats_of(char)
        if not threats or char.cell in danger_cells(threats):
            return


def attack_with_plan(char, target, func):
    """在本 MOD 的指挥下发动攻击：process_attack 会按 best_part 换部位，get_weapon 会按目标挑武器。"""
    local = STATE.local
    prev = (getattr(local, "attacker", None), getattr(local, "target", None), getattr(local, "weapon", None))
    local.attacker, local.target, local.weapon = char, target, None
    try:
        return func()
    finally:
        local.attacker, local.target, local.weapon = prev


# ----------------------------------------------------------------------------
# 接近目标
# ----------------------------------------------------------------------------

def all_danger(char):
    """地图上所有僵尸（包括看不见的）的攻击范围，和原版「等它过来」用的一样。"""
    return danger_cells(char.loc.zombies)


def kill_chance(char, weapon, zombie):
    """这一下打死 zombie 的概率（打头；暴击 ×5）。"""
    ammo = ammo_for(char, weapon)
    acc = max(0.0, min(100.0, char.get_accuracy(weapon, ammo, zombie, "head"))) / 100
    low, high = char.get_damage(weapon, ammo, zombie, "head")
    avg = (low + high) / 2
    crit = 0.0
    if char.entity_type != "npc":
        crit = max(0.0, min(100.0, char.get_critical_hit_chance(weapon, ammo, zombie, "head"))) / 100
    remaining = head_remaining(zombie)
    chance = crit if avg * 5 >= remaining else 0.0
    if avg >= remaining:
        chance += (1 - crit) * acc
    return chance


def approach_melee(comp, target, weapon):
    ca = STATE.ca
    pathdic = ca.get_pathdic_comp(comp, target.cell)
    if not pathdic:
        return None
    if comp.ap >= pathdic["ap"] + 5:
        return None             # 原版：走过去还剩 5 AP 以上，直接冲
    if comp.ap - pathdic["ap"] >= 1:
        why = None
        if CHARGE_TO_SAVE and threatens(target, [m.cell for m in party(comp) if m is not comp]):
            why = "它正威胁队伍"
        elif kill_chance(comp, weapon, target) >= CHARGE_KILL_CHANCE:
            why = "这一下多半能打死"
        if why:
            decision("%s 冲上去打 %s（%s）" % (name_of(comp), name_of(target), why))
            return bool(ca.move(comp, pathdic["path"]))
    # 停在僵尸攻击范围外等它过来。原版也这样做，但不按 AP 截断路线，路远时 move 失败、原地不动
    danger = all_danger(comp)
    path = []
    for cell in pathdic["path"]:
        if cell in danger:
            break
        path.append(cell)
    path = ca.truncate_path_by_ap(comp, path)
    comp.intention = "waiting_to_attack"
    if len(path) >= 2 and path[-1] is not comp.cell:
        decision("%s 走到 %s 攻击范围外等它过来" % (name_of(comp), name_of(target)))
        ca.move(comp, path)
    comp.face(target)
    return False


def take_firing_position(comp, target, weapon):
    """在这回合走得到、走完还能开火的格子里，优先挑僵尸够不着的，再挑最省 AP 的。"""
    ca = STATE.ca
    comp.update_paths()
    pathdics = comp.get_pathdics_within_ap()
    if not pathdics:
        return None
    danger = all_danger(comp)
    options = []
    for cell, pathdic in pathdics.items():
        if cell is comp.cell or comp.ap - pathdic["ap"] < 1 or getattr(cell, "pc", None):
            continue
        if ca.cy.caldist([*cell.coord, *target.cell.coord]) <= weapon.range and                 ca.can_attack_with_weapon(weapon, cell, target.cell):
            options.append((cell in danger, pathdic["ap"], pathdic))
    if not options:
        return None
    in_danger, _, pathdic = min(options, key=lambda o: (o[0], o[1]))
    decision("%s 走到%s射击位置打 %s" % (name_of(comp), "" if in_danger else "僵尸够不着的", name_of(target)))
    return bool(ca.move(comp, pathdic["path"]))


def approach_target(comp, target):
    """打不到目标时怎么接近。返回 None 表示交给原版 move_and_attack。"""
    ca = STATE.ca
    game = dt().game
    if getattr(game, "is_dlc", False) and getattr(comp, "role", None) == "terminus_survivor":
        return None             # 原版给 DLC 幸存者有特殊处理（发步枪）
    weapon = ca.get_weapon(comp)
    if not weapon:
        return None
    reach = weapon.reach if weapon.atktype == "melee" else weapon.range
    d = ca.cy.caldist([*comp.cell.coord, *target.cell.coord])
    if d <= reach and ca.can_attack_with_weapon(weapon, comp.cell, target.cell):
        return None             # 已经打得到：原版直接攻击
    if weapon.atktype == "melee":
        return approach_melee(comp, target, weapon)
    if SMART_FIRING_POSITION:
        return take_firing_position(comp, target, weapon)
    return None


# ----------------------------------------------------------------------------
# 用药 / 治疗
# ----------------------------------------------------------------------------

def strength_ok(item, patient, limit):
    return patient.med_strength + item.get_strength(patient) <= limit


def strength_limit(patient):
    return MED_STRENGTH_HARD if hp_ratio(patient) < CRITICAL_HP_RATIO else MED_STRENGTH_SOFT


def find_ailment_item(healer, patient):
    """[(异常, 物品)]：patient 身上的异常和 healer 够得着的对症用品（药物强度安全的）。"""
    found = []
    ailments = patient.status_ailments
    for ailment, names in AILMENT_ITEMS:
        if ailment not in ailments or any(a == ailment for a, _ in found):
            continue
        for name in names:
            item = next((i for i in healer.get_nearby_items(name) if strength_ok(i, patient, MED_STRENGTH_HARD)), None)
            if item is not None:
                found.append((ailment, item))
                break
    return found


def find_healing_med(healer, patient):
    if hp_ratio(patient) >= HEAL_HP_RATIO:
        return None
    deficit = patient.max_hp - patient.hp
    limit = strength_limit(patient)
    meds = []
    for med in healer.get_nearby_items(category="med"):
        heal = med.get_effects(patient).get("hp", 0) if hasattr(med, "get_effects") else med.effects.get("hp", 0)
        if heal > 0 and strength_ok(med, patient, limit):
            meds.append((min(heal, deficit), -med.get_strength(patient), med))
    if not meds:
        return None
    return max(meds, key=lambda m: (m[0], m[1]))[2]


def can_firstaid(healer, patient):
    from core.constants import UNIQUE_ACTIONS
    actions = UNIQUE_ACTIONS.get(healer.occu, ())
    if isinstance(actions, str):
        actions = (actions,)
    if "firstaid" not in actions or healer.cooldown.get("firstaid", 0) > 0:
        return False
    if patient not in healer.get_firstaid_targets():
        return False
    return patient.hp < patient.max_hp - 2 or any(a in patient.status_ailments for a in ("bleeding", "sprain"))


def treat(healer, patient):
    """healer 治疗 patient（可以是自己）。返回做了几件事。"""
    done = 0
    who = "自己" if patient is healer else " %s " % name_of(patient)
    if can_firstaid(healer, patient):
        decision("%s 给%s急救（HP %d/%d）" % (name_of(healer), who, patient.hp, patient.max_hp))
        healer.firstaid(patient)
        done += 1
    for ailment, item in find_ailment_item(healer, patient):
        if ailment in patient.status_ailments:
            decision("%s 用 %s 给%s治疗%s" % (name_of(healer), name_of(item), who, ailment_name(ailment)))
            item.use(patient)
            done += 1
    med = find_healing_med(healer, patient)
    if med is not None:
        decision("%s 用 %s 给%s回血（HP %d/%d，药物强度 %.1f）" % (
            name_of(healer), name_of(med), who, patient.hp, patient.max_hp, patient.med_strength))
        med.use(patient)
        done += 1
    if done:
        time.sleep(0.5)
    return done


def needs_treatment(patient):
    if any(a in patient.status_ailments for a, _ in AILMENT_ITEMS):
        return True
    return hp_ratio(patient) < HEAL_HP_RATIO


def has_treatment_for(healer, patient):
    return bool(can_firstaid_anywhere(healer) or find_ailment_item(healer, patient) or find_healing_med(healer, patient))


def can_firstaid_anywhere(healer):
    from core.constants import UNIQUE_ACTIONS
    actions = UNIQUE_ACTIONS.get(healer.occu, ())
    if isinstance(actions, str):
        actions = (actions,)
    return "firstaid" in actions and healer.cooldown.get("firstaid", 0) <= 0


def go_next_to(healer, patient):
    """走到 patient 旁边（不进僵尸攻击范围）。返回是否已经相邻。"""
    from core.location.utils import in_contact
    if patient.cell is healer.cell or in_contact(patient.cell, healer.cell):
        return True
    ca = STATE.ca
    danger = danger_cells(threats_of(healer))
    best = None
    for cell in patient.cell.adjs:
        if cell in danger or not in_contact(cell, patient.cell):
            continue
        pathdic = ca.get_pathdic_comp(healer, cell)
        if pathdic and pathdic["ap"] <= min(HEAL_OTHERS_MAX_MOVE_AP, healer.ap - 1):
            if best is None or pathdic["ap"] < best["ap"]:
                best = pathdic
    if best is None:
        return False
    decision("%s 走到 %s 身边去治疗" % (name_of(healer), name_of(patient)))
    ca.move(healer, best["path"])
    return patient.cell is healer.cell or in_contact(patient.cell, healer.cell)


def handle_medication(comp):
    """替换 compaction.handle_medication：先治自己，再治身边的你和队友。"""
    # 每个队友每回合最先调用的就是这里，顺便给大家手上的东西做记号
    try:
        remember_party_items(comp)
    except Exception:
        log_exc("记录队伍物品", once=True)
    if not settings(comp).get("use_medical_supplies"):
        return
    try:
        treat(comp, comp)
        if not HEAL_OTHERS:
            return
        patients = [m for m in party(comp) if m is not comp and needs_treatment(m)]
        # 流血最急，其次按 HP 比例
        patients.sort(key=lambda m: ("bleeding" not in m.status_ailments, hp_ratio(m)))
        for patient in patients:
            if not has_treatment_for(comp, patient):
                continue
            if go_next_to(comp, patient):
                treat(comp, patient)
    except Exception:
        log_exc("用药 / 治疗")


# ----------------------------------------------------------------------------
# 拾取地上的东西
# ----------------------------------------------------------------------------

def remember_party_items(char):
    """给你和队友现在拿着的物品做记号，之后它们出现在地上就知道是你们丢的。"""
    if not KEEP_DROPPED:
        return
    for member in party(char):
        for item in member.items:
            if not getattr(item, OWNED_ATTR, False):
                try:
                    setattr(item, OWNED_ATTR, True)
                except Exception:
                    pass


def is_melee(item):
    return getattr(item, "atktype", None) == "melee" and hasattr(item, "damage")


def melee_score(item):
    """游戏自己的近战武器估值（伤害、攻击距离、耐久、当前状态、重量）。"""
    try:
        from core.item.weapon import get_melee_weapon_value
        return get_melee_weapon_value(item)
    except Exception:
        return getattr(item, "value", 0) or 0


def pickup_needs(comp):
    ammo = set()
    for member in party(comp):
        for item in member.items:
            ammo.update(getattr(item, "ammos", None) or ())
    best_melee = max((melee_score(i) for i in comp.items if is_melee(i)), default=0)
    return ammo, best_melee


def needed_reason(item, needs):
    """item 是不是用得上的东西：返回原因，用不上返回 None。"""
    ammo, best_melee = needs
    if (getattr(item, "weight", 0) or 0) > PICK_UP_MAX_WEIGHT:
        return None
    name = getattr(item, "name", None)
    if name in PICK_UP_NAMES:
        return "设置里要捡"
    if PICK_UP_AMMO and name in ammo:
        return "弹药"
    if PICK_UP_MEDS and getattr(item, "category", None) == "med":
        return "医疗用品"
    if PICK_UP_BETTER_MELEE and is_melee(item) and melee_score(item) > best_melee * BETTER_MELEE_RATIO:
        return "更好的近战武器"
    return None


def filtered_pickup_items(comp, cell):
    """替换 compaction.get_filtered_pickup_items_for_cell：原版按价值 / 重量设置筛出的东西，
    去掉你们自己丢的，再加上用得上的。"""
    items = STATE.orig["get_filtered_pickup_items_for_cell"](comp, cell)
    if not cell.items:
        return items
    try:
        if KEEP_DROPPED:
            items = [i for i in items if not getattr(i, OWNED_ATTR, False)]
        if PICK_UP_NEEDED:
            chosen = set(map(id, items))
            needs = None
            for item in cell.items:
                if id(item) in chosen or (KEEP_DROPPED and getattr(item, OWNED_ATTR, False)):
                    continue
                if needs is None:
                    needs = pickup_needs(comp)
                if needed_reason(item, needs):
                    items.append(item)
    except Exception:
        log_exc("筛选要捡的东西", once=True)
        return STATE.orig["get_filtered_pickup_items_for_cell"](comp, cell)
    return items


def pick_up_here(comp):
    """捡起脚下格子里要捡的东西（和原版一样不花 AP）。"""
    ca = STATE.ca
    items = ca.get_filtered_pickup_items_for_cell(comp, comp.cell)
    if not items:
        return
    comp.items += items
    comp.cell.items = [i for i in comp.cell.items if i not in items]
    for item in items:
        try:
            setattr(item, OWNED_ATTR, True)
        except Exception:
            pass
    counts = {}
    for item in items:
        counts[name_of(item)] = counts.get(name_of(item), 0) + 1
    decision("%s 捡起 %s" % (name_of(comp), "、".join(n if c == 1 else "%s ×%d" % (n, c) for n, c in counts.items())))
    if comp.spr:
        ca.soundplay("ui_item")
        comp.items.show_acquired_items(items)


def approach(comp, pathdic, danger):
    """一回合走不到：沿路先走一段，不停在僵尸攻击范围里。返回是否走了。"""
    ca = STATE.ca
    path = ca.truncate_path_by_ap(comp, pathdic["path"])
    while len(path) > 1 and path[-1] in danger:
        path = path[:-1]
    if len(path) < 2 or path[-1] is comp.cell:
        return False
    decision("%s 去捡远处的东西，这回合先走一段" % name_of(comp))
    ca.move(comp, path)
    return True


def handle_search_or_pickup(comp):
    try:
        return search_or_pickup(comp)
    except Exception:
        log_exc("搜刮 / 拾取", once=True)
    return STATE.orig["handle_search_or_pickup"](comp)


def search_or_pickup(comp):
    """流程和原版 handle_search_or_pickup 一样（走到最近的目标格，捡东西、搜家具 / 尸体，
    循环到 AP 不够），改动：
      - 比较最近 SEARCH_CANDIDATES 个目标格的路程（原版 6 个）
      - 不去僵尸攻击范围内的格子
      - 一回合走不到时先走一段（原版原地不动）"""
    ca = STATE.ca
    remember_party_items(comp)
    for _ in range(50):     # 防止意外死循环；正常会因为 AP 用完或没有目标而结束
        if not ca.handle_enemy_encounter(comp):
            return None
        if comp.ap < comp.search_ap:
            return None
        comp.intention = "search"
        here = comp.cell
        danger = danger_cells(threats_of(comp))
        cells = [c for c in ca.get_search_or_pickup_cells(comp) if c is here or c not in danger]
        cells.sort(key=lambda c: (abs(c.floor - here.floor), dist(here, c)))
        reachable = []
        for cell in cells[:SEARCH_CANDIDATES]:
            pathdic = ca.get_pathdic_comp(comp, cell)
            if pathdic:
                reachable.append((cell, pathdic))
        if not reachable:
            if settings(comp).get("movement_option") in ("follow", "stay_close"):
                ca.move_closer_to_player(comp)
            return None
        target, pathdic = min(reachable, key=lambda cp: cp[1]["ap"])
        if comp.ap < pathdic["ap"]:
            if SEARCH_APPROACH and settings(comp).get("movement_option") in ("follow", "stay_close"):
                approach(comp, pathdic, danger)
            return None
        if not ca.move(comp, pathdic["path"]):
            return None
        if not ca.handle_enemy_encounter(comp):
            return None
        if comp.cell == target:
            if settings(comp).get("pick_up_items"):
                pick_up_here(comp)
            if comp.ap >= comp.search_ap:
                for obj in ca.get_unsearched_objects(comp):
                    if comp.cell in obj.interactable_cells:
                        comp.search_for_companion(obj)
                        while comp.spr and comp.spr.is_animating:
                            time.sleep(0.1)
    return None


# ----------------------------------------------------------------------------
# 护卫
# ----------------------------------------------------------------------------

def guard_needed(comp):
    if not GUARD:
        return False
    player = player_of(comp)
    if player is None:
        return False
    return any(dist(z.cell, player.cell) <= GUARD_RANGE for z in threats_of(comp))


def make_guarded(name, orig):
    def guarded(comp, *args, **kwargs):
        try:
            if guard_needed(comp):
                player = player_of(comp)
                if dist(comp.cell, player.cell) > GUARD_STAY_WITHIN and comp.ap > 0:
                    decision("%s 有僵尸靠近你，放下手头的事回到你身边" % name_of(comp))
                    STATE.ca.move_closer_to_player(comp)
                return None
        except Exception:
            log_exc("护卫（%s）" % name, once=True)
        return orig(comp, *args, **kwargs)
    guarded.__name__ = name
    return guarded


# ----------------------------------------------------------------------------
# 替换的队友 AI 函数
# ----------------------------------------------------------------------------

def handle_attack(comp):
    """HP 低于阈值时不主动近战：近处有僵尸就撤退，再从安全位置补几枪。
    否则交给原版攻击流程（目标 / 武器 / 部位已替换）。"""
    try:
        if is_low_hp(comp):
            threats = threats_of(comp)
            if threats:
                if any(dist(z.cell, comp.cell) <= RETREAT_TRIGGER_RANGE for z in threats):
                    retreat(comp, "HP 低于 %d%%" % round(RETREAT_HP_RATIO * 100))
                parting_shots(comp)
                return None
    except Exception:
        log_exc("撤退", once=True)
    return STATE.orig["handle_attack"](comp)


def move_and_attack(comp, target):
    orig = STATE.orig["move_and_attack"]
    if target.entity_type != "zombie":
        return orig(comp, target)

    def run():
        try:
            result = approach_target(comp, target)
            if result is not None:
                return result
        except Exception:
            log_exc("接近目标", once=True)
        return orig(comp, target)

    return attack_with_plan(comp, target, run)


def get_weapon(comp):
    local = STATE.local
    target = getattr(local, "target", None)
    if target is not None and getattr(local, "attacker", None) is comp:
        if getattr(local, "weapon", None) is not None:
            return local.weapon     # 同一次攻击里已经挑过
        try:
            weapon = pick_weapon(comp, target)
            if weapon is not None:
                local.weapon = weapon
                return weapon
        except Exception:
            log_exc("选择武器", once=True)
    return STATE.orig["get_weapon"](comp)


def flee(comp):
    try:
        if retreat(comp, "逃离僵尸"):
            return None
    except Exception:
        log_exc("逃跑", once=True)
    return STATE.orig["flee"](comp)


def handle_enemy_encounter(comp):
    try:
        if is_low_hp(comp) and comp.intention not in ("flee", "waiting_to_attack") and threats_of(comp):
            retreat(comp, "搜刮途中遇到僵尸，HP 低")
            return False    # 原版约定：False = 别再继续搜刮了
    except Exception:
        log_exc("遇敌撤退", once=True)
    return STATE.orig["handle_enemy_encounter"](comp)


def handle_food_consumption(comp, *args, **kwargs):
    # 原版在每回合任务做完后调用它（睡前也会调用），借这个时机做「回合结束前退到安全格」
    try:
        if SAFE_STEP and not comp.sleeping and comp.ap >= 1:
            threats = threats_of(comp)
            if threats and comp.cell in danger_cells(threats):
                retreat(comp, "回合结束前离开僵尸攻击范围")
    except Exception:
        log_exc("回合结束前避险", once=True)
    return STATE.orig["handle_food_consumption"](comp, *args, **kwargs)


# ----------------------------------------------------------------------------
# 补丁
# ----------------------------------------------------------------------------

def patch_character(module):
    if getattr(module, "_companion_ai_patched", False):
        return
    orig = module.process_attack

    def process_attack(char, target, part, *args, **kwargs):
        local = STATE.local
        if getattr(local, "attacker", None) is char and getattr(local, "target", None) is target:
            try:
                weapon = args[0] if args else kwargs.get("weapon")
                ammo = args[1] if len(args) > 1 else kwargs.get("ammo")
                chosen, (ev, acc, avg) = best_part(char, weapon, ammo, target)
                if chosen != part:
                    decision("%s 攻击 %s 的%s（命中 %d%%，平均伤害 %.1f）" % (
                        name_of(char), name_of(target), PART_NAMES[chosen], round(acc * 100), avg))
                part = chosen
            except Exception:
                log_exc("选择攻击部位", once=True)
        return orig(char, target, part, *args, **kwargs)

    module.process_attack = process_attack
    module._companion_ai_patched = True
    log("已接管队友攻击部位的选择")


def patch_compaction(module):
    if getattr(module, "_companion_ai_patched", False):
        return
    STATE.ca = module
    replacements = {
        "get_closest_target": choose_target,
        "get_weapon": get_weapon,
        "move_and_attack": move_and_attack,
        "handle_attack": handle_attack,
        "flee": flee,
        "handle_enemy_encounter": handle_enemy_encounter,
        "handle_medication": handle_medication,
        "handle_food_consumption": handle_food_consumption,
        "get_filtered_pickup_items_for_cell": filtered_pickup_items,
        "handle_search_or_pickup": handle_search_or_pickup,
    }
    # 自己的搜刮流程要用到的原版函数，缺了就不替换搜刮
    search_needs = ("get_search_or_pickup_cells", "get_unsearched_objects", "truncate_path_by_ap", "soundplay")
    if not all(hasattr(module, n) for n in search_needs):
        log("compaction 里缺少搜刮要用的函数，搜刮保持原版")
        del replacements["handle_search_or_pickup"]
    for name, func in replacements.items():
        orig = getattr(module, name, None)
        if orig is None:
            log("找不到 compaction.%s，跳过" % name)
            continue
        STATE.orig[name] = orig
        setattr(module, name, func)
    # 护卫套在最外层（搜刮套的是上面换过的版本）
    for name in GUARDED_TASKS:
        current = getattr(module, name, None)
        if current is None:
            log("找不到 compaction.%s，跳过护卫" % name)
            continue
        setattr(module, name, make_guarded(name, current))
    module._companion_ai_patched = True
    log("已替换队友 AI：目标 / 武器 / 部位选择、安静作战、撤退、逃跑、用药与互相治疗、护卫、拾取、回合结束前避险")


PATCHERS = {
    "core.creature.character": patch_character,
    "actions.compaction": patch_compaction,
}


def on_module_loaded(name, module):
    """由 base_library.zip 里的 MOD 加载器在游戏模块加载完成后调用。"""
    patcher = PATCHERS.get(name)
    if patcher is not None:
        patcher(module)


try:
    with open(LOG_PATH, "w", encoding="utf-8") as _f:
        _f.write(time.strftime("[%Y-%m-%d %H:%M:%S] ") + "队友 AI 加强 MOD 已加载\n")
except OSError:
    pass
