# -*- coding: utf-8 -*-
"""
Terminus: Zombie Survivors - 主角 AI 托管 MOD

在动作面板上加一个「AI 托管」按钮（机器人图标），让游戏自带的队友 AI 接管主角：

  左键按钮 / F6        AI 接管主角行动一回合，行动完自动结束回合
  Shift+F6             开启持续托管（每回合自动托管）；持续托管中再按 F6 或点按钮即停止
  右键按钮             菜单：托管本回合 / 开关持续托管 / AI 行为设置
  AI 行动中            点按钮（变成停止图标）、按 F6 或 Esc：立即停止，本回合不结束，交还操作

「AI 行为设置」打开的是游戏原生的队友自动化设置面板，只对主角生效：
可以单独设置主角要不要攻击、搜刮价值阈值、吃喝/睡觉/读书/种田等。
（面板顶部的总开关对主角无意义，忽略即可。）

托管回合跑在玩家回合里，而原版有一批只在玩家回合才弹的窗口（工具碎裂、读完书获得技能/属性、
学会配方、重读确认、升级选技能）。托管期间玩家输入是关着的，这些弹窗点不掉，重读确认框还会让
AI 0 AP 反复「读完」同一本书。所以托管期间：普通弹窗改成头顶飘字，重读和队友一样直接重读，
升级选技能等回合交还后再弹。另有卡住保护：AI 行动时 AP 和位置长时间不变就中止托管、交还操作，
并把所有线程的调用栈写进日志。

原版队友 AI 寻路时只把铁丝网当成「不能斜穿」，近战去打铁丝网另一边的僵尸时会走到网边、隔着网打不到，
搜刮途中每走一步又回头去打，来回跑到 AP 耗尽。托管主角用近战攻击时，把完好的铁丝网当成墙
（打不到的僵尸直接跳过，绕得过去的照常绕过去打）。原版寻路按步数算，翻网（10 AP）也只算一步，
托管主角的路线要翻网时，改走实际 AP 更少的绕行路线。

本文件由 base_library.zip 中的引导代码在游戏启动时加载，出错会写入同目录的 ai_takeover.log，
不会让游戏崩溃。
"""

import ctypes
import os
import re
import sys
import threading
import time
import traceback
from functools import partial

# ----------------------------------------------------------------------------
# 可调整的配置
# ----------------------------------------------------------------------------

# 快捷键（pygame 按键名，例如 "F6"、"F7"、"v"）
HOTKEY = "F6"

# 持续托管时，主角 HP 低于最大值的这个比例就自动停止持续托管（0 = 永不因 HP 停止）
CONTINUOUS_STOP_HP_RATIO = 0.35

# 持续托管时，每个玩家回合开始后等待多少秒再接管（方便看清局势、随时按 F6 叫停）
CONTINUOUS_DELAY = 0.6

# AI 行动时主角的 AP、位置、物品、士气这么多秒都没变化，就认为卡住：中止托管并交还操作
STALL_TIMEOUT = 40.0

# 结束回合（队友、NPC、僵尸行动）超过这么多秒，把各线程调用栈写进日志（只记录，不打断）
SLOW_TURN_DUMP = 90.0

# 点了「停止」后，AI 这么多秒内还没走到能安全停下的地方，就强制中止
STOP_GRACE = 3.0

# 托管回合内 Python 线程切换间隔（秒）。AI 线程算路径时会抢走渲染线程的全局锁，
# 原版默认 0.005，调小后画面不容易掉帧；回合结束后恢复原值。0 = 不调整
AI_SWITCH_INTERVAL = 0.0005

# 托管回合内一帧超过这么多秒就把 AI 当时在做的事写进日志（方便排查卡顿），0 = 不记录
HITCH_LOG = 0.2

# ----------------------------------------------------------------------------

ACTION = "ai_takeover"
ACTION_STOP = "ai_takeover_stop"
MOD_DIR = os.path.dirname(os.path.abspath(__file__))

# MOD 加载器据此决定把哪些游戏模块的加载事件通知给本 MOD
PATCH_TARGETS = (
    "ui.panels.actionpanel",
    "scenes.main",
    "ui.popups.defaultpopup",
    "ui.popups.perkpopup",
    "core.item.book",
    "actions.compaction",
    "actions.path",
    "actions.utils",
)
LOG_PATH = os.path.join(MOD_DIR, "ai_takeover.log")

# 13x13 图标，与原生动作图标同色同尺寸
ICON_ROWS = (
    "......#......",
    "......#......",
    "...#######...",
    "..#.......#..",
    "..#.##.##.#..",
    "..#.##.##.#..",
    "###.......###",
    "..#.......#..",
    "..#.#####.#..",
    "..#.......#..",
    "...#######...",
    "....#...#....",
    "...###.###...",
)
# AI 行动中按钮换成「停止」图标
ICON_STOP_ROWS = (
    ".............",
    ".............",
    "..#########..",
    "..#########..",
    "..#########..",
    "..#########..",
    "..#########..",
    "..#########..",
    "..#########..",
    "..#########..",
    "..#########..",
    ".............",
    ".............",
)
ICON_COLOR = (233, 233, 233, 255)

