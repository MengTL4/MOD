# -*- coding: utf-8 -*-
"""
Terminus: Zombie Survivors - 队伍背包共享 MOD

主角和所有队友的背包合并成一个大背包：
  - 打开任何人的背包都能看到、使用、丢弃全队的物品（队友身上穿戴的衣物/背包、手持武器、铺盖除外）
  - 制作、烹饪、修理、用药、生火、加固等所有「检索身上物品」的地方都会用到全队物品
  - 队友 AI（以及 AI 托管 MOD 控制的主角）吃喝、用药、制作、读书、修理时同样从全队物品里找
  - 物品被消耗/丢弃/交出时，从真正携带它的人身上扣除
  - 负重仍然各算各的：捡到的东西进捡的人背包
  - 队友携带的物品格子左上角显示持有者名字的首字；鼠标停在上面，提示里写「在 XX 身上」
    （一叠物品分属几个人时写「携带者：你 ×2 · XX ×1」）
  - 背包面板下方有「全队物品 / 只看自己」切换（快捷键 V），只看自己时列表和负重一致
  - 和队友交换物品的界面（库存界面右侧是队友）里，双方显示各自的真实背包，方便调配负重
  - 结束回合时如果出错，记录错误后把控制权交还玩家，不会卡在「其他幸存者的回合」

实现：替换 Character.items 属性。对主角/队友返回一个「合并视图」列表，增删操作会转发到
物品真正所在的那个人的原始列表；存档里保存的仍然是每个人自己的原始列表，卸载 MOD 不影响存档。
少数必须只看自己身上物品的游戏函数（负重/背包整理、死亡掉落、每回合物品结算、穿戴、照明、
防水、计分、NPC 扒窃等）通过调用方判断，仍然拿到原始列表。

本文件由 base_library.zip 中的 MOD 加载器在游戏启动时加载，出错会写入同目录的
shared_inventory.log，不会让游戏崩溃。
"""

import os
import sys
import threading
import time
import traceback
import types
from contextlib import contextmanager

# ----------------------------------------------------------------------------
# 可调整的配置
# ----------------------------------------------------------------------------

# True = 只和同一地图上的队友共享；False = 全队共享
SAME_LOCATION_ONLY = False

# 在背包物品提示里显示「在 XX 身上」
OWNER_TOOLTIP = True

# 在队友携带的物品格子左上角显示持有者名字的首字
OWNER_BADGE = True

# 背包界面里切换「全队物品 / 只看自己」的按键（pygame 按键名）
VIEW_HOTKEY = "v"

# ----------------------------------------------------------------------------

MOD_DIR = os.path.dirname(os.path.abspath(__file__))
LOG_PATH = os.path.join(MOD_DIR, "shared_inventory.log")
PREFS_PATH = os.path.join(MOD_DIR, "prefs.json")   # 记住「只看自己」的选择

# 持有者角标颜色，按 主角、队友1、队友2…… 的顺序循环使用
BADGE_COLORS = ((255, 214, 64), (120, 200, 255), (255, 150, 120), (170, 230, 130), (210, 160, 255))

# 加载器据此决定把哪些游戏模块的加载事件通知给本 MOD
PATCH_TARGETS = (
    "core.creature.character",
    "core.item.item",
    "core.item.clothing",
    "core.item.tool",
    "core.item.electronics",
    "core.actions",
    "actions.turn",
    "actions.npcaction",
    "data.game",
    "ui.panels.actionpanel",
    "ui.modals.inventorymodal",
    "ui.modals.common.inventory",
)

