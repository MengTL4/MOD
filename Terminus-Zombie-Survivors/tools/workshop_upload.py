r"""
用游戏自带的 steamworks 绑定更新创意工坊条目（Terminus 的所有 MOD 共用）。

绑定直接从 Terminus.exe 的 PYZ 里取，不用另外解包。需要 Python 3.12（与游戏一致，字节码才兼容），
Steam 处于运行并登录条目所有者的账号。本文件夹要放在游戏根目录（和 Terminus.exe 同级）。

含代码的 MOD（有 build_release.py 的）先构建再上传：

    cd <MOD 文件夹>
    uv run --no-project --python 3.12 build_release.py
    uv run --no-project --python 3.12 python -I ..\tools\workshop_upload.py <MOD 名> "更新说明"
    uv run --no-project --python 3.12 python -I ..\tools\workshop_upload.py <MOD 名> --dry-run

上传 <游戏目录>\<MOD>_workshop\，页面描述取自 build_release.steam_description()（release_files\steam_description.txt
填好占位符）。标题、标签、可见性保持条目上现有的设置。

新 MOD 第一次发布：
    ...\workshop_upload.py <MOD 名> --create          建条目，编号写进 <MOD>_workshop\workshop_item.json
    （再运行一次 build_release.py，把说明文字里的条目编号填上）
    ...\workshop_upload.py <MOD 名> "首次发布" --public
--public 时按 build_release.py 里的 WORKSHOP_TITLE / WORKSHOP_TAGS 设置标题和标签，并设为公开。

不要用游戏自带的 WorkshopUploader，它会用 manifest.description 覆盖页面上的长描述。
"""

import ctypes
import importlib.util
import json
import marshal
import os
import shutil
import struct
import sys
import tempfile
import time
import types
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
GAME = os.path.dirname(HERE)
INTERNAL = os.path.join(GAME, "_internal")
APP_ID = 1534980
DESCRIPTION_MAX = 8000                 # Steam 按 UTF-8 字节数算


def _extract_steamworks(exe, out):
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


class Workshop:
    """连上 Steam 的创意工坊会话。"""

    def __init__(self):
        work = tempfile.mkdtemp(prefix="terminus_workshop_")
        lib, self.bindir = os.path.join(work, "lib"), os.path.join(work, "bin")
        _extract_steamworks(os.path.join(GAME, "Terminus.exe"), lib)
        os.makedirs(self.bindir)
        for dll in ("steam_api64.dll", "SteamworksPy64.dll"):
            shutil.copy2(os.path.join(INTERNAL, dll), self.bindir)
        with open(os.path.join(self.bindir, "steam_appid.txt"), "w") as f:
            f.write(str(APP_ID))

        # steamworks/__init__ 会 import 游戏的 data 和 resources 模块，给它们放替身
        data_mod = types.ModuleType("data")
        data_mod.dt = types.SimpleNamespace()
        res_mod = types.ModuleType("resources")
        res_mod.resource_path = lambda p: os.path.join(self.bindir, p)
        sys.modules.setdefault("data", data_mod)
        sys.modules.setdefault("resources", res_mod)
        sys.path.insert(0, lib)
        cwd = os.getcwd()
        os.chdir(self.bindir)
        os.add_dll_directory(self.bindir)
        try:
            from steamworks import STEAMWORKS
            self.sw = STEAMWORKS()
            self.sw.initialize()
        finally:
            os.chdir(cwd)
        self.steam_id = self.sw.Users.GetSteamID()

    def _wait(self, done, what, timeout):
        end = time.time() + timeout
        while not done():
            self.sw.run_callbacks()
            time.sleep(0.1)
            if time.time() > end:
                sys.exit(what + ": timed out")

    def create(self):
        """新建一个空的社区条目，返回编号。"""
        from steamworks.enums import EWorkshopFileType
        box = {}
        self.sw.Workshop.CreateItem(APP_ID, EWorkshopFileType(0), lambda r: box.setdefault("r", r))
        self._wait(lambda: "r" in box, "CreateItem", 120)
        r = box["r"]
        print("CreateItem result:", r.result, "needs agreement:", r.userNeedsToAcceptWorkshopLegalAgreement)
        if r.result != 1:
            sys.exit("CreateItem failed")
        return int(r.publishedFileId)

    def submit(self, item_id, content, preview, description, change_note, title=None, tags=None, public=False):
        """更新条目的内容、预览图、描述；title / tags 为 None 时保持不变，public 时设为公开。"""
        size = len(description.encode("utf-8"))
        if size > DESCRIPTION_MAX:          # 超长时 Steam 只打一行警告、照样提交，描述却没改
            sys.exit("页面描述 %d 字节，超过 Steam 上限 %d（中文每字 3 字节），请删减后再上传。" % (size, DESCRIPTION_MAX))
        ws = self.sw.Workshop
        h = ws.StartItemUpdate(APP_ID, item_id)
        if title is not None:
            ws.SetItemTitle(h, title)
        if tags is not None:
            ws.SetItemTags(h, tags)
        if public:
            from steamworks.enums import ERemoteStoragePublishedFileVisibility
            ws.SetItemVisibility(h, ERemoteStoragePublishedFileVisibility(0))
        ws.SetItemDescription(h, description)
        ws.SetItemContent(h, os.path.abspath(content))
        ws.SetItemPreview(h, os.path.abspath(preview))
        box = {}
        ws.SubmitItemUpdate(h, change_note, lambda r: box.setdefault("r", r))
        self._wait(lambda: "r" in box, "SubmitItemUpdate", 900)
        r = box["r"]
        print("SubmitItemUpdate result:", r.result, "needs agreement:", r.userNeedsToAcceptWorkshopLegalAgreement)
        if r.result != 1:
            # 刚上传后条目在审核中时会是 ACCESS_DENIED(15)，等审核结束再传
            sys.exit("SubmitItemUpdate failed")

    def sync_dependencies(self, item_id, want, current):
        """把条目的「必需物品」从 current 同步成 want，返回 Steam 上实际的列表。
        （python 绑定没有这个接口，直接调 steam_api64.dll 的 flat API）"""
        want, current = set(want), set(current)
        if want == current:
            return sorted(current)
        api = ctypes.CDLL(os.path.join(self.bindir, "steam_api64.dll"))
        api.SteamAPI_SteamUGC_v018.restype = ctypes.c_void_p
        api.SteamAPI_SteamUtils_v010.restype = ctypes.c_void_p
        for fn in ("SteamAPI_ISteamUGC_AddDependency", "SteamAPI_ISteamUGC_RemoveDependency"):
            getattr(api, fn).restype = ctypes.c_uint64
            getattr(api, fn).argtypes = [ctypes.c_void_p, ctypes.c_uint64, ctypes.c_uint64]
        api.SteamAPI_ISteamUtils_IsAPICallCompleted.restype = ctypes.c_bool
        api.SteamAPI_ISteamUtils_IsAPICallCompleted.argtypes = [ctypes.c_void_p, ctypes.c_uint64, ctypes.POINTER(ctypes.c_bool)]
        api.SteamAPI_ISteamUtils_GetAPICallResult.restype = ctypes.c_bool
        api.SteamAPI_ISteamUtils_GetAPICallResult.argtypes = [
            ctypes.c_void_p, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.POINTER(ctypes.c_bool)]

        class UGCDependencyResult_t(ctypes.Structure):  # AddUGCDependencyResult_t / RemoveUGCDependencyResult_t
            _fields_ = [("result", ctypes.c_int32), ("parent", ctypes.c_uint64), ("child", ctypes.c_uint64)]

        ugc, utils = api.SteamAPI_SteamUGC_v018(), api.SteamAPI_SteamUtils_v010()

        def call(fn, callback_id, child):
            handle = getattr(api, fn)(ugc, item_id, child)
            failed = ctypes.c_bool(False)
            self._wait(lambda: api.SteamAPI_ISteamUtils_IsAPICallCompleted(utils, handle, ctypes.byref(failed)), fn, 60)
            res = UGCDependencyResult_t()
            ok = api.SteamAPI_ISteamUtils_GetAPICallResult(
                utils, handle, ctypes.byref(res), ctypes.sizeof(res), callback_id, ctypes.byref(failed))
            ok = ok and res.result == 1
            print(fn.rsplit("_", 1)[1], child, "ok" if ok else "failed (result=%d)" % res.result)
            return ok

        for child in sorted(want - current):
            if call("SteamAPI_ISteamUGC_AddDependency", 3412, child):
                current.add(child)
        for child in sorted(current - want):
            if call("SteamAPI_ISteamUGC_RemoveDependency", 3413, child):
                current.discard(child)
        return sorted(current)