# 依次为：简体中文、繁体中文、其他语言
TEXT = {
    "name": ("AI 托管", "AI 託管", "AI Takeover"),
    "desc": (
        "让 AI 控制主角行动一回合，行动完自动结束回合。",
        "讓 AI 控制主角行動一回合，行動完自動結束回合。",
        "Let the AI control your main character for one turn, then end the turn.",
    ),
    "hint_keys": (
        "{key}：托管一回合　Shift+{key}：持续托管　右键：更多",
        "{key}：託管一回合　Shift+{key}：持續託管　右鍵：更多",
        "{key}: one turn  Shift+{key}: continuous  Right-click: more",
    ),
    "continuous_on": (
        "持续托管中。点击按钮或按 {key} 停止。",
        "持續託管中。點擊按鈕或按 {key} 停止。",
        "Continuous takeover is ON. Click or press {key} to stop.",
    ),
    "menu_once": ("托管本回合", "託管本回合", "Take over this turn"),
    "menu_cont_on": ("开启持续托管", "開啟持續託管", "Start continuous takeover"),
    "menu_cont_off": ("停止持续托管", "停止持續託管", "Stop continuous takeover"),
    "menu_settings": ("AI 行为设置", "AI 行為設定", "AI behavior settings"),
    "float_turn": ("AI 托管", "AI 託管", "AI"),
    "float_cont_on": ("持续托管：开", "持續託管：開", "Continuous AI: ON"),
    "float_cont_off": ("持续托管：关", "持續託管：關", "Continuous AI: OFF"),
    "float_hp_stop": ("HP 过低，停止持续托管", "HP 過低，停止持續託管", "HP low, continuous AI stopped"),
    "float_error": ("AI 托管出错（见 ai_takeover.log）", "AI 託管出錯（見 ai_takeover.log）", "AI takeover error (see ai_takeover.log)"),
    "float_stalled": (
        "AI 托管卡住，已停止，请手动操作（见 ai_takeover.log）",
        "AI 託管卡住，已停止，請手動操作（見 ai_takeover.log）",
        "AI takeover got stuck and stopped, please act manually (see ai_takeover.log)",
    ),
    "float_endturn_error": (
        "结束回合出错，已交还操作（见 ai_takeover.log）",
        "結束回合出錯，已交還操作（見 ai_takeover.log）",
        "Ending the turn failed, control returned (see ai_takeover.log)",
    ),
    "cause_state": ("主角当前无法行动", "主角目前無法行動", "Your character can't act right now"),
    "name_stop": ("停止托管", "停止託管", "Stop AI"),
    "desc_stop": (
        "AI 正在控制主角。点击、按 {key} 或 Esc 立即停止，本回合不结束，交还操作。",
        "AI 正在控制主角。點擊、按 {key} 或 Esc 立即停止，本回合不結束，交還操作。",
        "The AI is controlling your character. Click, press {key} or Esc to stop now; the turn does not end.",
    ),
    "float_stopping": ("正在停止托管…", "正在停止託管…", "Stopping AI..."),
    "float_stopped": ("已停止托管，请手动操作", "已停止託管，請手動操作", "AI stopped, your turn"),
}


class AIAbort(BaseException):
    """中止 AI 行动的异常。继承 BaseException，免得被游戏或其他 MOD 的 except Exception 吞掉。"""


class AIStalled(AIAbort):
    """看门狗发现 AI 卡住时注入 AI 线程。"""


class AIInterrupted(AIAbort):
    """玩家点了「停止托管」。"""


class _State:
    busy = False            # AI 正在行动 / 正在结束回合
    acting = False          # AI 正在替主角行动（这期间的弹窗改成飘字）
    continuous = False      # 持续托管开关
    ready_since = None      # 持续托管：本回合可接管的起始时间
    logged = set()          # 每帧调用处的错误只记录一次
    worker = None           # 当前 AI 回合线程
    guard_lock = threading.Lock()
    guard_armed = False     # 看门狗可以向 AI 线程注入 AIStalled
    pending_perk = None     # 托管期间升级：(perk_popup, 角色)，回合交还后再弹
    orig_perk_open = None
    task = ""               # 正在执行的子任务（写进卡住时的诊断日志）
    stop_requested = False  # 玩家要求立即停止本回合托管
    local = threading.local()   # block_fences：近战寻路时把铁丝网当墙
    ca = None               # actions.compaction
    last_frame = None       # 上一帧时间（卡顿诊断）
    hitches = 0
    saved_interval = None   # 托管前的线程切换间隔


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
    log("%s 出错:\n%s" % (where, traceback.format_exc()))


def t(key, **kwargs):
    from data import dt
    lang = getattr(dt.settings, "lang", "English")
    idx = {"Simplified Chinese": 0, "Traditional Chinese": 1}.get(lang, 2)
    return TEXT[key][idx].format(key=HOTKEY, **kwargs)


def float_text(char, text, color=None):
    """在角色头顶飘字。"""
    try:
        from data import dt
        from paint.color import YELLOW
        dt.scene.result.add(text, char, color or YELLOW)
    except Exception:
        log_exc("float_text", once=True)


def hotkey_code():
    import pygame
    for name in ("K_" + HOTKEY, "K_" + HOTKEY.upper(), "K_" + HOTKEY.lower()):
        if hasattr(pygame, name):
            return getattr(pygame, name)
    return pygame.K_F6


def ensure_icon():
    from paint.grapher import Images
    import pygame
    for action, rows in ((ACTION, ICON_ROWS), (ACTION_STOP, ICON_STOP_ROWS)):
        key = "assets/image/action/%s.png" % action
        if key in Images.cache:
            continue
        surf = pygame.Surface((13, 13), pygame.SRCALPHA)
        for y, row in enumerate(rows):
            for x, ch in enumerate(row):
                if ch == "#":
                    surf.set_at((x, y), ICON_COLOR)
        Images.cache[key] = surf


def ensure_settings(char):
    """旧存档里的主角可能没有自动化设置，补上默认值。"""
    if not isinstance(getattr(char, "automation_settings", None), dict):
        from core.constants import DEFAULT_AUTOMATION_SETTINGS
        char.automation_settings = DEFAULT_AUTOMATION_SETTINGS.copy()