# 这些函数里的 xxx.items 返回原始列表（只看自己身上）。键为模块名，值为 (类名或 None, 属性名)。
RAW_FUNCS = {
    "core.creature.character": (
        ("Character", "organize_bags"),          # 背包整理 / 负重
        ("Character", "die_human"),              # 死亡掉落
        ("Character", "armed_lv"),               # 持械等级
        ("Character", "get_light_cells"),        # 手电等照明
        ("Character", "get_water_resistance_items"),  # 雨具防水
    ),
    "core.item.clothing": (
        ("BaseClothing", "put_on"),              # 穿戴前先把物品转移到自己身上
        ("BaseBag", "_put_on"),
        ("Bedding", "use"),
    ),
    "core.item.tool": (
        ("Umbrella", "in_use"),
    ),
    "core.actions": (
        ("CanCheck", "turnon"),                  # 「开灯」按钮只看自己身上的灯
    ),
    "actions.turn": (
        (None, "_prepare_next_turn"),            # 每回合物品结算（腐败、刷新等），避免重复处理
    ),
    "actions.npcaction": (
        (None, "get_unworn_items"),              # NPC 扒窃只偷目标本人
        (None, "act_pickpocket"),
    ),
    "data.game": (
        ("GameData", "get_score_factors"),       # 计分，避免重复计算
        ("GameData", "_prune_missing_mod_content"),
        ("GameData", "validate_data"),
    ),
}

# 只看自己身上的物品名：has_item 查询这些名字时不看队友（雨伞要自己带着才能挡雨，
# 游戏里「在用伞」「防水」「伞是否在用」三处必须一致，否则会在 max() 上报错卡住回合）
PERSONAL_ITEMS = frozenset(("umbrella",))

# 依次为：简体中文、繁体中文、其他语言
TEXT = {
    "carried_by": ("在 {name} 身上", "在 {name} 身上", "Carried by {name}"),
    "carried_split": ("携带者：{parts}", "攜帶者：{parts}", "Carried by: {parts}"),
    "you": ("你", "你", "you"),
    "view_party": ("显示：全队物品 [{hotkey}]", "顯示：全隊物品 [{hotkey}]", "Showing: party [{hotkey}]"),
    "view_own": ("显示：只看自己 [{hotkey}]", "顯示：只看自己 [{hotkey}]", "Showing: mine only [{hotkey}]"),
    "view_title": ("背包显示（{hotkey}）", "背包顯示（{hotkey}）", "Inventory view ({hotkey})"),
    "view_desc_party": (
        "全队物品：显示自己和所有队友可共享的物品。",
        "全隊物品：顯示自己和所有隊友可共享的物品。",
        "Party: shows your items and every companion's shareable items.",
    ),
    "view_desc_own": (
        "只看自己：只显示自己身上的物品，和负重一致。",
        "只看自己：只顯示自己身上的物品，和負重一致。",
        "Mine only: shows only what you carry, matching your weight.",
    ),
    "view_switch": ("切换", "切換", "Switch"),
    "turn_recovered": (
        "回合结算出错，已恢复（见 game_log.log）",
        "回合結算出錯，已恢復（見 game_log.log）",
        "Turn error, recovered (see game_log.log)",
    ),
}


class _State:
    raw_codes = set()        # 需要原始列表的调用方代码对象
    local = threading.local()  # local.depth > 0：本线程处于 raw_scope() 中
    exchange_modal = None    # 正在和队友交换物品的库存界面
    exchange_opening = False
    turn_failed = False      # 本次结束回合中 _end_turn 抛出了异常
    own_view = False         # 背包界面「只看自己」
    inventory_opening = None  # 正在打开的库存界面（打开过程中界面还不在 open_modals 里）
    owner_cache = (None, {})  # (帧号, {id(物品): 持有者})
    logged = set()


STATE = _State()
_dt = None


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


def t(text_id, /, **kwargs):
    lang = getattr(dt().settings, "lang", "English")
    idx = {"Simplified Chinese": 0, "Traditional Chinese": 1}.get(lang, 2)
    return TEXT[text_id][idx].format(**kwargs)


def dt():
    global _dt
    if _dt is None:
        from data import dt as module
        _dt = module
    return _dt


