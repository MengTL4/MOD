"""更新创意工坊条目（LemonCitrine 全系列 MOD 汉化合集）。

用游戏自带的 steamworks 绑定上传 workshop\\ 目录；绑定直接从 Terminus.exe 里取，不用另外解包。
需要 Python 3.12（与游戏一致，字节码才兼容），Steam 处于运行并登录条目所有者的账号：
    uv run --no-project --python 3.12 python -I publish.py "更新说明"
    uv run --no-project --python 3.12 python -I publish.py --dry-run    只检查能否连上 Steam，不上传

本文件夹要放在游戏根目录（和 Terminus.exe 同级）。
workshop_item.json 记录条目编号和 Steam 上当前设置的「必需物品」；不存在时用下面的 ITEM_ID。
"""
import ctypes, importlib.util, json, marshal, os, shutil, struct, sys, tempfile, time, types, zlib

HERE = os.path.dirname(os.path.abspath(__file__))
GAME = os.path.dirname(HERE)
CONTENT = os.path.join(HERE, "workshop")
INTERNAL = os.path.join(GAME, "_internal")
APP_ID = 1534980
ITEM_ID = 3826727240
TITLE = "LemonCitrine 全系列MOD 简体中文汉化 / LemonCitrine Mods - Simplified Chinese"
TAGS = ["data", "traits", "perks", "Weapons"]
DEPENDENCIES = []  # 必需物品；合集覆盖多个可选 MOD，不强制任何一个
ITEM_FILE = os.path.join(HERE, "workshop_item.json")


def extract_steamworks(exe, out):
    """从 PyInstaller 的 CArchive → PYZ 里取出 steamworks 包，写成无源码 .pyc。"""
    data = open(exe, "rb").read()
    cookie = data.rfind(b"MEI\014\013\012\013\016")
    pkg_len, toc_pos, toc_len, _ = struct.unpack("!iiii", data[cookie + 8:cookie + 24])
    start = cookie + 88 - pkg_len
    p, end = start + toc_pos, start + toc_pos + toc_len
    while p < end:
        entry_len, = struct.unpack("!i", data[p:p + 4])
        pos, clen, _, cflag, typ = struct.unpack("!iiiBc", data[p + 4:p + 18])
        p += entry_len
        if typ != b"z":
            continue
        pyz = data[start + pos:start + pos + clen]
        if cflag:
            pyz = zlib.decompress(pyz)
        toc, = struct.unpack("!i", pyz[8:12])
        entries = marshal.loads(pyz[toc:])
        for name, ent in (entries.items() if isinstance(entries, dict) else entries):
            if name != "steamworks" and not name.startswith("steamworks."):
                continue
            is_pkg, mpos, mlen = ent[-3:]
            code = zlib.decompressobj().decompress(pyz[mpos:mpos + mlen])
            path = os.path.join(out, *name.split(".")) + (os.sep + "__init__" if is_pkg == 1 else "") + ".pyc"
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as f:
                f.write(importlib.util.MAGIC_NUMBER + b"\0" * 12 + code)
        return
    sys.exit("Terminus.exe 里没有找到 PYZ")


change_note = sys.argv[1] if len(sys.argv) > 1 else "update"
work = tempfile.mkdtemp(prefix="lemon_mods_zh_publish_")
lib, bindir = os.path.join(work, "lib"), os.path.join(work, "bin")
extract_steamworks(os.path.join(GAME, "Terminus.exe"), lib)
os.makedirs(bindir)
for dll in ("steam_api64.dll", "SteamworksPy64.dll"):
    shutil.copy2(os.path.join(INTERNAL, dll), bindir)
with open(os.path.join(bindir, "steam_appid.txt"), "w") as f:
    f.write(str(APP_ID))

# steamworks/__init__ 会 import 游戏的 data 和 resources 模块，给它们放替身
data_mod = types.ModuleType("data"); data_mod.dt = types.SimpleNamespace()
res_mod = types.ModuleType("resources"); res_mod.resource_path = lambda p: os.path.join(bindir, p)
sys.modules["data"] = data_mod; sys.modules["resources"] = res_mod
sys.path.insert(0, lib)
os.chdir(bindir)
os.add_dll_directory(bindir)

from steamworks import STEAMWORKS

sw = STEAMWORKS()
sw.initialize()
print("Steam user:", sw.Users.GetSteamID())
if change_note == "--dry-run":
    sys.exit(0)