# ----------------------------------------------------------------------------
# AI 本体：复用游戏队友 AI（actions/compaction.py）的各个子任务
# ----------------------------------------------------------------------------

# 与 companion_action() 的任务顺序一致。去掉了只对队友有意义的
# 「跟随玩家换地图」「跟随玩家到终点站」「靠近玩家」三项。
TASKS = (
    ("sleep", "check_sleep", "handle_sleep"),
    ("attack", "check_attack", "handle_attack"),
    ("vehicle_repairs", "check_vehicle_repairs", "handle_vehicle_repairs"),
    ("clearing_debris", "check_clearing_debris", "handle_clearing_debris"),
    ("search_or_pickup", "check_search_or_pickup", "handle_search_or_pickup"),
    ("upgrade", "check_upgrading", "handle_upgrade"),
    ("reading_morale", "check_reading_morale", "handle_reading_morale"),
    ("reading_skill", "check_reading_skill", "handle_reading_skill"),
    ("listening_music", "check_listening_music", "handle_listening_music"),
    ("crafting", "check_crafting", "handle_crafting"),
    ("mending", "check_mending", "handle_mending"),
    ("electronic_repairs", "check_electronic_repairs", "handle_electronic_repairs"),
    ("tool_repairs", "check_tool_repairs", "handle_tool_repairs"),
    ("plant_care", "check_plant_care", "handle_plant_care"),
)


def run_ai_for(char):
    """让 AI 控制 char 行动（在回合线程中调用）。返回执行的任务列表。"""
    from actions import compaction as ca
    from data import dt

    ensure_settings(char)
    scene = dt.scene
    scene.active_companion = char
    char.intention = None
    tasks = []
    try:
        STATE.task = "medication"
        ca.handle_medication(char)
        if not char.sleeping:
            for name, check, _ in TASKS:
                if getattr(ca, check)(char):
                    tasks.append(name)
        handlers = {name: handle for name, _, handle in TASKS}
        for name in tasks:
            check_stop()
            STATE.task = name
            getattr(ca, handlers[name])(char)
            if name == "sleep" and char.sleeping:
                break
        check_stop()
        if not char.sleeping:
            STATE.task = "food_and_drink"
            ca.handle_food_consumption(char)
            ca.handle_hydration(char)
            ca.handle_alcohol_consumption(char)
    finally:
        scene.active_companion = False
        STATE.task = ""
    return tasks


def wait_idle(scene, timeout=15.0):
    deadline = time.time() + timeout
    while time.time() < deadline and scene.char_animating:
        time.sleep(0.05)


# ----------------------------------------------------------------------------
# 卡住保护与诊断
# ----------------------------------------------------------------------------

def describe_state(scene, char):
    parts = []
    for label, get in (
        ("task", lambda: STATE.task),
        ("turn", lambda: scene.turn),
        ("allow_player_input", lambda: scene.allow_player_input),
        ("open_modals", lambda: [type(m).__name__ for m in scene.open_modals]),
        ("ap", lambda: char.ap),
        ("cell", lambda: getattr(char.cell, "pos", char.cell)),
        ("is_animating", lambda: char.spr.is_animating if char.spr else None),
        ("time_reserved_funcs", lambda: len(char.spr.time_reserved_funcs) if char.spr else None),
        ("char_animating", lambda: scene.char_animating),
    ):
        try:
            parts.append("%s=%r" % (label, get()))
        except Exception as e:
            parts.append("%s=<%s>" % (label, type(e).__name__))
    return ", ".join(parts)


def dump_threads(reason, scene=None, char=None):
    """把所有线程的调用栈写进日志，方便排查卡死。"""
    try:
        frames = sys._current_frames()
        lines = ["%s，各线程调用栈：" % reason]
        if scene is not None and char is not None:
            lines.append("状态: " + describe_state(scene, char))
        for th in threading.enumerate():
            frame = frames.get(th.ident)
            if frame is None:
                continue
            lines.append("--- 线程 %s ---" % th.name)
            lines.append("".join(traceback.format_stack(frame)).rstrip())
        log("\n".join(lines))
    except Exception:
        log_exc("dump_threads")


def _async_exc(thread, exc):
    """向线程注入异常（exc=None 表示撤销尚未触发的注入）。游戏自己的回合看门狗也用这个办法。"""
    ident = getattr(thread, "ident", None)
    if ident is None:
        return 0
    obj = None if exc is None else ctypes.py_object(exc)
    return ctypes.pythonapi.PyThreadState_SetAsyncExc(ctypes.c_ulong(ident), obj)


def arm_guard(worker):
    with STATE.guard_lock:
        STATE.worker = worker
        STATE.guard_armed = True


def disarm_guard():
    """关掉看门狗（AI 线程自己调用）。注入只在加锁且未关闭时发生，这里再撤销已注入但还没触发的，
    所以之后的结束回合不会被打断。"""
    with STATE.guard_lock:
        STATE.guard_armed = False
        _async_exc(threading.current_thread(), None)


def on_ai_thread():
    """当前在 AI 线程里、且 AI 正在替主角行动（不含结束回合）。"""
    return STATE.guard_armed and threading.current_thread() is STATE.worker


def check_stop():
    """AI 线程里能安全停下的地方（等动画、两次寻路之间、两个任务之间）调用：玩家要求停止就中止。"""
    if STATE.stop_requested and on_ai_thread():
        raise AIInterrupted()


def request_stop(scene):
    """主线程：玩家点了停止 / 按了 F6、Esc。"""
    from data import dt
    STATE.continuous = False
    STATE.ready_since = None
    if not STATE.acting:
        return
    if not STATE.stop_requested:
        STATE.stop_requested = True
        log("玩家要求立即停止托管（任务: %s）" % (STATE.task or "-"))
        float_text(dt.game.player, t("float_stopping"))