def iter_codes(obj):
    """函数 / property / staticmethod 里的所有代码对象（含嵌套的推导式、lambda）。"""
    if isinstance(obj, property):
        for f in (obj.fget, obj.fset, obj.fdel):
            if f is not None:
                yield from iter_codes(f)
        return
    obj = getattr(obj, "__func__", obj)
    code = getattr(obj, "__code__", None)
    if code is None:
        return
    stack = [code]
    while stack:
        c = stack.pop()
        yield c
        stack.extend(k for k in c.co_consts if isinstance(k, types.CodeType))


@contextmanager
def raw_scope():
    """在此范围内（本线程）所有 xxx.items 都只看自己身上。"""
    local = STATE.local
    local.depth = getattr(local, "depth", 0) + 1
    try:
        yield
    finally:
        local.depth -= 1


def in_raw_scope():
    return getattr(STATE.local, "depth", 0) > 0


def take_item(char, item):
    """item 在其他队员身上时，把它转移到 char 身上（用于「在谁身上才对谁生效」的物品）。"""
    members = party_of(char)
    if not members:
        return
    owner = owner_of(item, members[1:])
    if owner is None:
        return
    owner._items.remove(item)
    list.append(char._items, item)   # 与 `+=` 一致：不弹「获得物品」提示
    char._items.check()
    log("%s 从 %s 身上拿来了 %s" % (char.name, owner.name, item))


def register_raw(name, module):
    for cls_name, attr in RAW_FUNCS.get(name, ()):
        owner = module if cls_name is None else getattr(module, cls_name, None)
        target = None if owner is None else vars(owner).get(attr) if isinstance(owner, type) else getattr(owner, attr, None)
        if target is None:
            log("找不到 %s.%s%s，跳过" % (name, cls_name + "." if cls_name else "", attr))
            continue
        STATE.raw_codes.update(iter_codes(target))


# ----------------------------------------------------------------------------
# 队伍
# ----------------------------------------------------------------------------

def party_of(char):
    """char 是主角或队友时返回参与共享的队伍成员列表（char 在第一位），否则返回 None。"""
    game = dt().game
    if game is None:
        return None
    player = getattr(game, "player", None)
    comps = getattr(game, "companions", None) or ()
    if player is None or (char is not player and char not in comps):
        return None
    members = [char]
    for m in [player, *comps]:
        if m is char or not getattr(m, "is_alive", True):
            continue
        if SAME_LOCATION_ONLY and getattr(m, "loc", None) is not getattr(char, "loc", None):
            continue
        members.append(m)
    return members


def owner_of(item, members):
    for m in members:
        if item in m._items:
            return m
    return None


def exchange_active():
    modal = STATE.exchange_modal
    if modal is None:
        return False
    if STATE.exchange_opening:
        return True
    scene = getattr(modal, "scene", None)
    if scene is not None and scene is dt().scene and modal in scene.open_modals:
        return True
    STATE.exchange_modal = None
    return False


def own_view_active():
    """背包界面开着、且是最上层窗口、且选了「只看自己」。"""
    if not STATE.own_view:
        return False
    scene = dt().scene
    modal = getattr(scene, "inventory_modal", None)
    if modal is None:
        return False
    if STATE.inventory_opening is modal:
        return True
    open_modals = getattr(scene, "open_modals", None)
    return bool(open_modals) and open_modals[-1] is modal


def ui_raw_active():
    """界面要求显示各自真实背包：和队友交换物品，或背包选了「只看自己」。只作用于画面线程。"""
    if threading.current_thread() is not threading.main_thread():
        return False
    return exchange_active() or own_view_active()


def owner_map():
    """{id(物品): 持有者}，覆盖主角和所有队友的原始背包。每帧只建一次。"""
    d = dt()
    frame = getattr(d, "f_count", None)
    cached_frame, cached = STATE.owner_cache
    if frame is not None and frame == cached_frame:
        return cached
    result = {}
    game = d.game
    if game is not None and getattr(game, "player", None) is not None:
        for m in (game.player, *game.companions):
            for item in m._items:
                result[id(item)] = m
    STATE.owner_cache = (frame, result)
    return result


