# -*- coding: utf-8 -*-
"""
Terminus: Zombie Survivors - 主角 AI 托管 MOD

在动作面板上加一个「AI 托管」按钮（机器人图标），让游戏自带的队友 AI 接管主角：

  左键按钮 / F6        AI 接管主角行动一回合，行动完自动结束回合
  Shift+F6             开启持续托管（每回合自动托管）；持续托管中再按 F6 或点按钮即停止
  右键按钮             菜单：托管本回合 / 开关持续托管 / AI 行为设置

「AI 行为设置」打开的是游戏原生的队友自动化设置面板，只对主角生效：
可以单独设置主角要不要攻击、搜刮价值阈值、吃喝/睡觉/读书/种田等。
（面板顶部的总开关对主角无意义，忽略即可。）

托管回合跑在玩家回合里，而原版有一批只在玩家回合才弹的窗口（工具碎裂、读完书获得技能/属性、
学会配方、重读确认、升级选技能）。托管期间玩家输入是关着的，这些弹窗点不掉，重读确认框还会让
AI 0 AP 反复「读完」同一本书。所以托管期间：普通弹窗改成头顶飘字，重读和队友一样直接重读，
升级选技能等回合交还后再弹。另有卡住保护：AI 行动时 AP 和位置长时间不变就中止托管、交还操作，
并把所有线程的调用栈写进日志。

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

# ----------------------------------------------------------------------------

ACTION = "ai_takeover"
MOD_DIR = os.path.dirname(os.path.abspath(__file__))

# MOD 加载器据此决定把哪些游戏模块的加载事件通知给本 MOD
PATCH_TARGETS = (
    "ui.panels.actionpanel",
    "scenes.main",
    "ui.popups.defaultpopup",
    "ui.popups.perkpopup",
    "core.item.book",
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
}


class AIStalled(BaseException):
    """看门狗注入 AI 线程的异常。继承 BaseException，免得被游戏或其他 MOD 的 except Exception 吞掉。"""


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
    key = "assets/image/action/%s.png" % ACTION
    if key in Images.cache:
        return
    import pygame
    surf = pygame.Surface((13, 13), pygame.SRCALPHA)
    for y, row in enumerate(ICON_ROWS):
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
            STATE.task = name
            getattr(ca, handlers[name])(char)
            if name == "sleep" and char.sleeping:
                break
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
    """AI 行动期间 AP / 位置长时间不变 → 中止；结束回合太久 → 只记录调用栈。"""
    last = progress_signature(char)
    since = time.time()
    started = since
    slow_dumped = False
    while worker.is_alive():
        time.sleep(0.5)
        now = time.time()
        with STATE.guard_lock:
            armed = STATE.guard_armed and STATE.worker is worker
        if not armed:
            if not slow_dumped and now - started > SLOW_TURN_DUMP:
                slow_dumped = True
                dump_threads("托管回合已持续 %.0f 秒（可能卡在结束回合）" % (now - started), scene, char)
            continue
        sig = progress_signature(char)
        if sig != last:
            last, since = sig, now
            continue
        if now - since < STALL_TIMEOUT:
            continue
        dump_threads("AI 行动 %.0f 秒没有进展（AP、位置、物品都没变），中止本回合托管" % (now - since), scene, char)
        with STATE.guard_lock:
            injected = STATE.guard_armed and STATE.worker is worker and worker.is_alive()
            if injected:
                STATE.guard_armed = False
                _async_exc(worker, AIStalled)
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


def recover_from_stall(scene, char):
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
        float_text(char, t("float_stalled"))
    log("已中止托管并交还操作（本回合未结束，持续托管已关闭）")


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


def ai_turn_worker(scene, char):
    from actions import turn as turn_mod
    from data import dt

    worker = threading.current_thread()
    stalled = False
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
        if stalled:
            recover_from_stall(scene, char)
            return
        if dt.scene is not scene or not turn_mod.check_player_alive():
            return
        end_turn_safely(scene, char)
    finally:
        STATE.busy = False


def blocked_causes(scene):
    """按钮不可用的原因（空列表 = 可用）。与原生动作按钮的判断方式一致。"""
    from data import dt
    causes = []
    player = dt.game.player
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
    STATE.ready_since = None
    scene.allow_player_input = False
    float_text(player, t("float_turn"))
    thread = threading.Thread(target=ai_turn_worker, args=(scene, player), daemon=True)
    scene.turn_thread = thread
    thread.start()
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

        def set_tooltip(self):
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
            if STATE.continuous:
                set_continuous(self.panel.scene, False)
            else:
                start_ai_turn(self.panel.scene)

        def callback_right(self):
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
        orig_init(self, scene)

    def update(self):
        orig_update(self)
        try:
            btn = getattr(self, "_ai_btn", None)
            if btn is None:
                btn = self._ai_btn = AITakeoverBtn(self)
            char = self.scene.selected
            if char is None or not char.sleeping:
                self.action_btns.append(btn)
                relayout(self)
        except Exception:
            log_exc("ActionPanel.update", once=True)
            return
        if getattr(self.scene, "action_panel", None) is self:
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

    def on_hotkey(scene):
        import pygame
        console = getattr(scene, "debug_console", None)
        if console is not None and getattr(console, "active", False):
            return False
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
        except Exception:
            log_exc("热键处理")
        return orig_keydown(self, key)

    MainScene.keydown = keydown
    MainScene._ai_takeover_patched = True
    log("已注册快捷键 %s" % HOTKEY)


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


try:
    with open(LOG_PATH, "w", encoding="utf-8") as _f:
        _f.write(time.strftime("[%Y-%m-%d %H:%M:%S] ") + "AI 托管 MOD 已加载\n")
except OSError:
    pass