def progress_signature(char):
    """AI 是否还在推进：AP、位置、物品数、物品耐久总和、士气。修工具、听音乐这类不耗 AP 的连续动作
    会改变后几项，不会被误判为卡住。读取失败时返回新对象，按「有进展」处理。"""
    try:
        items = list(char.items)
        cond = sum(float(getattr(i, "condition", 0) or 0) for i in items)
        return (round(float(char.ap), 3), id(char.cell), bool(char.sleeping), len(items),
                round(cond, 1), round(float(getattr(char, "morale", 0) or 0), 2))
    except Exception:
        return object()


def watchdog(scene, char, worker):
    """AI 行动期间 AP / 位置长时间不变 → 中止；点了停止却迟迟停不下 → 强制中止；
    结束回合太久 → 只记录调用栈。"""
    last = progress_signature(char)
    since = time.time()
    started = since
    slow_dumped = False
    stop_since = None
    while worker.is_alive():
        time.sleep(0.2 if STATE.stop_requested else 0.5)
        now = time.time()
        with STATE.guard_lock:
            armed = STATE.guard_armed and STATE.worker is worker
        if not armed:
            if not slow_dumped and now - started > SLOW_TURN_DUMP:
                slow_dumped = True
                dump_threads("托管回合已持续 %.0f 秒（可能卡在结束回合）" % (now - started), scene, char)
            continue
        exc = None
        if STATE.stop_requested:
            stop_since = stop_since or now
            if now - stop_since >= STOP_GRACE:
                exc = AIInterrupted
                dump_threads("点了停止 %.0f 秒后 AI 仍未停下，强制中止" % (now - stop_since), scene, char)
        if exc is None:
            sig = progress_signature(char)
            if sig != last:
                last, since = sig, now
                continue
            if now - since < STALL_TIMEOUT:
                continue
            exc = AIStalled
            dump_threads("AI 行动 %.0f 秒没有进展（AP、位置、物品都没变），中止本回合托管" % (now - since), scene, char)
        with STATE.guard_lock:
            injected = STATE.guard_armed and STATE.worker is worker and worker.is_alive()
            if injected:
                STATE.guard_armed = False
                _async_exc(worker, exc)
        if injected:
            worker.join(10.0)
            if worker.is_alive():
                # 线程卡在原生调用里，注入的异常落不下去：至少把操作交还给玩家（托管按钮会一直不可用）
                dump_threads("AI 线程无法中止，强制交还操作", scene, char)
                STATE.continuous = False
                scene.allow_player_input = True
                float_text(char, t("float_stalled"))


def reset_player_sprite(char):
    """卡住后在主线程收拾主角的动画状态（尽力而为）。"""
    spr = getattr(char, "spr", None)
    if not spr or not spr.is_animating:
        return
    log("主角动画仍未结束，强制收尾: " + describe_state(spr.scene, char))
    try:
        if getattr(spr, "is_upgrading", False):
            spr.stop_upgrading()
        # 立即执行尚未执行的动画回调（与游戏「快速移动」相同），再清掉动画状态
        if hasattr(spr, "force_finish_move"):
            spr.force_finish_move()
    except Exception:
        log_exc("收尾主角动画")
    try:
        spr.time_reserved_funcs.clear()
        spr.pos_reserved_funcs.clear()
        spr.set_center()
        get_origin = getattr(sys.modules.get("sprites.charsprite"), "get_origin", None)
        if get_origin is not None:
            spr.origin = get_origin(char)
    except Exception:
        log_exc("复位主角动画")
    spr.is_animating = False


def recover_from_stall(scene, char, interrupted=False):
    """中止托管后（卡住或玩家点了停止）：等动画放完，交还操作，本回合不结束。"""
    from data import dt
    STATE.continuous = False
    STATE.ready_since = None
    deadline = time.time() + 2.0
    while time.time() < deadline and char.spr and char.spr.is_animating:
        time.sleep(0.05)
    if char.spr and char.spr.is_animating:
        queue = getattr(scene, "task_queue", None)
        if queue is not None:
            queue.append(partial(reset_player_sprite, char))
            deadline = time.time() + 2.0
            while time.time() < deadline and char.spr and char.spr.is_animating:
                time.sleep(0.05)
    if dt.scene is scene:
        scene.allow_player_input = True
        float_text(char, t("float_stopped" if interrupted else "float_stalled"))
    log("已%s托管并交还操作（本回合未结束，持续托管已关闭，剩余 AP %.1f）"
        % ("按玩家要求停止" if interrupted else "中止", getattr(char, "ap", 0) or 0))


def end_turn_safely(scene, char):
    """结束回合。原版结束回合出异常后会一直不能操作，这里把回合交还给玩家。"""
    from actions import turn as turn_mod
    from data import dt

    end_turn = getattr(turn_mod, "_end_turn", None)
    handle_error = getattr(turn_mod, "handle_error", None)
    if end_turn is None or handle_error is None:
        turn_mod.try_end_turn_except_handle_error()
        return
    try:
        end_turn()
        return
    except SystemExit:
        # 游戏自己的低帧率看门狗终止了回合线程，它已经把回合交还给玩家
        handle_error("TURNLOOP")
        return
    except Exception:
        log_exc("结束回合（队友 / NPC / 僵尸行动）")
        try:
            handle_error("ENDTURN")
        except Exception:
            pass
    if dt.scene is scene and (scene.turn != "player" or not scene.allow_player_input):
        log("结束回合中途出错，强制交还玩家回合: " + describe_state(scene, char))
        STATE.continuous = False
        scene.turn = "player"
        scene.allow_player_input = True
        try:
            turn_mod.mouse.show()   # actions.turn 里的 system.mouse
        except Exception:
            pass
        float_text(char, t("float_endturn_error"))