def display_name(char):
    name = getattr(char, "localized_name", None)
    if callable(name):
        name = name()
    return name or char.name


def member_color(char):
    game = dt().game
    members = [game.player, *game.companions]
    index = next((i for i, m in enumerate(members) if m is char), 0)
    return BADGE_COLORS[index % len(BADGE_COLORS)]


def load_prefs():
    try:
        import json
        with open(PREFS_PATH, encoding="utf-8") as f:
            STATE.own_view = bool(json.load(f).get("own_view", False))
    except (OSError, ValueError):
        pass


def save_prefs():
    try:
        import json
        with open(PREFS_PATH, "w", encoding="utf-8") as f:
            json.dump({"own_view": STATE.own_view}, f)
    except OSError:
        log_exc("保存设置", once=True)


def private_items(member):
    """队友身上不参与共享的物品：穿戴的衣物和背包、手持武器、正在用的铺盖。"""
    ids = {id(i) for i in member.worn_items}
    ids.update(id(i) for i in getattr(member, "used_bedding", ()) or ())
    weapon = getattr(member, "holding_weapon", None)
    if weapon is not None:
        ids.add(id(weapon))
    return ids


# ----------------------------------------------------------------------------
# 合并视图
# ----------------------------------------------------------------------------

class SharedItems(list):
    """主角 / 队友的合并背包视图。

    内容 = 自己的全部物品 + 其他队员可共享的物品。读操作看到全队物品；
    remove / batch_remove / pop 转发到物品真正所在的人；append / extend 加到自己身上。
    """

    def __init__(self, owner, members):
        raw = owner._items
        data = list(raw)
        seen = {id(i) for i in data}
        for m in members:
            if m is owner:
                continue
            hidden = private_items(m)
            for item in m._items:
                key = id(item)
                if key not in hidden and key not in seen:
                    seen.add(key)
                    data.append(item)
        super().__init__(data)
        self._owner = owner
        self._raw = raw
        self._members = members

    # 存档 / 复制时当作普通列表，避免把本类写进存档
    def __reduce_ex__(self, protocol):
        return (list, (list(self),))

    def __getattr__(self, name):
        # item_list 的其他属性（check、show_acquired_items、_suppress_check ……）转给自己的原始列表
        if name.startswith("__") or name in ("_owner", "_raw", "_members"):
            raise AttributeError(name)
        return getattr(self._raw, name)

    @property
    def obj(self):
        return self._owner

    def _route_remove(self, items):
        groups = {}
        for item in items:
            owner = owner_of(item, self._members) or self._owner
            groups.setdefault(id(owner), (owner, []))[1].append(item)
        for owner, group in groups.values():
            if len(group) == 1:
                owner._items.remove(group[0])
            else:
                owner._items.batch_remove(group)

    def append(self, item):
        self._raw.append(item)
        super().append(item)

    def extend(self, items):
        items = list(items)
        self._raw.extend(items)
        super().extend(items)

    def insert(self, index, item):
        # 与原版 item_list.insert（即 list.insert）一致：不触发「获得物品」提示；
        # 位置落在自己的物品范围内时保持原位置，否则放到自己物品的末尾
        list.insert(self._raw, min(index, len(self._raw)), item)
        super().insert(index, item)

    def __iadd__(self, items):
        # 与原版 `char.items += x` 一致：不触发「获得物品」提示，最后由 setter 整理背包
        items = list(items)
        list.extend(self._raw, items)
        super().extend(items)
        self._raw.check()
        return self

    def remove(self, item):
        self._route_remove([item])
        try:
            super().remove(item)
        except ValueError:
            pass

    def batch_remove(self, items):
        items = list(items)
        self._route_remove(items)
        for item in items:
            try:
                super().remove(item)
            except ValueError:
                pass

    def pop(self, index=-1):
        item = super().pop(index)
        self._route_remove([item])
        return item

    def clear(self):
        # 只清空自己身上的（视图开头的 len(raw) 个就是自己的物品）
        n = len(self._raw)
        self._raw.clear()
        super().__delitem__(slice(0, n))

    def __delitem__(self, index):
        items = self[index] if isinstance(index, slice) else [self[index]]
        super().__delitem__(index)
        self._route_remove(items)

    def __setitem__(self, index, value):
        old = self[index] if isinstance(index, slice) else [self[index]]
        new = list(value) if isinstance(index, slice) else [value]
        super().__setitem__(index, value)
        self._route_remove(old)
        self._raw.extend(new)