item = json.load(open(ITEM_FILE)) if os.path.exists(ITEM_FILE) else {"published_file_id": ITEM_ID, "dependencies": []}
file_id = item["published_file_id"]

h = sw.Workshop.StartItemUpdate(APP_ID, file_id)
sw.Workshop.SetItemTitle(h, TITLE)
sw.Workshop.SetItemDescription(h, open(os.path.join(HERE, "steam_description.txt"), encoding="utf-8").read())
sw.Workshop.SetItemTags(h, TAGS)
sw.Workshop.SetItemContent(h, CONTENT)
sw.Workshop.SetItemPreview(h, os.path.join(CONTENT, "preview.png"))
box = {}
sw.Workshop.SubmitItemUpdate(h, change_note, lambda r: box.setdefault("r", r))
end = time.time() + 900
while "r" not in box:
    sw.run_callbacks(); time.sleep(0.1)
    if time.time() > end:
        sys.exit("SubmitItemUpdate: timed out")
r = box["r"]
print("SubmitItemUpdate result:", r.result, "needs agreement:", r.userNeedsToAcceptWorkshopLegalAgreement)
if r.result != 1:
    sys.exit("SubmitItemUpdate failed")

# 同步「必需物品」（python 绑定没有这个接口，直接调 steam_api64.dll 的 flat API）
current = set(item.get("dependencies", []))
if current != set(DEPENDENCIES):
    api = ctypes.CDLL(os.path.join(bindir, "steam_api64.dll"))
    api.SteamAPI_SteamUGC_v018.restype = ctypes.c_void_p
    api.SteamAPI_SteamUtils_v010.restype = ctypes.c_void_p
    for fn in ("SteamAPI_ISteamUGC_AddDependency", "SteamAPI_ISteamUGC_RemoveDependency"):
        getattr(api, fn).restype = ctypes.c_uint64
        getattr(api, fn).argtypes = [ctypes.c_void_p, ctypes.c_uint64, ctypes.c_uint64]
    api.SteamAPI_ISteamUtils_IsAPICallCompleted.restype = ctypes.c_bool
    api.SteamAPI_ISteamUtils_IsAPICallCompleted.argtypes = [ctypes.c_void_p, ctypes.c_uint64, ctypes.POINTER(ctypes.c_bool)]
    api.SteamAPI_ISteamUtils_GetAPICallResult.restype = ctypes.c_bool
    api.SteamAPI_ISteamUtils_GetAPICallResult.argtypes = [ctypes.c_void_p, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.POINTER(ctypes.c_bool)]

    class UGCDependencyResult_t(ctypes.Structure):  # AddUGCDependencyResult_t / RemoveUGCDependencyResult_t
        _fields_ = [("result", ctypes.c_int32), ("parent", ctypes.c_uint64), ("child", ctypes.c_uint64)]

    ugc, utils = api.SteamAPI_SteamUGC_v018(), api.SteamAPI_SteamUtils_v010()

    def dep_call(fn, callback_id, child):
        call = getattr(api, fn)(ugc, file_id, child)
        failed = ctypes.c_bool(False)
        end = time.time() + 60
        while not api.SteamAPI_ISteamUtils_IsAPICallCompleted(utils, call, ctypes.byref(failed)) and time.time() < end:
            sw.run_callbacks(); time.sleep(0.1)
        res = UGCDependencyResult_t()
        ok = api.SteamAPI_ISteamUtils_GetAPICallResult(utils, call, ctypes.byref(res), ctypes.sizeof(res), callback_id, ctypes.byref(failed))
        print(fn.rsplit("_", 1)[1], child, "ok" if ok and res.result == 1 else f"failed (ok={ok}, result={res.result})")
        return ok and res.result == 1

    for child in sorted(set(DEPENDENCIES) - current):
        if dep_call("SteamAPI_ISteamUGC_AddDependency", 3412, child):
            current.add(child)
    for child in sorted(current - set(DEPENDENCIES)):
        if dep_call("SteamAPI_ISteamUGC_RemoveDependency", 3413, child):
            current.discard(child)
    item["dependencies"] = sorted(current)
    json.dump(item, open(ITEM_FILE, "w"))

print(f"https://steamcommunity.com/sharedfiles/filedetails/?id={file_id}")