def set_switch_interval():
    """托管回合内调小线程切换间隔，返回原值（None = 没改）。"""
    if not AI_SWITCH_INTERVAL:
        return None
    try:
        old = sys.getswitchinterval()
        if AI_SWITCH_INTERVAL < old:
            sys.setswitchinterval(AI_SWITCH_INTERVAL)
            return old
    except Exception:
        log_exc("调整线程切换间隔", once=True)
    return None


def restore_switch_interval(old):
    if old is None:
        return
    try:
        if abs(sys.getswitchinterval() - AI_SWITCH_INTERVAL) < 1e-9:   # 别人改过就不动
            sys.setswitchinterval(old)
    except Exception:
        log_exc("恢复线程切换间隔", once=True)


def ai_turn_worker(scene, char):
    from actions import turn as turn_mod
    from data import dt

    worker = threading.current_thread()
    stalled = interrupted = False
    try:
        try:
            try:
                arm_guard(worker)
                threading.Thread(target=watchdog, args=(scene, char, worker), daemon=True).start()
                try:
                    tasks = run_ai_for(char)
                finally:
                    disarm_guard()
                log("AI 回合完成，执行任务: %s" % (", ".join(tasks) or "无"))
                wait_idle(scene)
                # AI 刚好做完时点了停止：同样不结束回合
                interrupted = STATE.stop_requested
            except AIInterrupted:
                disarm_guard()
                interrupted = True
            except AIStalled:
                disarm_guard()
                stalled = True
            finally:
                STATE.acting = False
        except Exception:
            log_exc("AI 行动")
            STATE.continuous = False
            float_text(char, t("float_error"))
            scene.allow_player_input = True
            return
        if stalled or interrupted:
            recover_from_stall(scene, char, interrupted=interrupted)
            return
        if dt.scene is not scene or not turn_mod.check_player_alive():
            return
        end_turn_safely(scene, char)
    finally:
        STATE.busy = False
        STATE.stop_requested = False
        saved, STATE.saved_interval = STATE.saved_interval, None
        restore_switch_interval(saved)


def blocked_causes(scene):
    """按钮不可用的原因（空列表 = 可用）。与原生动作按钮的判断方式一致。"""
    from data import dt
    causes = []
    player = dt.game.player
    if STATE.acting:
        return causes       # AI 行动中，按钮用来停止
    if scene.turn != "player" or not scene.allow_player_input or STATE.busy:
        causes.append("")
    elif scene.char_animating:
        from system.lang import tr
        causes.append(tr("actionpanel_is_moving"))
    elif player.fainted or player.is_asleep or player.lifting:
        causes.append(t("cause_state"))
    return causes


def can_start(scene):
    from data import dt
    if dt.scene is not scene or STATE.busy or blocked_causes(scene):
        return False
    thread = getattr(scene, "turn_thread", None)
    if thread is not None and thread.is_alive():
        return False
    if scene.playerturn.is_animating:
        return False
    # 有弹窗 / 模态框 / 右键菜单打开时，场景按钮列表里就没有我们的按钮
    btn = getattr(scene.action_panel, "_ai_btn", None)
    return btn is not None and btn in scene.buttons


def start_ai_turn(scene):
    """在主线程调用：开始一个 AI 托管回合。"""
    from data import dt
    from ui.sound import soundplay

    if not can_start(scene):
        return False
    player = dt.game.player
    if scene.selected is not player:
        scene.select(player)
    scene.set_mode(None)
    scene.result.buffer.clear()
    if scene.tutorial.is_open:
        scene.tutorial.close_btn.callback()
    soundplay("endturn")
    STATE.busy = True
    STATE.acting = True
    STATE.stop_requested = False
    STATE.ready_since = None
    scene.allow_player_input = False
    float_text(player, t("float_turn"))
    # 要在 AI 线程启动前调小，线程一开始的那段计算才不会卡住画面；AI 线程结束时恢复
    if STATE.saved_interval is None:
        STATE.saved_interval = set_switch_interval()
    thread = threading.Thread(target=ai_turn_worker, args=(scene, player), daemon=True)
    scene.turn_thread = thread
    try:
        thread.start()
    except Exception:
        log_exc("启动 AI 线程")
        restore_switch_interval(STATE.saved_interval)
        STATE.saved_interval = None
        STATE.busy = STATE.acting = False
        scene.allow_player_input = True
        return False
    return True


def set_continuous(scene, on):
    from data import dt
    STATE.continuous = on
    STATE.ready_since = None
    float_text(dt.game.player, t("float_cont_on" if on else "float_cont_off"))


def tick_continuous(scene):
    """每帧（主线程）检查是否该自动开始下一个托管回合。"""
    from data import dt
    if not STATE.continuous or not can_start(scene):
        STATE.ready_since = None
        return
    now = time.time()
    if STATE.ready_since is None:
        STATE.ready_since = now
        return
    if now - STATE.ready_since < CONTINUOUS_DELAY:
        return
    player = dt.game.player
    max_hp = getattr(player, "max_hp", 0) or 0
    if CONTINUOUS_STOP_HP_RATIO > 0 and max_hp > 0 and player.hp < max_hp * CONTINUOUS_STOP_HP_RATIO:
        STATE.continuous = False
        STATE.ready_since = None
        float_text(player, t("float_hp_stop"))
        return
    start_ai_turn(scene)


def open_menu(scene):
    from data import dt
    menus = [
        {"text": t("menu_once"), "func": partial(start_ai_turn, scene)},
        {
            "text": t("menu_cont_off" if STATE.continuous else "menu_cont_on"),
            "func": partial(set_continuous, scene, not STATE.continuous),
        },
        {"text": t("menu_settings"), "func": partial(scene.comp_auto_modal.set_and_open, dt.game.player)},
    ]
    scene.context_menu.set_and_open(menus)