def make_items_property(orig):
    orig_get, orig_set = orig.fget, orig.fset
    getframe = sys._getframe

    def items_get(self):
        try:
            if getframe(1).f_code in STATE.raw_codes or in_raw_scope():
                return self._items
            members = party_of(self)
            if members is None or len(members) < 2 or ui_raw_active():
                return self._items
            return SharedItems(self, members)
        except Exception:
            log_exc("读取合并背包", once=True)
            return orig_get(self)

    def items_set(self, value):
        if isinstance(value, SharedItems) and value._owner is self:
            return  # `+=` 等原地操作已经作用到各自的原始列表
        try:
            members = None
            if getframe(1).f_code not in STATE.raw_codes and not in_raw_scope() and not ui_raw_active():
                members = party_of(self)
        except Exception:
            log_exc("写入合并背包", once=True)
            members = None
        if members is None or len(members) < 2:
            orig_set(self, value)
            return
        # 把赋值理解为「合并背包变成 value」：从合并视图里消失的物品从各自的主人身上移除，
        # 新出现的物品放到自己身上，队友身上仍在 value 里的物品留在原处
        value = list(value)
        held_by = {id(i): m for m in members[1:] for i in m._items}
        try:
            keep = {id(i) for i in value}
            removed = {}
            for item in SharedItems(self, members):
                owner = held_by.get(id(item))
                if owner is not None and id(item) not in keep:
                    removed.setdefault(id(owner), (owner, []))[1].append(item)
        except Exception:
            # 出错时只更新自己身上的物品，队友的不动（宁可少移除，也不复制物品）
            log_exc("写入合并背包（差异计算）", once=True)
            removed = {}
        orig_set(self, [i for i in value if id(i) not in held_by])
        for owner, items in removed.values():
            owner._items.batch_remove(items)

    return property(items_get, items_set, orig.fdel, orig.__doc__)


# ----------------------------------------------------------------------------
# 补丁
# ----------------------------------------------------------------------------

def patch_character(module):
    Character = module.Character
    if vars(Character).get("_shared_inventory_patched"):
        return
    orig = vars(Character)["items"]
    Character.items = make_items_property(orig)

    orig_has_item = Character.has_item
    getframe = sys._getframe

    def has_item(self, itemname):
        # 个人物品，或调用方本身只看自己身上（例如防水计算）时，has_item 也只看自己，
        # 避免「全队有 → 自己身上找不到」的前后矛盾
        if itemname in PERSONAL_ITEMS or getframe(1).f_code in STATE.raw_codes or in_raw_scope():
            return any(i.name == itemname for i in self._items)
        return orig_has_item(self, itemname)

    Character.has_item = has_item
    Character._shared_inventory_patched = True
    log("已替换 Character.items 为合并背包")


def patch_item(module):
    Item = module.Item
    if vars(Item).get("_shared_inventory_patched"):
        return
    orig_get_storage = Item.get_storage

    def get_storage(self, scope="any"):
        # 先在队伍成员的原始列表里找真正的持有者（合并视图会让每个队员都「包含」这件物品）
        if scope in ("any", "party"):
            try:
                game = dt().game
                if game is not None and game.player is not None:
                    for m in (game.player, *game.companions):
                        if self in m._items:
                            return m
            except Exception:
                log_exc("get_storage", once=True)
        return orig_get_storage(self, scope)

    Item.get_storage = get_storage
    Item._shared_inventory_patched = True
    log("已修正物品归属查询")


