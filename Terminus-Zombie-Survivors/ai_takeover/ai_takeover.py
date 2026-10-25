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

本文件由 base_library.zip 中的引导代码在游戏启动时加载，出错会写入同目录的 ai_takeover.log，
不会让游戏崩溃。
"""

import os
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

# ----------------------------------------------------------------------------

ACTION = "ai_takeover"
MOD_DIR = os.path.dirname(os.path.abspath(__file__))

# MOD 加载器据此决定把哪些游戏模块的加载事件通知给本 MOD
PATCH_TARGETS = ("ui.panels.actionpanel", "scenes.main")
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
    "cause_state": ("主角当前无法行动", "主角目前無法行動", "Your character can't act right now"),
}


class _State:
    busy = False            # AI 正在行动 / 正在结束回合
    continuous = False      # 持续托管开关
    ready_since = None      # 持续托管：本回合可接管的起始时间
    logged = set()          # 每帧调用处的错误只记录一次


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
        ca.handle_medication(char)
        if not char.sleeping:
            for name, check, _ in TASKS:
                if getattr(ca, check)(char):
                    tasks.append(name)
        handlers = {name: handle for name, _, handle in TASKS}
        for name in tasks:
            getattr(ca, handlers[name])(char)
            if name == "sleep" and char.sleeping:
                break
        if not char.sleeping:
            ca.handle_food_consumption(char)
            ca.handle_hydration(char)
            ca.handle_alcohol_consumption(char)
    finally:
        scene.active_companion = False
    return tasks


def wait_idle(scene, timeout=15.0):
    deadline = time.time() + timeout
    while time.time() < deadline and scene.char_animating:
        time.sleep(0.05)


def ai_turn_worker(scene, char):
    from actions import turn as turn_mod
    from data import dt

    try:
        try:
            tasks = run_ai_for(char)
            log("AI 回合完成，执行任务: %s" % (", ".join(tasks) or "无"))
            wait_idle(scene)
        except Exception:
            log_exc("AI 行动")
            STATE.continuous = False
            float_text(char, t("float_error"))
            scene.allow_player_input = True
            return
        if dt.scene is not scene or not turn_mod.check_player_alive():
            return
        turn_mod.try_end_turn_except_handle_error()
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
        STATE.continuous = False
        STATE.ready_since = None
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


try:
    with open(LOG_PATH, "w", encoding="utf-8") as _f:
        _f.write(time.strftime("[%Y-%m-%d %H:%M:%S] ") + "AI 托管 MOD 已加载\n")
except OSError:
    pass
