# -*- coding: utf-8 -*-
"""
Terminus: Zombie Survivors - 队友 AI 加强 MOD

在游戏自带的队友 AI（actions/compaction.py）上替换几个决策点，其余行为保持原版：
  - 回合规划：按游戏真实规则估算「回合结束站在哪个格子，下个僵尸回合整个队伍会挨多少打、有多大死亡风险」
    （僵尸追最近的人、能打几下、砸门要花的行动力、命中、伤害减防御、抓伤 / 擒抱 / 咬的分布，
    流血且 AP 为 0 会被咬死；你的损失按 5 倍算），每一步比较打哪只、用什么、打哪个部位、
    要不要先走过去、打一下就走还是打到底，收益和风险都折算成 HP，打不划算就停手，最后站到风险最低的格子。
    撤退、等僵尸过来、打完就跑、集火、替你挡刀都由此而来
  - 补刀：残废的僵尸（没了躯干不能抓人，但还会咬流血的人）也清掉，不会整队守着它不敢干别的
  - 武器磨损算进代价：近战打中一下掉 6~8 点状态（打头 ×1.5），不为一点点收益把武器磨坏
  - 没有能用的近战武器时，从你和其他队友包里拿一把多余的（每人至少留一把，不拿你最好的和最爱的）
  - 关门：站在门边、关上门能明显少挨打就关
  - 队友按顺序行动：离僵尸近的先行动，后面的人看着结果再决定
  - 选部位：头 = 打死的概率；躯干 = 攻击力按比例下降（打到 0 就不能抓）；腿 = 行动力按比例下降
  - 安静作战：开枪会把附近最多 40 只僵尸引过来，这个代价算进收益里
  - 僵尸能打到自己时不做读书、制作、修理等杂事，留着 AP
  - 自动穿上自己背包里有防御的衣物（防御直接从僵尸每次伤害里减）
  - 用药：按 HP 百分比提前治疗；药物强度不超过头痛线（濒危时不超过昏厥线）
    （原版 HP 低于 5 才吃药，强度超标时还会给玩家弹「会昏厥」的确认框）
  - 互相治疗：给相邻或走得到的你和其他队友包扎、用药，流血的先治；医生会用「急救」
  - 护卫：附近有僵尸靠近你时，队友不再跑去搜刮、修东西、种田，先回到你身边（不送上门）
  - 修原版 bug：队友一步都走不了时原版会报错、中断整个队友回合
  - 拾取：地上用得上的东西（弹药、药品绷带、更好的近战武器）不论价值都捡；
    不捡回你们自己丢下的东西；一回合走不到的先走过去，下回合接着捡；不进僵尸攻击范围
    （原版默认只捡价值「高」以上的东西，弹药、绷带、药都不够格；够不着时原地不动）

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

# 回合结束前还站在僵尸能打到的格子、又有剩余 AP 时，换到更安全的格子
SAFE_STEP = True

# ---- 战斗：回合规划 ----
# 队友每一步比较「打哪只、用什么、打哪个部位、要不要先走过去、最后站在哪」，
# 收益和风险都折算成 HP：打这一下能让僵尸下回合少造成多少伤害（加上推进击杀的长远收益），
# 减去回合结束时自己预计挨的打、开枪引来的僵尸、子弹消耗。打不划算就停手，挪到最安全的格子。
PLANNER = True

# 死亡折算成多少 HP 的损失（人物最大 HP 一般是 20）
DEATH_COST = 80.0
# 有感染抗性的人被咬不会死，但会加尸变进度，折算成多少 HP
ZOMBIFY_COST = 25.0
# 血越少越怕挨打：损失 × (1 + RISK_AVERSION × 已损失 HP 比例)
RISK_AVERSION = 1.0
# 削弱 / 打死僵尸的长远收益：大致是「不管它的话它还会威胁几回合」。
# 以下几个默认值是在真实存档上和原版 AI 对打几百场调出来的：再大队友太莽，再小队友太怂、护不住你
FUTURE_WEIGHT = 3.5
# 回合结束时离你每远一格（超过 GUARD_STAY_WITHIN 的部分）算多少 HP 的代价，让队友待在你附近
STAY_NEAR_COST = 0.15
# 算回合结束风险时也算上你和其他队友会挨的打（僵尸追最近的人：队友站得比你离僵尸近，就替你挡了）。
# 你倒下游戏就结束，所以你的损失按 PROTECT_PLAYER_WEIGHT 倍算
PROTECT_PLAYER_WEIGHT = 5.0
PROTECT_TEAM_WEIGHT = 1.0
# 回合结束站在门边时，关上门能明显少挨打就关（花 0.5 AP；关着的门僵尸得先砸开）
CLOSE_DOORS = True
# 队友回合按顺序来：离僵尸近、正在交战的先行动，后面的人看着前面的结果再决定
ORDER_COMPANIONS = True
# 下回合打不到任何人的僵尸，长远收益按这个权重算（它会自己走过来，那时我们先手）
FUTURE_WEIGHT_IDLE = 0.5
# 打死一只僵尸本身值多少 HP：残废的僵尸（没了躯干不能抓人）也还会咬流血的人、挡着你们搜刮，
# 不补刀的话队伍会一直守着它不敢干别的
CLEAR_VALUE = 2.0
# 单只僵尸每回合威胁的上限（HP），防止大块头的长远收益大过死亡风险
POTENTIAL_CAP = 12.0
# 评估出手时，除了「打一下就停」，也考虑「把这回合能打的都打完」
COMMIT_PLANNING = True
# 每花 1 AP 走位的代价（AP 能攒到下回合，不白花）
MOVE_AP_COST = 0.05
# 打这一下的收益至少要有这么多（HP）才出手
MIN_ATTACK_GAIN = 0.05
# 回合结束前换位置，至少要少挨这么多（HP）
MIN_REPOSITION_GAIN = 0.3

# 安静作战：开枪会把附近最多 40 只没在追人的僵尸引过来（游戏规则），还费子弹
QUIET_COMBAT = True
NOISE_COST_PER_ZOMBIE = 0.8   # 每引来一只算多少 HP 的代价
AMMO_COST = 0.4               # 每发子弹 / 每支箭的代价
# 只在原版流程里用（DLC 等交给原版时）：不出声的武器达到枪的这个比例时用不出声的
QUIET_MIN_RATIO = 0.5

# 僵尸能打到你的时候，不做读书、制作、修理、种田等花 AP 的杂事：
# 留着 AP 不光能逃跑，回合结束时有 AP，僵尸的攻击有一部分会变成擒抱（扣 AP 不扣血）
COMBAT_LOCK = True

# 武器磨损：近战每打中一下掉 6~8 点状态（武器耐久越高掉得越少，打头 ×1.5），100 点的武器十来下就坏。
# 每掉 1 点状态折算成多少 HP；身上只剩这一把能用的近战武器时按 WEAR_COST_LAST 算（坏了就只能干看着）
WEAR_COST = 0.02
WEAR_COST_LAST = 0.06

# 队友身上没有能用的近战武器（没有、或状态都低于 ARM_MIN_CONDITION）时，从你和其他队友包里拿一把多余的：
# 每人至少留一把，不拿你最好的那把，也不拿你设成「最爱」的武器
ARM_FROM_PARTY = True
ARM_MIN_CONDITION = 10

# 自动穿上自己背包里有防御的衣物（衣物防御直接从僵尸每次伤害里减），总负重不超过这个值
AUTO_DRESS = True
DRESS_MAX_ENCUMBRANCE = 9.0

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

# 僵尸能打到自己时不做的杂事（花 AP 的）→ 日志里的说法
LOCKED_TASKS = {
    "handle_upgrade": "升级设施",
    "handle_clearing_debris": "清理障碍",
    "handle_vehicle_repairs": "修车",
    "handle_plant_care": "种田",
    "handle_reading_morale": "读书解闷",
    "handle_reading_skill": "读书学技能",
    "handle_listening_music": "听音乐",
    "handle_crafting": "制作",
    "handle_mending": "缝补衣服",
    "handle_electronic_repairs": "修电器",
    "handle_tool_repairs": "修工具",
}

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
# 威胁模型：回合结束时站在某个格子，下个僵尸回合预计的损失
# ----------------------------------------------------------------------------
# 按游戏规则（core/creature/zombie/base.py 的 Zombie.attack、actions/zombieaction.py）：
#   - 僵尸追最近的人，沿最短路走；路线下一格有人就原地攻击，直到 AP 用完（每次 1 AP）
#   - 只能从上下左右相邻的格子攻击；行动力 = max_action × 腿剩余血量比例（没腿只能爬）
#   - 每次攻击随机挑：没流血 → 抓伤、抓伤、（人还有 AP 时）擒抱；
#     流血 → 只有擒抱（扣 AP 不扣血）；流血且 AP 为 0 → 咬，没有感染抗性就直接死
#   - 伤害 = 攻击力 × 0.9~1.1 − 衣物防御；攻击力 = max_attack × 躯干剩余血量比例（没腿再减半）；
#     躯干打到 0 的僵尸不能抓伤和擒抱
#   - 先按闪避率判定闪避

def cheb(a, b):
    if getattr(a, "floor", 0) != getattr(b, "floor", 0):
        return 99
    return max(abs(a.coord[0] - b.coord[0]), abs(a.coord[1] - b.coord[1]))


def reach_cost(zombie, cell):
    """僵尸走到 cell 上下左右某一格大约要花的行动力（斜走 1、直走 1.41，这里按格数近似）。"""
    return min((cheb(zombie.cell, n) for n in cell.adjs), default=99)


def blocking_divider(a, b, closed=()):
    """a → b 之间关着、还没被砸烂的门窗（僵尸得先砸开才能过去或打到人）。closed：假设关上的门。"""
    for div in (getattr(a, "doors", None) or ()):
        if b not in div.cells:
            continue
        if div in closed:
            return div if (getattr(div, "hp", 0) or 0) > 0 else None
        if getattr(div, "melee_blocked", False):
            return div
    for div in (getattr(a, "windows", None) or ()):
        if b in div.cells and getattr(div, "melee_blocked", False):
            return div
    return None


def bash_cost(zombie, cells, closed=()):
    """沿 cells 走一遍要砸门窗花的 AP（每砸一下 1 AP，伤害 = 攻击力 × 0.9~1.1）。"""
    cost = 0
    hit = max(0.5, zombie.attack_power or 0)
    for a, b in zip(cells, cells[1:]):
        div = blocking_divider(a, b, closed)
        if div is not None:
            cost += math.ceil((getattr(div, "hp", 0) or 0) / hit)
    return cost


def zombie_attacks_at(zombie, cell, closed=()):
    """zombie 下回合能对站在 cell 的人攻击几次。
    有僵尸自己算好的路径（pathdics：到每个格子的路线和行动力）时按它算，并加上砸门窗的 AP：
    游戏算攻击范围时把关着的门当作能过，实际得先砸开。"""
    power = zombie.action_power or 0
    if power < 1:
        return 0
    pathdics = getattr(zombie, "pathdics", None)
    attack_cells = getattr(zombie, "attack_cells", None)
    if pathdics and attack_cells is not None:
        if cell not in attack_cells:
            return 0
        best = None
        for n in cell.adjs:
            pd = pathdics.get(n)
            if pd is None:
                continue
            cost = pd["ap"] + bash_cost(zombie, list(pd["path"]) + [cell], closed)
            if best is None or cost < best:
                best = cost
        if best is None or power - best < 1:
            return 0
        return int(power - best)
    cost = reach_cost(zombie, cell)
    if attack_cells:
        if cell not in attack_cells:
            return 0
    elif cost > power - 1:
        return 0
    return max(1, int(power - cost))


def has_trait(char, name):
    try:
        return bool(char.has_trait(name))
    except Exception:
        return False


def bleeding_now(char):
    """会被咬的那种流血（本回合刚流的血不算，游戏规则）。"""
    try:
        return bool(char.is_bleeding())
    except Exception:
        return "bleeding" in (getattr(char, "status_ailments", None) or {})


class Victim:
    """被攻击者的属性快照。"""

    def __init__(self, char):
        self.char = char
        self.hp = max(0.1, char.hp)
        self.max_hp = max(1.0, getattr(char, "max_hp", 0) or 1)
        avd = getattr(char, "avd", 0) or 0
        self.hit = 1 - max(0, min(95, avd)) / 100
        self.bleeding = bleeding_now(char)
        self.thick = 0.5 if has_trait(char, "thick_skin") else 1.0
        self.resist = has_trait(char, "infection_resistance")
        self.defense = getattr(char, "defense", 0) or 0
        self.is_pc = getattr(char, "entity_type", "pc") == "pc"
        self._dmg = {}

    def damage(self, zombie):
        dmg = self._dmg.get(zombie)
        if dmg is None:
            base = zombie.attack_power or 0
            if self.is_pc:
                # 伤害 = U(0.9, 1.1) × 攻击力 − 防御，最低 0：按截断后的期望算
                low, high = base * 0.9 - self.defense, base * 1.1 - self.defense
                dmg = 0.0 if high <= 0 else (high * high / 2 / (high - low) if low < 0 else (low + high) / 2)
            else:
                dmg = base * max(0.1, 1 - self.defense / 10)
            self._dmg[zombie] = dmg
        return dmg


def expected_loss(victim, attackers, ap_end, weaken=None):
    """attackers = [(僵尸, 攻击次数)]，weaken = {僵尸: 被削弱的比例}。返回折算成 HP 的期望损失。"""
    ap = max(0.0, ap_end)
    loss = bite = 0.0
    for zombie, count in attackers:
        k = 1.0 - (weaken.get(zombie, 0.0) if weaken else 0.0)
        if k <= 0:
            continue
        dmg = victim.damage(zombie)
        claws = zombie.hp.get("body", 0) > 0
        bites = "UNABLE_TO_BITE" not in (getattr(zombie, "flags", None) or ())
        p = victim.hit * k
        for _ in range(count):
            alive = 1.0 - bite
            if victim.bleeding:
                if ap > 0:
                    if claws:
                        ap -= p * dmg
                elif bites:
                    bite += alive * p
            elif claws:
                if ap > 0:
                    loss += alive * p * dmg * victim.thick * 2 / 3
                    ap -= p * dmg / 3
                else:
                    loss += alive * p * dmg * victim.thick
    hp = victim.hp
    hp_death = min(1.0, max(0.0, (loss - 0.75 * hp) / (0.5 * hp)))
    if victim.resist:
        cost, death = loss + ZOMBIFY_COST * bite, hp_death
    else:
        cost, death = loss, 1 - (1 - bite) * (1 - hp_death)
    aversion = 1 + RISK_AVERSION * (1 - min(1.0, hp / victim.max_hp))
    return cost * aversion + DEATH_COST * death


def nearest_char_cells(zombie, cells):
    return min((dist(zombie.cell, c) for c in cells), default=999)


class Board:
    """某一时刻的战场：char 这回合走得到的格子，站在每个格子时整个队伍下个僵尸回合预计的损失。
    僵尸追最近的人：char 站得比你（或别的队友）离某只僵尸更近，它就改来追 char，等于替人挡了。"""

    def __init__(self, char, closed=()):
        self.char = char
        self.closed = closed
        self.victim = Victim(char)
        self.zombies = list(char.loc.zombies)
        loc_chars = getattr(char.loc, "chars", None) or party(char)
        self.others = [c for c in loc_chars if c is not char and getattr(c, "is_alive", True)]
        self.other_cells = [c.cell for c in self.others]
        char.update_paths()
        pds = dict(char.get_pathdics_within_ap() or {})
        pds[char.cell] = {"ap": 0, "path": [char.cell]}
        self.pds = pds
        try:
            self.step = max(0.5, float(char.move_ap))
        except Exception:
            self.step = 1.0
        self.player = player_of(char)
        # 你和其他队友（不算 char）会被哪些僵尸打，按保护权重计入
        self.team = []
        game_player = dt().game.player
        for member in party(char):
            if member is char:
                continue
            rest = [o.cell for o in self.others if o is not member]
            attackers = []
            for zombie in self.zombies:
                count = zombie_attacks_at(zombie, member.cell, closed)
                if count and nearest_char_cells(zombie, rest) >= dist(zombie.cell, member.cell):
                    attackers.append((zombie, count))
            if attackers:
                weight = PROTECT_PLAYER_WEIGHT if member is game_player else PROTECT_TEAM_WEIGHT
                self.team.append((member, Victim(member), weight, attackers, getattr(member, "ap", 0) or 0))
        self._attackers = {}
        self._team_loss = {}
        self._special = None
        self._safe = None

    def attackers(self, cell):
        got = self._attackers.get(cell)
        if got is None:
            got = []
            for zombie in self.zombies:
                count = zombie_attacks_at(zombie, cell, self.closed)
                # 僵尸追最近的人：有别人更近，它就去打别人
                if count and nearest_char_cells(zombie, self.other_cells) >= dist(zombie.cell, cell):
                    got.append((zombie, count))
            self._attackers[cell] = got
        return got

    def diverted(self, cell):
        """char 站在 cell 时，从你 / 队友身上引开的僵尸。"""
        out = []
        for member, _, _, attackers, _ in self.team:
            for zombie, _ in attackers:
                if dist(zombie.cell, cell) < dist(zombie.cell, member.cell):
                    out.append(zombie)
        return frozenset(out)

    def team_loss(self, diverted, weaken=None):
        key = (diverted, tuple(sorted((id(z), w) for z, w in weaken.items())) if weaken else None)
        got = self._team_loss.get(key)
        if got is None:
            got = 0.0
            for _, victim, weight, attackers, ap in self.team:
                left = [(z, n) for z, n in attackers if z not in diverted]
                if left:
                    got += weight * expected_loss(victim, left, ap, weaken)
            self._team_loss[key] = got
        return got

    def place_cost(self, cell):
        """站位本身的代价：离你太远。"""
        if self.player is None:
            return 0.0
        return STAY_NEAR_COST * max(0, dist(cell, self.player.cell) - GUARD_STAY_WITHIN)

    def own_risk(self, cell, ap_end, weaken=None):
        attackers = self.attackers(cell)
        return expected_loss(self.victim, attackers, ap_end, weaken) if attackers else 0.0

    def risk(self, cell, ap_end, weaken=None):
        """char 回合结束站在 cell、还剩 ap_end AP 时，整个队伍预计的损失（含站位代价）。"""
        return self.own_risk(cell, ap_end, weaken) + self.team_loss(self.diverted(cell), weaken) + self.place_cost(cell)

    def move_cost(self, origin, cell):
        if origin is self.char.cell:
            pd = self.pds.get(cell)
            return pd["ap"] if pd else None
        return cheb(origin, cell) * self.step

    def special_cells(self):
        """站上去会挨打、或会把僵尸从别人身上引开的格子。"""
        if self._special is None:
            self._special = [c for c in self.pds if self.attackers(c) or self.diverted(c)]
        return self._special

    def safe_cells(self):
        """其余格子：风险只差站位代价，按它排好。"""
        if self._safe is None:
            special = set(self.special_cells())
            self._safe = sorted((c for c in self.pds if c not in special), key=self.place_cost)
        return self._safe

    def end_risk(self, origin, budget, weaken=None):
        """从 origin 出发、还剩 budget AP，回合结束能做到的最低风险。返回 (风险, 格子)。"""
        best = (self.risk(origin, budget, weaken), origin)
        base = self.team_loss(frozenset(), weaken)
        for cell in self.safe_cells():
            if base + self.place_cost(cell) >= best[0]:
                break
            cost = self.move_cost(origin, cell)
            if cost is not None and cost <= budget:
                r = base + self.place_cost(cell) + MOVE_AP_COST * cost
                if r < best[0]:
                    best = (r, cell)
                break
        for cell in self.special_cells():
            if cell is origin:
                continue
            cost = self.move_cost(origin, cell)
            if cost is None or cost > budget:
                continue
            r = self.risk(cell, budget - cost, weaken) + MOVE_AP_COST * cost
            if r < best[0]:
                best = (r, cell)
        return best


# ----------------------------------------------------------------------------
# 一次攻击的效果
# ----------------------------------------------------------------------------

def uniform_capped_mean(low, high, cap):
    """均匀分布 U(low, high) 截断到 cap 的期望。"""
    if high <= low:
        return min(low, cap)
    if cap >= high:
        return (low + high) / 2
    if cap <= low:
        return cap
    return ((cap * cap - low * low) / 2 + cap * (high - cap)) / (high - low)


def uniform_at_least(low, high, x):
    if high <= low:
        return 1.0 if low >= x else 0.0
    return min(1.0, max(0.0, (high - x) / (high - low)))


def damage_spread(states, outcomes, remaining, attacks):
    """连续打 attacks 下，累计伤害（封顶到 remaining）的分布 {伤害: 概率}。"""
    for _ in range(attacks):
        nxt = {}
        for dealt, p in states.items():
            if dealt >= remaining:
                nxt[dealt] = nxt.get(dealt, 0.0) + p
                continue
            for q, d in outcomes:
                if q > 0:
                    v = min(remaining, round(dealt + d, 3))
                    nxt[v] = nxt.get(v, 0.0) + p * q
        states = nxt
    return states


def hit_effect(char, weapon, ammo, zombie, part, attacks=1):
    """(平均每下马上削弱它的比例, 平均每下长远削弱的比例, 命中率)，按这回合连续打这个部位 attacks 下算。
    头：打死的概率 / 击杀进度；躯干：攻击力按剩余躯干血量成比例下降；腿：行动力按剩余腿血量成比例下降。
    （只看一下的话，打躯干总是立刻见效、打头要暴击才有用；连砍两三下能把头砍掉时打头更好）"""
    hp = zombie.hp.get(part, 0)
    remaining = hp + (getattr(zombie, "armor", None) or {}).get(part, 0)
    if hp <= 0 or remaining <= 0:
        return 0.0, 0.0, 0.0
    acc = max(0.0, min(100.0, char.get_accuracy(weapon, ammo, zombie, part))) / 100
    low, high = char.get_damage(weapon, ammo, zombie, part)
    crit = 0.0
    if part == "head" and char.entity_type != "npc":
        crit = max(0.0, min(100.0, char.get_critical_hit_chance(weapon, ammo, zombie, part))) / 100
    # 游戏先判暴击（×5 伤害），没暴击再按命中率判命中
    dealt = crit * uniform_capped_mean(low * 5, high * 5, remaining) + \
        (1 - crit) * acc * uniform_capped_mean(low, high, remaining)
    broken = crit * uniform_at_least(low * 5, high * 5, remaining) + \
        (1 - crit) * acc * uniform_at_least(low, high, remaining)
    if attacks > 1:
        hit = (1 - crit) * acc
        outcomes = [(1 - crit - hit, 0.0), (hit / 2, low), (hit / 2, high), (crit / 2, low * 5), (crit / 2, high * 5)]
        states = damage_spread({0.0: 1.0}, outcomes, remaining, attacks)
        dealt = sum(d * p for d, p in states.items())
        broken = sum(p for d, p in states.items() if d >= remaining)
    attacks = max(1, attacks)
    share = dealt / remaining / attacks
    if part == "head":
        return broken / attacks, share, acc
    if part == "body":
        return share * 0.95, share * 0.85, acc   # 没了躯干还得补刀，也还能咬流血的人
    return share * 0.8, share * 0.7, acc        # 没了腿还能爬


def zombie_potential(zombie):
    """僵尸平常一回合大约能造成的伤害（没有对上具体的人时用），封顶 POTENTIAL_CAP：
    不然大块头的「长远威胁」会大过自己的死亡风险，打到 AP 用光、站在它旁边等死。"""
    return min(POTENTIAL_CAP, max(1.0, zombie.action_power or 0) * (zombie.attack_power or 0) * 0.6)


def noise_cost(char, weapon):
    if not QUIET_COMBAT or not is_noisy(weapon):
        return 0.0
    zombies = sorted(char.loc.zombies, key=lambda z: dist(z.cell, char.cell))[:40]
    idle = 0
    for zombie in zombies:
        target = getattr(zombie, "target", None)
        if target is None or getattr(target, "char", None) is None:
            idle += 1
    return NOISE_COST_PER_ZOMBIE * min(idle, 15)


def attack_ap_cost(char, weapon, zombie, part):
    from core.creature.combat import get_attack_ap
    return 1 + get_attack_ap(char, weapon, zombie, part)


def wear_cost(char, weapon, part, acc):
    """打这一下预计的武器磨损，折算成 HP（照游戏的 degrade_condition_melee，打空不磨损，不含技能减免）。"""
    if WEAR_COST <= 0 or weapon.atktype != "melee":
        return 0.0
    low, high = getattr(weapon, "condition_loss_combat", None) or (0, 0)
    loss = (low + high) / 2 * (1 - (getattr(weapon, "durability", 0) or 0) / 13)
    try:
        config = dt().balance_config
        loss = max(config.get("melee_condition_loss_min", 0), loss) * config.get("condition_loss_mult", {}).get(part, 1)
    except Exception:
        pass
    spare = any(i is not weapon for i in usable_melee(char.items))
    return acc * min(loss, getattr(weapon, "condition", 0) or 0) * (WEAR_COST if spare else WEAR_COST_LAST)


def in_reach(weapon, cell, target_cell):
    ca = STATE.ca
    reach = weapon.reach if weapon.atktype == "melee" else weapon.range
    return ca.cy.caldist(*cell.coord, *target_cell.coord) <= reach and \
        ca.can_attack_with_weapon(weapon, cell, target_cell)


# ----------------------------------------------------------------------------
# 回合规划
# ----------------------------------------------------------------------------

class Option:
    __slots__ = ("score", "zombie", "weapon", "ammo", "part", "pathdic", "gain", "end", "note")

    def __init__(self, score, zombie, weapon, ammo, part, pathdic, gain, end, note):
        self.score, self.zombie, self.weapon, self.ammo, self.part = score, zombie, weapon, ammo, part
        self.pathdic, self.gain, self.end, self.note = pathdic, gain, end, note


def attack_positions(board, weapon, zombie):
    """[(格子, pathdic)]：走过去还能出手（剩 ≥1 AP）、打得到 zombie 的格子，最省 AP 的几个。"""
    char = board.char
    found = []
    for cell, pathdic in board.pds.items():
        if cell is char.cell or char.ap - pathdic["ap"] < 1:
            continue
        if getattr(cell, "zombies", None) or getattr(cell, "char", None) not in (None, char):
            continue
        if in_reach(weapon, cell, zombie.cell):
            found.append((cell, pathdic))
    found.sort(key=lambda cp: cp[1]["ap"])
    return found[:5]


def best_option(board, end_now):
    char = board.char
    targets = sorted(threats_of(char), key=lambda z: dist(z.cell, char.cell))[:8]
    # 紧迫：下回合就能打到队里的人。不紧迫的僵尸现在去打等于送上门（它自己会走过来，
    # 走过来花掉行动力，下回合我们满 AP 先手），长远收益按低权重算
    members = [m.cell for m in party(char)]
    urgent = {z for z in targets if any(zombie_attacks_at(z, c) for c in members)}
    best = None
    for weapon in candidate_weapons(char):
        ammo = ammo_for(char, weapon)
        if weapon.atktype == "ranged" and ammo is None:
            continue
        extra = noise_cost(char, weapon) + (AMMO_COST if weapon.atktype == "ranged" else 0.0)
        for zombie in targets:
            if in_reach(weapon, char.cell, zombie.cell):
                spots = [(char.cell, None)]
            else:
                spots = attack_positions(board, weapon, zombie)
            if not spots:
                continue
            # 部位只影响收益和攻击 AP，先按收益挑好；回合结束风险每个位置只算一次。
            # 按这回合还能连续打几下来比较部位（AP 扣到 0 为止，剩 1 AP 也能出手）
            spare = char.ap - (spots[0][1]["ap"] if spots[0][1] else 0)
            attacks = max(1, min(6, math.ceil(spare / attack_ap_cost(char, weapon, zombie, "head"))))
            # 下回合的效果（它少打谁几下、打死了就不打了）由 weaken 在整个队伍的回合结束风险里体现，
            # 这里的收益只算长远的：它以后几回合少造成的伤害
            part, gain, now, value, wear = None, 0.0, 0.0, None, 0.0
            for p in PARTS:
                p_now, p_later, p_acc = hit_effect(char, weapon, ammo, zombie, p, attacks)
                g = (FUTURE_WEIGHT if zombie in urgent else FUTURE_WEIGHT_IDLE) * zombie_potential(zombie) * p_later
                if p == "head":
                    g += CLEAR_VALUE * p_now     # p_now 是平均每下打死的概率
                p_wear = wear_cost(char, weapon, p, p_acc)
                v = g + zombie_potential(zombie) * p_now - p_wear     # 只用来先挑部位
                if value is None or v > value:
                    part, gain, now, value, wear = p, g, p_now, v, p_wear
            if value <= 0:
                continue
            cost = attack_ap_cost(char, weapon, zombie, part)
            ranged = weapon.atktype == "ranged"
            for cell, pathdic in spots:
                move = pathdic["ap"] if pathdic else 0
                # 打这一下就停手（打完还能跑）
                plans = [(1, now, gain, extra + wear)]
                if COMMIT_PLANNING and attacks > 1:
                    # 打完这回合能打的（AP 用光）；用 attacks 下累计的效果
                    plans.append((attacks, min(1.0, now * attacks), gain * attacks, extra + wear * attacks + (AMMO_COST * (attacks - 1) if ranged else 0.0)))
                for n, effect, g, cost_extra in plans:
                    end = outcome_risk(board, cell, max(0.0, char.ap - move - cost * n), zombie, part, effect)
                    score = g - (end - end_now) - cost_extra - MOVE_AP_COST * move
                    if best is None or score > best.score:
                        note = "，不得不开枪" if QUIET_COMBAT and is_noisy(weapon) else ""
                        best = Option(score, zombie, weapon, ammo, part, pathdic, g, end, note)
    return best


def outcome_risk(board, cell, budget, zombie, part, effect):
    """打完之后回合结束的风险。打头是「打死 / 没打死」两种结局（打完知道结果，可以分别挑站位），
    按概率加权，这样「一半概率砍不死、然后 0 AP 站在它旁边挨 5 下」的风险不会被平均掉；
    打躯干 / 腿是按比例削弱它的伤害。"""
    if part == "head":
        alive = board.end_risk(cell, budget)[0]
        if effect <= 0:
            return alive
        dead = board.end_risk(cell, budget, {zombie: 1.0})[0]
        return (1 - effect) * alive + effect * dead
    return board.end_risk(cell, budget, {zombie: effect})[0]


def wait_effects(char):
    scene = dt().scene
    for _ in range(100):
        busy = scene.effect.is_animating or (char.spr is not None and char.spr.is_animating)
        if not busy:
            return
        time.sleep(0.1)


def fight(char):
    """本回合的战斗：一步步挑收益最高的攻击，直到不划算或没 AP，最后站到风险最低的格子。"""
    ca = STATE.ca
    char.intention = "attack"
    attack_zombies = settings(char).get("attack_zombies", True)
    for _ in range(30):
        if char.ap < 1 or not attack_zombies or not threats_of(char):
            break
        board = Board(char)
        end_now, _ = board.end_risk(char.cell, char.ap)
        option = best_option(board, end_now)
        if option is None or option.score < MIN_ATTACK_GAIN:
            break
        zombie = option.zombie
        if option.pathdic is not None:
            # 走过去再打是一个整体：走到了就按计划出手，避免走一步又改主意来回跑
            decision("%s 走到能打 %s 的位置" % (name_of(char), name_of(zombie)))
            start = char.cell
            ca.move(char, option.pathdic["path"])
            wait_effects(char)
            if char.cell is start:
                break       # 走不过去（门、障碍），别原地打转
            if char.ap < 1 or zombie not in char.loc.zombies or not in_reach(option.weapon, char.cell, zombie.cell):
                continue
        decision("%s 用 %s 打 %s 的%s（收益 %.1f，回合结束预计损失 %.1f%s）" % (
            name_of(char), name_of(option.weapon), name_of(zombie), PART_NAMES[option.part],
            option.gain, option.end, option.note))
        attack_with_plan(char, zombie, lambda: char.attack_for_companion(zombie, option.weapon, option.ammo),
                         part=option.part)
        wait_effects(char)
    final_position(char)
    close_doors(char)
    if threats_of(char):
        char.intention = "waiting_to_attack"   # 原版约定：之后不再去搜刮


def final_position(char, why="回合结束前"):
    """站到这回合走得到的、预计损失最低的格子。返回是否移动了。"""
    if not char.loc.zombies or char.ap <= 0:
        return False
    board = Board(char)
    here = board.risk(char.cell, char.ap)
    best, cell = board.end_risk(char.cell, char.ap)
    if cell is char.cell or here - best < MIN_REPOSITION_GAIN:
        return False
    decision("%s %s换到更安全的位置（预计损失 %.1f → %.1f）" % (name_of(char), why, here, best))
    char.intention = "flee"
    STATE.ca.move(char, board.pds[cell]["path"])
    wait_effects(char)
    return True


def door_is_open(door):
    try:
        return bool(door.is_open)
    except Exception:
        return False


def close_doors(char):
    """站在门边、关上门能让整个队伍明显少挨打时关门（花 0.5 AP）。返回是否关了。"""
    if not CLOSE_DOORS or char.ap < 0.5 or not char.loc.zombies:
        return False
    doors = [d for d in (getattr(char.cell, "doors", None) or ())
             if door_is_open(d) and (getattr(d, "hp", 0) or 0) > 0 and not any(c.char or c.zombies for c in d.cells if c is not char.cell)]
    if not doors:
        return False
    here = Board(char).risk(char.cell, char.ap)
    best = None
    for door in doors:
        r = Board(char, closed={door}).risk(char.cell, char.ap - 0.5)
        if here - r >= MIN_REPOSITION_GAIN and (best is None or r < best[0]):
            best = (r, door)
    if best is None:
        return False
    decision("%s 关上门挡僵尸（预计损失 %.1f → %.1f）" % (name_of(char), here, best[0]))
    char.close(best[1])
    return True


def in_danger(char):
    """现在站的格子下回合会不会挨打。"""
    return any(zombie_attacks_at(z, char.cell) for z in char.loc.zombies)


# ----------------------------------------------------------------------------
# 原版流程里的选目标 / 武器 / 部位（DLC 等交给原版攻击流程时用）
# ----------------------------------------------------------------------------

def part_score(char, weapon, ammo, zombie, part):
    now, later, acc = hit_effect(char, weapon, ammo, zombie, part)
    return now + FUTURE_WEIGHT * later, acc


def best_part(char, weapon, ammo, zombie):
    """(部位, (得分, 命中率, 平均伤害))"""
    best = ("head", (0.0, 0.0, 0.0))
    for part in PARTS:
        if zombie.hp.get(part, 0) <= 0:
            continue
        score, acc = part_score(char, weapon, ammo, zombie, part)
        if score > best[1][0]:
            low, high = char.get_damage(weapon, ammo, zombie, part)
            best = (part, (score, acc, (low + high) / 2))
    return best


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
    """这把武器这回合对 zombie 大约能造成的削弱（多次攻击累加）。"""
    ca = STATE.ca
    ammo = ammo_for(char, weapon)
    if weapon.atktype == "ranged" and ammo is None:
        return -1.0, None
    part, (score, _, _) = best_part(char, weapon, ammo, zombie)
    attack_ap = attack_ap_cost(char, weapon, zombie, part)
    if in_reach(weapon, char.cell, zombie.cell):
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
    return score * attacks + condition * 1e-4, part


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
    if QUIET_COMBAT and is_noisy(best):
        quiet = [s for s in scored if not is_noisy(s[1])]
        if quiet:
            q = max(quiet, key=lambda s: s[0])
            if q[0] > 0 and q[0] >= value * QUIET_MIN_RATIO:
                value, best, part = q
    return best


def head_remaining(zombie):
    return zombie.hp.get("head", 0) + (getattr(zombie, "armor", None) or {}).get("head", 0)


def choose_target(obj, targets):
    """替换 compaction.get_closest_target：按「打一下能拆掉它多少威胁」挑，路程作为次要因素。"""
    closest = STATE.orig["get_closest_target"]
    try:
        zombies = [t for t in targets if t.entity_type == "zombie"]
        if not zombies or len(zombies) != len(targets):
            return closest(obj, targets)
        cells = [m.cell for m in party(obj)]

        def value(z):
            weapon = STATE.orig["get_weapon"](obj)
            score = best_part(obj, weapon, ammo_for(obj, weapon), z)[1][0] if weapon else 0.0
            urgent = any(zombie_attacks_at(z, c) for c in cells)
            return (urgent, score * zombie_potential(z) - 0.1 * dist(obj.cell, z.cell))

        return max(zombies, key=value)
    except Exception:
        log_exc("选择攻击目标", once=True)
        return closest(obj, targets)


def attack_with_plan(char, target, func, part=None):
    """在本 MOD 的指挥下发动攻击：process_attack 换成指定（或按效果挑的）部位，get_weapon 按目标挑武器。"""
    local = STATE.local
    prev = (getattr(local, "attacker", None), getattr(local, "target", None),
            getattr(local, "weapon", None), getattr(local, "part", None))
    local.attacker, local.target, local.weapon, local.part = char, target, None, part
    try:
        return func()
    finally:
        local.attacker, local.target, local.weapon, local.part = prev


# ----------------------------------------------------------------------------
# 自动穿护甲 / 战斗时不做杂事
# ----------------------------------------------------------------------------

def own_items(char):
    """char 自己身上的物品（装了队伍背包共享 MOD 时不含别人的）。"""
    return list(getattr(char.items, "_raw", char.items))


def dress_up(char):
    """把自己背包里有防御的衣物穿上（不脱任何东西），总负重不超过 DRESS_MAX_ENCUMBRANCE。"""
    if not AUTO_DRESS or in_danger(char):
        return
    worn = list(getattr(char, "worn_items", ()) or ())
    worn_names = {getattr(i, "name", None) for i in worn}
    has_gloves = any(getattr(i, "subcategory", None) == "gloves" for i in worn)
    clothes = []
    for item in own_items(char):
        if getattr(item, "category", None) != "clothing" or item in worn or not hasattr(item, "put_on"):
            continue
        if getattr(item, "name", None) in worn_names or (has_gloves and getattr(item, "subcategory", None) == "gloves"):
            continue
        defense = getattr(item, "defense", 0) or 0
        if defense > 0:
            clothes.append((defense / max(0.5, getattr(item, "encumbrance", 1) or 1), defense, item))
    for _, defense, item in sorted(clothes, key=lambda c: (c[0], c[1]), reverse=True):
        encumbrance = (getattr(char, "encumbrance", 0) or 0) + (getattr(item, "encumbrance", 0) or 0)
        if encumbrance > DRESS_MAX_ENCUMBRANCE or item.get_unavailables(char):
            continue
        decision("%s 穿上 %s（防御 +%.1f，负重 %.1f）" % (name_of(char), name_of(item), defense, encumbrance))
        item.put_on(char)
        worn_names.add(getattr(item, "name", None))


def make_combat_locked(name, orig):
    """僵尸能打到自己时不做这件杂事，留着 AP。"""
    def locked(comp, *args, **kwargs):
        try:
            if COMBAT_LOCK and in_danger(comp):
                decision("%s 僵尸能打到这里，先不%s，留着 AP" % (name_of(comp), LOCKED_TASKS.get(name, "做杂事")))
                return None
        except Exception:
            log_exc("战斗时不做杂事（%s）" % name, once=True)
        return orig(comp, *args, **kwargs)
    locked.__name__ = name
    return locked


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
    try:
        dress_up(comp)
    except Exception:
        log_exc("穿护甲", once=True)
    try:
        arm_up(comp)
    except Exception:
        log_exc("拿武器", once=True)
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


def usable_melee(items):
    return [i for i in items if is_melee(i) and (getattr(i, "condition", 0) or 0) >= ARM_MIN_CONDITION]


def arm_up(comp):
    """身上没有能用的近战武器时，从你和其他队友包里拿一把多余的（每人至少留一把，不拿你最好的和最爱的）。"""
    if not ARM_FROM_PARTY or comp is dt().game.player or usable_melee(comp.items):
        return
    best = None
    for member in party(comp):
        if member is comp:
            continue
        mine = usable_melee(own_items(member))
        if len(mine) < 2:
            continue
        keep = max(mine, key=melee_score)
        favorite = getattr(member, "favorite_weapon", None)     # 游戏里是 (武器, 时间, 名字)
        favorite = favorite[0] if isinstance(favorite, (tuple, list)) and favorite else favorite
        for item in mine:
            if item is keep or item is favorite:
                continue
            if best is None or melee_score(item) > melee_score(best[1]):
                best = (member, item)
    if best is None:
        return
    member, item = best
    source, target = getattr(member, "_items", None), getattr(comp, "_items", None)
    if source is None or target is None:
        return
    source.remove(item)
    list.append(target, item)        # 不弹「获得物品」提示
    if hasattr(target, "check"):
        target.check()
    decision("%s 手上没有能用的武器，从 %s 包里拿了 %s" % (name_of(comp), name_of(member), name_of(item)))


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
                    # 按回合结束风险挑位置（离你近、又不送上门）；原版的靠近不管危险
                    if PLANNER:
                        final_position(comp, "回你身边：")
                    else:
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

def use_planner(comp):
    if not PLANNER:
        return False
    # DLC 里有原版攻击流程才处理的任务判定和特殊角色，交给原版（目标 / 武器 / 部位仍由本 MOD 挑）
    return not getattr(dt().game, "is_dlc", False)


def handle_attack(comp):
    """有看得见的僵尸时用回合规划打；只有敌对 NPC、或 DLC 时交给原版攻击流程。"""
    try:
        if use_planner(comp) and threats_of(comp):
            from core import effects
            if not settings(comp).get("attack") or effects.gated(comp, "attack"):
                return None
            fight(comp)
            if threats_of(comp) or not settings(comp).get("attack_npcs"):
                return None
            # 僵尸打完了，剩下敌对 NPC 交给原版
    except Exception:
        log_exc("回合规划", once=True)
    return STATE.orig["handle_attack"](comp)


def move_and_attack(comp, target):
    orig = STATE.orig["move_and_attack"]
    if target.entity_type != "zombie":
        return orig(comp, target)
    return attack_with_plan(comp, target, lambda: orig(comp, target))


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
        if final_position(comp, "躲开僵尸："):
            return None
    except Exception:
        log_exc("逃跑", once=True)
    return STATE.orig["flee"](comp)


def handle_food_consumption(comp, *args, **kwargs):
    # 原版在每回合任务做完后调用它（睡前也会调用），借这个时机做「回合结束前换到安全格」
    try:
        if SAFE_STEP and not comp.sleeping and comp.ap >= 1:
            final_position(comp)
        if not comp.sleeping:
            close_doors(comp)
    except Exception:
        log_exc("回合结束前避险", once=True)
    return STATE.orig["handle_food_consumption"](comp, *args, **kwargs)


def move_closer_to_player(comp, *args, **kwargs):
    """修原版 bug：队友一步都走不了（AP 用光、被围住）时，原版对空列表取 min() 抛异常，
    整个队友回合中断。这时什么都不做。"""
    try:
        return STATE.orig["move_closer_to_player"](comp, *args, **kwargs)
    except ValueError:
        log_exc("靠近主角（原版在走不了时报错，已跳过）", once=True)
        return None


def companion_action(*args, **kwargs):
    """原版逐个让队友行动；这里先排好顺序：离看得见的僵尸近的先行动（先打、先躲），
    后面的人看着前面的结果再决定。行动完恢复原来的顺序。"""
    game = dt().game
    original = list(game.companions)
    try:
        if ORDER_COMPANIONS and len(original) > 1:
            def engaged(comp):
                threats = threats_of(comp) if comp.loc is not None else []
                return min((dist(z.cell, comp.cell) for z in threats), default=999)
            game.companions.sort(key=engaged)
    except Exception:
        log_exc("排行动顺序", once=True)
    try:
        return STATE.orig["companion_action"](*args, **kwargs)
    finally:
        try:
            current = list(game.companions)
            ordered = [c for c in original if c in current] + [c for c in current if c not in original]
            game.companions[:] = ordered
        except Exception:
            log_exc("恢复队友顺序", once=True)


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
                chosen = getattr(local, "part", None)
                if chosen is None:
                    weapon = args[0] if args else kwargs.get("weapon")
                    ammo = args[1] if len(args) > 1 else kwargs.get("ammo")
                    chosen, (_, acc, avg) = best_part(char, weapon, ammo, target)
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
        "handle_medication": handle_medication,
        "handle_food_consumption": handle_food_consumption,
        "get_filtered_pickup_items_for_cell": filtered_pickup_items,
        "handle_search_or_pickup": handle_search_or_pickup,
        "companion_action": companion_action,
        "move_closer_to_player": move_closer_to_player,
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
    for name in LOCKED_TASKS:
        current = getattr(module, name, None)
        if current is None:
            log("找不到 compaction.%s，跳过战斗时不做杂事" % name)
            continue
        setattr(module, name, make_combat_locked(name, current))
    # 护卫套在最外层（搜刮套的是上面换过的版本）
    for name in GUARDED_TASKS:
        current = getattr(module, name, None)
        if current is None:
            log("找不到 compaction.%s，跳过护卫" % name)
            continue
        setattr(module, name, make_guarded(name, current))
    module._companion_ai_patched = True
    log("已替换队友 AI：回合规划（威胁模型、选目标 / 武器 / 部位 / 站位）、安静作战、用药与互相治疗、自动穿护甲、没武器时从队伍拿、护卫、拾取、战斗时不做杂事")


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