def patch_inventorymodal(module):
    InventoryModal = module.InventoryModal
    if vars(InventoryModal).get("_shared_inventory_patched"):
        return
    orig_open = InventoryModal.set_and_open
    orig_close = InventoryModal.close

    def set_and_open(self, obj, *args, **kwargs):
        # 右侧是队友 = 交换物品界面：双方显示各自真实的背包
        try:
            selected = dt().scene.selected
            is_exchange = obj is not selected and party_of(obj) is not None and party_of(selected) is not None
        except Exception:
            log_exc("判断交换界面", once=True)
            is_exchange = False
        STATE.exchange_modal = self if is_exchange else None
        STATE.exchange_opening = is_exchange
        STATE.inventory_opening = self
        try:
            return orig_open(self, obj, *args, **kwargs)
        finally:
            STATE.exchange_opening = False
            STATE.inventory_opening = None

    def close(self, *args, **kwargs):
        try:
            return orig_close(self, *args, **kwargs)
        finally:
            if STATE.exchange_modal is self:
                STATE.exchange_modal = None

    InventoryModal.set_and_open = set_and_open
    InventoryModal.close = close
    add_view_toggle(InventoryModal)
    InventoryModal._shared_inventory_patched = True
    log("已设置队友交换界面")


def view_toggle_available(modal):
    """有队友、不在交换界面时才显示「全队 / 只看自己」切换。"""
    if exchange_active():
        return False
    members = party_of(modal.scene.selected)
    return members is not None and len(members) >= 2


def toggle_own_view(modal):
    STATE.own_view = not STATE.own_view
    save_prefs()
    from ui.sound import soundplay
    soundplay("ui_click_s")
    modal.setup()


def add_view_toggle(InventoryModal):
    """背包面板下方的「全队物品 / 只看自己」切换按钮，以及快捷键。"""
    import pygame
    from paint import Sprite, Surface
    from paint.color import LIMA, WHITE
    from system.opengl import gltext
    from ui.tooltip import Tooltip

    key_name = VIEW_HOTKEY.upper()
    key_code = getattr(pygame, "K_" + VIEW_HOTKEY.lower(), None) or getattr(pygame, "K_" + VIEW_HOTKEY, pygame.K_v)

    # 这些方法由游戏主循环直接调用，抛出异常会让游戏退出，所以全部兜底
    class ViewToggleBtn(Sprite):
        def __init__(self, modal):
            super().__init__(Surface(110, 10))
            self.modal = modal

        def draw(self):
            try:
                text = t("view_own" if STATE.own_view else "view_party", hotkey=key_name)
                gltext(text, 6, LIMA if self.hovered else WHITE, self, "center", option="bold")
            except Exception:
                log_exc("切换按钮绘制", once=True)

        def callback(self):
            try:
                toggle_own_view(self.modal)
            except Exception:
                log_exc("切换按钮点击", once=True)

        def set_tooltip(self):
            try:
                Tooltip.title(t("view_title", hotkey=key_name))
                Tooltip.add(t("view_desc_party"))
                Tooltip.add(t("view_desc_own"))
                Tooltip.lclick = t("view_switch")
            except Exception:
                log_exc("切换按钮提示", once=True)

    def toggle_btn(modal):
        btn = vars(modal).get("_si_view_btn")
        if btn is None:
            btn = modal._si_view_btn = ViewToggleBtn(modal)
        return btn

    orig_setup = InventoryModal._setup
    orig_draw = InventoryModal.draw
    orig_keydown = InventoryModal.keydown
    orig_buttons = vars(InventoryModal)["buttons"]

    def _setup(self, *args, **kwargs):
        result = orig_setup(self, *args, **kwargs)
        try:
            toggle_btn(self).rect.midtop = self.left_area.rect.move(0, 3).midbottom
        except Exception:
            log_exc("切换按钮布局", once=True)
        return result

    def draw(self, *args, **kwargs):
        result = orig_draw(self, *args, **kwargs)
        try:
            if view_toggle_available(self):
                toggle_btn(self).draw()
        except Exception:
            log_exc("切换按钮绘制", once=True)
        return result

    def keydown(self, key, *args, **kwargs):
        try:
            if key == key_code and view_toggle_available(self):
                toggle_own_view(self)
                return None
        except Exception:
            log_exc("切换快捷键", once=True)
        return orig_keydown(self, key, *args, **kwargs)

    def buttons(self):
        btns = orig_buttons.fget(self)
        try:
            if view_toggle_available(self):
                btns = [toggle_btn(self)] + list(btns)
        except Exception:
            log_exc("切换按钮列表", once=True)
        return btns

    InventoryModal._setup = _setup
    InventoryModal.draw = draw
    InventoryModal.keydown = keydown
    InventoryModal.buttons = property(buttons)
    InventoryModal.ViewToggleBtn = ViewToggleBtn