# ----------------------------------------------------------------------------
# 托管期间的弹窗
# ----------------------------------------------------------------------------

def plain_text(text):
    text = re.sub(r"<br\s*/?>", "\n", str(text or ""), flags=re.I)
    return re.sub(r"<[^>]*>", "", text).strip()


def swallow_popup(popup):
    """托管期间不打开 scene.popup（玩家输入关着，点不掉），改成在主角头顶飘字。"""
    from data import dt
    title = plain_text(getattr(popup, "title", ""))
    content = plain_text(getattr(popup, "content", ""))
    log("托管中的弹窗改为飘字：%s｜%s" % (title, content.replace("\n", " / ")))
    lines = [s for s in content.split("\n") if s.strip()]
    text = lines[0] if lines else title
    if text:
        float_text(dt.game.player, text)
    # 调用方打开前改过按钮（确认框的 instant 等），按关闭时的方式复位；没打开时 close 只做复位
    try:
        popup.close()
    except Exception:
        log_exc("复位弹窗", once=True)


def patch_defaultpopup(module):
    Popup = getattr(module, "DefaultPopup", None)
    if Popup is None or getattr(Popup, "_ai_takeover_patched", False):
        return
    orig_open = Popup.open

    def open(self, *args, **kwargs):
        if STATE.acting:
            try:
                from data import dt
                scene = getattr(dt, "scene", None)
                if self is getattr(scene, "popup", None) and self not in scene.open_modals:
                    swallow_popup(self)
                    return None
            except Exception:
                log_exc("拦截弹窗", once=True)
        return orig_open(self, *args, **kwargs)

    Popup.open = open
    Popup._ai_takeover_patched = True
    log("托管期间的弹窗改为飘字")


def patch_perkpopup(module):
    Popup = getattr(module, "PerkPopup", None)
    if Popup is None or getattr(Popup, "_ai_takeover_patched", False):
        return
    orig = Popup.set_and_open
    STATE.orig_perk_open = orig

    def set_and_open(self, char):
        # 升级检查在单独的线程里，托管回合（含结束回合）期间弹出的话点不掉，等交还后再弹
        if STATE.busy:
            STATE.pending_perk = (self, char)
            log("托管中升级，回合交还后再弹出技能选择")
            return None
        return orig(self, char)

    Popup.set_and_open = set_and_open
    Popup._ai_takeover_patched = True


def flush_pending_perk(scene):
    """主线程每帧调用：托管结束、轮到玩家且没有弹窗时，补弹升级选技能。"""
    pending = STATE.pending_perk
    if pending is None or STATE.busy:
        return
    if scene.turn != "player" or not scene.allow_player_input or scene.open_modals:
        return
    STATE.pending_perk = None
    popup, char = pending
    if getattr(popup, "char", None) or not char.can_level_up():
        return
    STATE.orig_perk_open(popup, char)


def patch_book(module):
    BookType2 = getattr(module, "BookType2", None)
    if BookType2 is None or getattr(BookType2, "_ai_takeover_patched", False):
        return
    orig = BookType2.reread

    def reread(self, char):
        # 原版在玩家回合弹「再读一遍？」确认框且不重置进度，AI 接着会 0 AP 反复「读完」同一本书；
        # 托管时与队友 AI 相同，直接从头重读
        if STATE.acting:
            char.book_pages[self] = 0
            return None
        return orig(self, char)

    BookType2.reread = reread
    BookType2._ai_takeover_patched = True


# ----------------------------------------------------------------------------
# 铁丝网：托管主角近战时当成墙
# ----------------------------------------------------------------------------
# 原版队友寻路（actions.path.get_pathdic_comp / actions.utils.get_closest_target）只把铁丝网当成
# 不能斜穿，路径的最后一步会隔着网。角色走路时又会停在僵尸前一格，于是站在网边打不到，
# 搜刮循环里每一步 handle_enemy_encounter 又把人拉回网边。近战攻击的寻路和挑目标时把完好的
# 铁丝网整段封死：打不到的僵尸不选，绕得过去的照常绕。远程武器隔网能打，不受影响。

def takeover_melee(comp):
    """托管主角正在用近战武器走原版攻击流程（handle_attack → 选目标 / move_and_attack）。"""
    if not (STATE.acting and on_ai_thread()):
        return False
    from data import dt
    if comp is not dt.game.player or getattr(comp, "intention", None) != "attack":
        return False
    ca = STATE.ca
    weapon = ca.get_weapon(comp) if ca is not None else None
    return getattr(weapon, "atktype", None) == "melee"


def with_fences_blocked(func, *args, **kwargs):
    local = STATE.local
    prev = getattr(local, "block_fences", False)
    local.block_fences = True
    try:
        return func(*args, **kwargs)
    finally:
        local.block_fences = prev


def block_fences(loc, blockcellsets):
    from core.creature import utils as cu
    block = getattr(cu, "_block_cells_completely", None)
    for fence in getattr(loc, "fences", None) or ():
        if (getattr(fence, "hp", 0) or 0) <= 0:
            continue
        cell1, cell2 = fence.cells
        if block is not None:
            block(cell1, cell2, blockcellsets)
        else:
            blockcellsets.add(frozenset((cell1, cell2)))


def make_blockcellsets(orig):
    def get_blockcellsets(loc, entity_type, *args, **kwargs):
        sets = orig(loc, entity_type, *args, **kwargs)
        if entity_type == "comp" and getattr(STATE.local, "block_fences", False):
            try:
                block_fences(loc, sets)
            except Exception:
                log_exc("近战寻路封铁丝网", once=True)
        return sets
    get_blockcellsets._ai_takeover = True
    return get_blockcellsets