def load_build(mod):
    mod_dir = os.path.join(GAME, mod)
    sys.path.insert(0, mod_dir)
    spec = importlib.util.spec_from_file_location("build_release", os.path.join(mod_dir, "build_release.py"))
    build = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(build)
    return build


def create_code_mod(mod):
    """新 MOD：建条目，编号写进 <MOD>_workshop\\workshop_item.json。"""
    build = load_build(mod)
    if str(build.item_id()).isdigit():
        sys.exit("%s 已有条目 %s" % (mod, build.item_id()))
    ws = Workshop()
    item_id = ws.create()
    os.makedirs(build.WORKSHOP_DIR, exist_ok=True)
    with open(build.ITEM_FILE, "w", encoding="utf-8") as f:
        json.dump({"published_file_id": item_id, "owner_steam_id": str(ws.steam_id)}, f)
    print("新条目 %s，已写入 %s。再运行一次 build_release.py，然后带 --public 上传。" % (item_id, build.ITEM_FILE))


def upload_code_mod(mod, change_note, public=False):
    """含代码的 MOD：上传 build_release.py 生成的 <MOD>_workshop 目录。"""
    build = load_build(mod)

    manifest_path = os.path.join(build.WORKSHOP_DIR, "manifest.json")
    if not os.path.isfile(manifest_path):
        sys.exit("先运行 build_release.py 生成 " + build.WORKSHOP_DIR)
    built = json.load(open(manifest_path, encoding="utf-8")).get("version")
    if built != build.VERSION:
        sys.exit("上传目录是 v%s，build_release.py 是 v%s：先重新运行 build_release.py" % (built, build.VERSION))
    item_id = build.item_id()
    if not str(item_id).isdigit():
        sys.exit("找不到条目编号（%s 里的 workshop_item.json）" % build.WORKSHOP_DIR)

    ws = Workshop()
    print("Steam user:", ws.steam_id)
    print("%s v%s → 条目 %s" % (mod, build.VERSION, item_id))
    if change_note == "--dry-run":
        return
    title = getattr(build, "WORKSHOP_TITLE", None) if public else None
    tags = getattr(build, "WORKSHOP_TAGS", None) if public else None
    ws.submit(int(item_id), build.WORKSHOP_DIR, os.path.join(build.WORKSHOP_DIR, "preview.png"),
              build.steam_description(), change_note, title=title, tags=tags, public=public)
    print("https://steamcommunity.com/sharedfiles/filedetails/?id=%s" % item_id)


if __name__ == "__main__":
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    if sys.argv[2] == "--create":
        create_code_mod(sys.argv[1])
    else:
        upload_code_mod(sys.argv[1], sys.argv[2], public="--public" in sys.argv[3:])