def patch_item_box(module):
    BaseItemBox = module.BaseItemBox
    if vars(BaseItemBox).get("_shared_inventory_patched"):
        return
    from system.opengl import gltext, glrect
    orig_tooltip = BaseItemBox.set_tooltip
    orig_draw_else = BaseItemBox.draw_else

    def set_tooltip(self, *args, **kwargs):
        result = orig_tooltip(self, *args, **kwargs)
        if OWNER_TOOLTIP:
            try:
                add_owner_tooltip(self)
            except Exception:
                log_exc("物品提示", once=True)
        return result

    def draw_else(self, *args, **kwargs):
        result = orig_draw_else(self, *args, **kwargs)
        if OWNER_BADGE:
            try:
                draw_owner_badge(self, gltext, glrect)
            except Exception:
                log_exc("持有者角标", once=True)
        return result

    BaseItemBox.set_tooltip = set_tooltip
    BaseItemBox.draw_else = draw_else
    BaseItemBox._shared_inventory_patched = True
    log("已添加物品携带者提示和角标")


def box_owners(box):
    """格子里的物品按持有者计数：[(持有者, 件数), ...]，只统计主角和队友身上的物品。"""
    items = getattr(box, "items", None)
    if not items or ui_raw_active():
        return []
    omap = owner_map()
    counts = {}
    for item in items:
        owner = omap.get(id(item))
        if owner is not None:
            counts.setdefault(id(owner), [owner, 0])[1] += 1
    return [tuple(v) for v in counts.values()]


def add_owner_tooltip(box):
    selected = dt().scene.selected
    owners = box_owners(box)
    if not owners or all(o is selected for o, _ in owners):
        return
    from ui.tooltip import Tooltip
    from core.utils import ct
    from paint.color import GRAY
    if len(owners) == 1:
        text = t("carried_by", name=display_name(owners[0][0]))
    else:
        parts = ["%s ×%d" % (t("you") if o is selected else display_name(o), n) for o, n in owners]
        text = t("carried_split", parts=" · ".join(parts))
    Tooltip.add(ct(text, GRAY))


def draw_owner_badge(box, gltext, glrect):
    selected = dt().scene.selected
    others = [o for o, _ in box_owners(box) if o is not selected]
    if not others:
        return
    if len(others) == 1:
        label, color = display_name(others[0])[:1].upper(), member_color(others[0])
    else:
        label, color = "+", (233, 233, 233)
    r = box.rect
    masks = getattr(box, "maskrects", None) or None
    glrect((r.x + 1, r.y + 1, 7, 7), (14, 14, 14), maskrect=masks[0] if masks else None, alpha=200)
    gltext(label, 5, color, box, "topleft", (2.2, 1.0), maskrects=masks, option="bold")