def make_fence_aware(orig, label):
    def wrapper(comp, *args, **kwargs):
        try:
            melee = takeover_melee(comp)
        except Exception:
            log_exc("判断近战（%s）" % label, once=True)
            melee = False
        if melee:
            return with_fences_blocked(orig, comp, *args, **kwargs)
        return orig(comp, *args, **kwargs)
    wrapper.__name__ = getattr(orig, "__name__", label)
    wrapper._ai_takeover = True
    return wrapper


def crosses_fence(path):
    for a, b in zip(path, path[1:]):
        fences = getattr(a, "fences", None)
        if fences:
            other = getattr(b, "fences", ())
            if any(f in other and (getattr(f, "hp", 0) or 0) > 0 for f in fences):
                return True
    return False


def make_pathdic(orig):
    """compaction.get_pathdic_comp：原版按步数找最短路，翻一次铁丝网（10 AP，跑酷 5）也只算一步，
    搜刮时宁可翻网也不绕几步。托管主角的路要翻网时，再找一条不翻网的，实际 AP 更少就走那条。"""
    def get_pathdic_comp(comp, *args, **kwargs):
        pathdic = orig(comp, *args, **kwargs)
        try:
            from data import dt
            if not pathdic or not (STATE.acting and on_ai_thread() and comp is dt.game.player):
                return pathdic
            if getattr(STATE.local, "block_fences", False) or not crosses_fence(pathdic["path"]):
                return pathdic
            other = with_fences_blocked(orig, comp, *args, **kwargs)
            if other and comp.calculate_path_ap_cost(other["path"]) < comp.calculate_path_ap_cost(pathdic["path"]):
                return other
        except Exception:
            log_exc("绕开铁丝网的路线", once=True)
        return pathdic
    get_pathdic_comp._ai_takeover = True
    return get_pathdic_comp


def make_ai_sleep(orig):
    """原版 AI 等动画、寻路时调用的 sleep：在 AI 线程里顺便检查玩家是否点了停止。"""
    def sleep(secs):
        if not on_ai_thread():
            return orig(secs)
        check_stop()
        orig(secs)
        check_stop()
    sleep._ai_takeover = True
    return sleep


def patch_compaction(module):
    if getattr(module, "_ai_takeover_patched", False):
        return
    STATE.ca = module
    for name in ("get_closest_target", "move_and_attack"):
        orig = getattr(module, name, None)
        if orig is None:
            log("找不到 compaction.%s，近战不会绕开铁丝网" % name)
            continue
        setattr(module, name, make_fence_aware(orig, name))
    if callable(getattr(module, "get_pathdic_comp", None)):
        module.get_pathdic_comp = make_pathdic(module.get_pathdic_comp)
    if callable(getattr(module, "sleep", None)):
        module.sleep = make_ai_sleep(module.sleep)
    module._ai_takeover_patched = True
    log("托管主角近战时绕开铁丝网；托管中可随时停止")


def patch_pathing(module, label):
    """actions.path（get_pathdic_comp）和 actions.utils（get_closest_target）各自导入了 get_blockcellsets。"""
    if getattr(module, "_ai_takeover_patched", False):
        return
    orig = getattr(module, "get_blockcellsets", None)
    if orig is None:
        log("找不到 %s.get_blockcellsets，近战不会绕开铁丝网" % label)
    else:
        module.get_blockcellsets = make_blockcellsets(orig)
    if callable(getattr(module, "sleep", None)):
        module.sleep = make_ai_sleep(module.sleep)
    module._ai_takeover_patched = True


# ----------------------------------------------------------------------------
# 卡顿诊断
# ----------------------------------------------------------------------------

def note_frame(scene):
    """主线程每帧调用：托管回合里一帧特别长，就把 AI 线程当时在做的事写进日志（每次启动最多 10 条）。"""
    now = time.perf_counter()
    last, STATE.last_frame = STATE.last_frame, now
    if not HITCH_LOG or last is None or not STATE.busy or STATE.hitches >= 10:
        return
    gap = now - last
    if gap < HITCH_LOG:
        return
    STATE.hitches += 1
    worker = STATE.worker
    where = ""
    try:
        frame = sys._current_frames().get(getattr(worker, "ident", None))
        if frame is not None:
            where = "".join(traceback.format_stack(frame, limit=6)).rstrip()
    except Exception:
        pass
    log("托管中画面卡了 %.2f 秒（%s，任务: %s）\nAI 线程此刻位置:\n%s" % (
        gap, "AI 行动" if STATE.acting else "结束回合", STATE.task or "-", where or "（无）"))


# ----------------------------------------------------------------------------
# 补丁
# ----------------------------------------------------------------------------

