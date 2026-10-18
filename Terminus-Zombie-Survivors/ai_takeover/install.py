# -*- coding: utf-8 -*-
"""
主角 AI 托管 MOD 安装 / 卸载脚本。

    python install.py            安装（或在游戏更新后重新安装）
    python install.py --uninstall 卸载
    python install.py --status    查看状态

原理：游戏是 PyInstaller 打包的 Python 3.12 程序，官方 Workshop 模组只支持 JSON 数据，
不能加代码。本脚本把 _internal/base_library.zip 里的标准库模块 linecache.pyc 替换为
「原样执行原 linecache 字节码 + 一个很小的导入钩子」的版本。游戏加载动作面板和主场景时，
钩子会加载本目录的 ai_takeover.py 给游戏类打补丁。原 linecache 的行为完全不变。

必须用 Python 3.12 运行（与游戏内置 Python 版本一致，字节码格式才能兼容）。
"""

import argparse
import importlib.util
import marshal
import os
import shutil
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
GAME_DIR = os.path.dirname(HERE)
ZIP_PATH = os.path.join(GAME_DIR, "_internal", "base_library.zip")
BACKUP_PATH = os.path.join(HERE, "backup", "base_library.zip.orig")
TARGET = "linecache.pyc"
MARK = "TERMINUS_AI_TAKEOVER_BOOTSTRAP_V1"

BOOTSTRAP_TEMPLATE = r'''
_TAI_MARK = %(mark)r
import marshal as _tai_marshal
_TAI_ORIG_PYC = %(orig)r
exec(_tai_marshal.loads(_TAI_ORIG_PYC[16:]), globals())
del _tai_marshal


def _tai_bootstrap():
    import sys
    if getattr(sys, "_tai_bootstrapped", False):
        return
    sys._tai_bootstrapped = True
    import os
    base = getattr(sys, "_MEIPASS", None) or os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    mod_path = os.path.join(os.path.dirname(base), "ai_takeover", "ai_takeover.py")
    if not os.path.isfile(mod_path):
        return
    targets = ("ui.panels.actionpanel", "scenes.main")
    state = {}

    def notify(name, module):
        try:
            mod = state.get("mod")
            if mod is None:
                import importlib.util
                spec = importlib.util.spec_from_file_location("ai_takeover_mod", mod_path)
                mod = importlib.util.module_from_spec(spec)
                sys.modules["ai_takeover_mod"] = mod
                spec.loader.exec_module(mod)
                state["mod"] = mod
            mod.on_module_loaded(name, module)
        except Exception:
            try:
                import traceback
                log = os.path.join(os.path.dirname(mod_path), "ai_takeover.log")
                with open(log, "a", encoding="utf-8") as f:
                    f.write("bootstrap error while patching %%s:\n%%s\n" %% (name, traceback.format_exc()))
            except Exception:
                pass

    class _Loader:
        def __init__(self, inner, name):
            self._tai_inner = inner
            self._tai_name = name

        def create_module(self, spec):
            create = getattr(self._tai_inner, "create_module", None)
            return create(spec) if create is not None else None

        def exec_module(self, module):
            self._tai_inner.exec_module(module)
            notify(self._tai_name, module)

        def __getattr__(self, attr):
            return getattr(self._tai_inner, attr)

    class _Finder:
        def find_spec(self, name, path=None, target=None):
            if name not in targets:
                return None
            for finder in sys.meta_path:
                if finder is self:
                    continue
                find = getattr(finder, "find_spec", None)
                if find is None:
                    continue
                spec = find(name, path, target)
                if spec is not None:
                    if spec.loader is not None and hasattr(spec.loader, "exec_module"):
                        spec.loader = _Loader(spec.loader, name)
                    return spec
            return None

    sys.meta_path.insert(0, _Finder())


try:
    _tai_bootstrap()
except Exception:
    pass
'''


def read_zip():
    with zipfile.ZipFile(ZIP_PATH) as z:
        return z.read(TARGET), z.getinfo(TARGET)


def is_patched(pyc):
    return MARK.encode() in pyc


def extract_original(pyc):
    """从已打补丁的 linecache.pyc 里取出嵌入的原始 pyc。"""
    code = marshal.loads(pyc[16:])
    for const in code.co_consts:
        if isinstance(const, bytes) and const[:4] == pyc[:4] and len(const) > 16:
            return const
    raise RuntimeError("无法从补丁中找到原始 linecache 字节码")


def build_patched(orig_pyc):
    src = BOOTSTRAP_TEMPLATE % {"mark": MARK, "orig": orig_pyc}
    code = compile(src, "<ai_takeover_bootstrap>", "exec", dont_inherit=True, optimize=0)
    # 头部沿用原文件（魔数 + flags + hash），zipimport 不会校验源码
    return orig_pyc[:16] + marshal.dumps(code)


def rewrite_zip(new_pyc, info):
    tmp = ZIP_PATH + ".tmp"
    with zipfile.ZipFile(ZIP_PATH) as src, zipfile.ZipFile(tmp, "w") as dst:
        for item in src.infolist():
            data = new_pyc if item.filename == TARGET else src.read(item.filename)
            dst.writestr(item, data)
    try:
        os.replace(tmp, ZIP_PATH)
    except PermissionError:
        os.remove(tmp)
        sys.exit("写入失败：base_library.zip 被占用。请先退出游戏再运行。")


def check_python(pyc):
    if pyc[:4] != importlib.util.MAGIC_NUMBER:
        sys.exit(
            "Python 版本不匹配：游戏字节码魔数 %s，当前 Python %s 的魔数 %s。\n"
            "请用 Python 3.12 运行本脚本（例如：uv run --no-project --python 3.12 install.py）。"
            % (pyc[:4].hex(), sys.version.split()[0], importlib.util.MAGIC_NUMBER.hex())
        )


def install():
    pyc, info = read_zip()
    check_python(pyc)
    if is_patched(pyc):
        orig = extract_original(pyc)
    else:
        orig = pyc
        os.makedirs(os.path.dirname(BACKUP_PATH), exist_ok=True)
        shutil.copy2(ZIP_PATH, BACKUP_PATH)
        print("已备份原文件到", BACKUP_PATH)
    rewrite_zip(build_patched(orig), info)
    print("安装完成。启动游戏后，动作面板里会出现机器人图标的「AI 托管」按钮。")


def uninstall():
    pyc, info = read_zip()
    if not is_patched(pyc):
        print("当前未安装，无需卸载。")
        return
    rewrite_zip(extract_original(pyc), info)
    print("已卸载（base_library.zip 已还原）。可以删除 ai_takeover 文件夹。")


def status():
    pyc, _ = read_zip()
    print("已安装" if is_patched(pyc) else "未安装")


def main():
    parser = argparse.ArgumentParser(description="主角 AI 托管 MOD 安装工具")
    parser.add_argument("--uninstall", action="store_true", help="卸载")
    parser.add_argument("--status", action="store_true", help="查看安装状态")
    args = parser.parse_args()
    if not os.path.isfile(ZIP_PATH):
        sys.exit("找不到 %s，请把 ai_takeover 文件夹放在游戏根目录（Terminus.exe 旁边）。" % ZIP_PATH)
    if args.status:
        status()
    elif args.uninstall:
        uninstall()
    else:
        install()


if __name__ == "__main__":
    main()