def patch_turn(module):
    """安全网：结束回合时出现异常，原版只记录错误，回合永远停在「其他幸存者的回合」转圈。
    这里在原版记录错误之后把控制权交还玩家（做法与游戏自带的低帧率看门狗相同）。"""
    if vars(module).get("_shared_inventory_patched"):
        return
    orig_end_turn = module._end_turn
    orig_try = module.try_end_turn_except_handle_error

    def _end_turn(*args, **kwargs):
        try:
            return orig_end_turn(*args, **kwargs)
        except SystemExit:
            raise
        except Exception:
            STATE.turn_failed = True
            raise

    def try_end_turn_except_handle_error(*args, **kwargs):
        STATE.turn_failed = False
        try:
            return orig_try(*args, **kwargs)
        finally:
            if STATE.turn_failed:
                STATE.turn_failed = False
                recover_turn(module)

    module._end_turn = _end_turn
    module.try_end_turn_except_handle_error = try_end_turn_except_handle_error
    module._shared_inventory_patched = True
    log("已添加结束回合出错恢复")


def recover_turn(turn_module):
    try:
        d = dt()
        scene = d.scene
        if getattr(scene, "name", None) != "main" or scene.turn == "player":
            return
        log("结束回合出错（详见 game_log.log），已恢复玩家回合")
        scene.turn = "player"
        scene.allow_player_input = True
        turn_module.mouse.show()
        from paint.color import YELLOW
        scene.result.add(t("turn_recovered"), d.game.player, YELLOW)
    except Exception:
        log_exc("恢复玩家回合")


def patch_actionpanel(module):
    TurnonBtn = getattr(module.ActionPanel, "TurnonBtn", None)
    if TurnonBtn is None or vars(TurnonBtn).get("_shared_inventory_patched"):
        return
    orig_get_tool = TurnonBtn.get_tool

    def get_tool(self, *args, **kwargs):
        # 「开灯」只从自己身上挑灯，灯亮在谁身上就照亮谁
        with raw_scope():
            return orig_get_tool(self, *args, **kwargs)

    TurnonBtn.get_tool = get_tool
    TurnonBtn._shared_inventory_patched = True
    log("已设置开灯只用自己身上的灯")


def patch_electronics(module):
    Light = module.Light
    if vars(Light).get("_shared_inventory_patched"):
        return
    orig_use = Light.use

    def use(self, char, *args, **kwargs):
        result = orig_use(self, char, *args, **kwargs)
        # 背包里对队友的手电点「使用」：确认后先拿到自己身上再打开
        try:
            btn = dt().scene.popup.confirm_btn
            inner = btn.func

            def confirm():
                try:
                    take_item(char, self)
                except Exception:
                    log_exc("拿取队友的灯", once=True)
                return inner()

            btn.func = confirm
        except Exception:
            log_exc("Light.use", once=True)
        return result

    Light.use = use
    Light._shared_inventory_patched = True
    log("已设置使用队友的灯时先拿到自己身上")


PATCHERS = {
    "core.creature.character": patch_character,
    "core.item.item": patch_item,
    "core.item.electronics": patch_electronics,
    "actions.turn": patch_turn,
    "ui.panels.actionpanel": patch_actionpanel,
    "ui.modals.inventorymodal": patch_inventorymodal,
    "ui.modals.common.inventory": patch_item_box,
}


def on_module_loaded(name, module):
    """由 base_library.zip 里的 MOD 加载器在游戏模块加载完成后调用。"""
    if name in RAW_FUNCS:
        register_raw(name, module)
    patcher = PATCHERS.get(name)
    if patcher is not None:
        patcher(module)


try:
    with open(LOG_PATH, "w", encoding="utf-8") as _f:
        _f.write(time.strftime("[%Y-%m-%d %H:%M:%S] ") + "队伍背包共享 MOD 已加载\n")
except OSError:
    pass
load_prefs()