def patch_actionpanel(module):
    from paint import static
    from ui.tooltip import Tooltip
    from core.utils import ct
    from paint.color import RED

    ActionPanel = module.ActionPanel
    if getattr(ActionPanel, "_ai_takeover_patched", False):
        return
    BaseBtn = ActionPanel.ActionBtn

    class AITakeoverBtn(BaseBtn):
        def __init__(self, panel):
            ensure_icon()
            super().__init__(panel, ACTION)

        def cancheck(self, char, causes):
            causes.extend(blocked_causes(self.panel.scene))

        @property
        def pushed(self):
            base = BaseBtn.pushed
            pushed = base.fget(self) if isinstance(base, property) else base(self)
            return pushed or STATE.continuous or STATE.busy

        def draw(self):
            # AI 行动中显示停止图标
            self.action = ACTION_STOP if STATE.acting else ACTION
            BaseBtn.draw(self)

        def set_tooltip(self):
            if STATE.acting:
                Tooltip.title(t("name_stop") + " (%s / Esc)" % HOTKEY)
                Tooltip.add(t("desc_stop"))
            else:
                Tooltip.title(t("name") + " (%s)" % HOTKEY)
                Tooltip.add(t("desc"))
                Tooltip.add(t("hint_keys"))
                if STATE.continuous:
                    Tooltip.add(t("continuous_on"))
                for cause in self.causes:
                    if cause:
                        Tooltip.add(ct(cause, RED))
            Tooltip.fixed = (self.panel.rect.x + 10, self.rect.y - 2)
            Tooltip.align = "topright"

        def callback(self):
            if STATE.acting:
                request_stop(self.panel.scene)
            elif STATE.continuous:
                set_continuous(self.panel.scene, False)
            else:
                start_ai_turn(self.panel.scene)

        def callback_right(self):
            if STATE.acting:
                request_stop(self.panel.scene)
            else:
                open_menu(self.panel.scene)

    def relayout(panel):
        # 与 ActionPanel.update 末尾的布局代码相同
        n = len(panel.action_btns)
        panel.image = static("ui/action_panel_bg" if n <= 15 else "ui/action_panel_bg_long")
        panel.rect = panel.image.get_rect(bottomright=(639, 358))
        for i, btn in enumerate(panel.action_btns):
            btn.rect.topleft = panel.rect.move(9 + i % 5 * 25, 17 + int(i / 5) * 25).topleft

    orig_init = ActionPanel.__init__
    orig_update = ActionPanel.update

    def __init__(self, scene):
        STATE.busy = False
        STATE.acting = False
        STATE.continuous = False
        STATE.ready_since = None
        STATE.pending_perk = None
        STATE.stop_requested = False
        orig_init(self, scene)

    def update(self):
        orig_update(self)
        try:
            btn = getattr(self, "_ai_btn", None)
            if btn is None:
                btn = self._ai_btn = AITakeoverBtn(self)
            char = self.scene.selected
            if char is None or not char.sleeping or STATE.acting:
                self.action_btns.append(btn)
                relayout(self)
        except Exception:
            log_exc("ActionPanel.update", once=True)
            return
        if getattr(self.scene, "action_panel", None) is self:
            try:
                note_frame(self.scene)
            except Exception:
                log_exc("卡顿诊断", once=True)
            try:
                flush_pending_perk(self.scene)
            except Exception:
                log_exc("flush_pending_perk", once=True)
            try:
                tick_continuous(self.scene)
            except Exception:
                log_exc("tick_continuous", once=True)

    ActionPanel.__init__ = __init__
    ActionPanel.update = update
    ActionPanel.AITakeoverBtn = AITakeoverBtn
    ActionPanel._ai_takeover_patched = True
    log("已为动作面板添加 AI 托管按钮")


def patch_mainscene(module):
    MainScene = module.MainScene
    if getattr(MainScene, "_ai_takeover_patched", False):
        return
    orig_keydown = MainScene.keydown

    def console_active(scene):
        console = getattr(scene, "debug_console", None)
        return console is not None and getattr(console, "active", False)

    def on_hotkey(scene):
        import pygame
        if console_active(scene):
            return False
        if STATE.acting:
            request_stop(scene)
            return True
        if STATE.continuous:
            set_continuous(scene, False)
            return True
        if pygame.key.get_mods() & pygame.KMOD_SHIFT:
            set_continuous(scene, True)
            return True
        return start_ai_turn(scene)

    def keydown(self, key):
        try:
            if key == hotkey_code() and on_hotkey(self):
                return None
            if STATE.acting and not console_active(self):
                import pygame
                if key == pygame.K_ESCAPE:
                    request_stop(self)
                    return None
        except Exception:
            log_exc("热键处理")
        return orig_keydown(self, key)

    MainScene.keydown = keydown

    # 托管时玩家输入是关着的，场景按钮列表为空；AI 行动中只放出托管按钮，用来点「停止」
    orig_buttons = MainScene.__dict__.get("buttons")
    if isinstance(orig_buttons, property) and orig_buttons.fget is not None:
        def buttons(self):
            btns = orig_buttons.fget(self)
            if not STATE.acting or btns or self.allow_player_input:
                return btns
            try:
                if getattr(self, "fadeout", False) or console_active(self):
                    return btns
                panel = getattr(self, "action_panel", None)
                btn = getattr(panel, "_ai_btn", None)
                if btn is not None and btn in panel.action_btns:
                    return [btn]
            except Exception:
                log_exc("托管中的停止按钮", once=True)
            return btns
        MainScene.buttons = property(buttons)
    else:
        log("MainScene.buttons 不是属性，托管中只能用 %s / Esc 停止" % HOTKEY)

    MainScene._ai_takeover_patched = True
    log("已注册快捷键 %s（托管中 %s / Esc 立即停止）" % (HOTKEY, HOTKEY))


def on_module_loaded(name, module):
    """由 base_library.zip 里的引导代码在游戏模块加载完成后调用。"""
    if name == "ui.panels.actionpanel":
        patch_actionpanel(module)
    elif name == "scenes.main":
        patch_mainscene(module)
    elif name == "ui.popups.defaultpopup":
        patch_defaultpopup(module)
    elif name == "ui.popups.perkpopup":
        patch_perkpopup(module)
    elif name == "core.item.book":
        patch_book(module)
    elif name == "actions.compaction":
        patch_compaction(module)
    elif name == "actions.path":
        patch_pathing(module, "actions.path")
    elif name == "actions.utils":
        patch_pathing(module, "actions.utils")


try:
    with open(LOG_PATH, "w", encoding="utf-8") as _f:
        _f.write(time.strftime("[%Y-%m-%d %H:%M:%S] ") + "AI 托管 MOD 已加载\n")
except OSError:
    pass
