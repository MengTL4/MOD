# -*- coding: utf-8 -*-
r"""
生成发布内容（免 Python 安装）。需要 Python 3.12 运行：

    uv run --no-project --python 3.12 build_release.py

输出：
  <游戏目录>\<MOD>_workshop\                     创意工坊上传目录（保留 workshop_item.json）
  <游戏目录>\<MOD>_release\<ZIP_NAME>_v<版本>.zip  外部发布用，内容相同

release_files 里的文本会替换这些占位符：
  __VERSION__  __ORIG_SHA256__  __ITEM_ID__（创意工坊条目编号，取自 workshop_item.json）
  __MOD__  __MOD_TITLE__  __DONE_MSG__
"""

import hashlib
import json
import os
import shutil
import sys
import zipfile

import install

# ----------------------------------------------------------------------------
# 本 MOD 的发布配置
# ----------------------------------------------------------------------------
VERSION = "1.1"
ZIP_NAME = "TerminusCompanionAI"
MOD_TITLE = "队友 AI 加强 MOD"
DONE_MSG = "安装完成！启动游戏后队友 AI 加强生效，每次决策记录在 <游戏目录>\\companion_ai\\companion_ai.log。"
WORKSHOP_TITLE = "队友 AI 加强 / Smarter Companion AI (需手动安装 Manual install)"
WORKSHOP_TAGS = ["data"]
# ----------------------------------------------------------------------------

HERE = install.HERE
MOD = os.path.basename(HERE)
FILES = os.path.join(HERE, "release_files")
WORKSHOP_DIR = os.path.join(install.GAME_DIR, MOD + "_workshop")
RELEASE_DIR = os.path.join(install.GAME_DIR, MOD + "_release")
ITEM_FILE = os.path.join(WORKSHOP_DIR, "workshop_item.json")
KEEP = {"workshop_item.json"}   # 上传工具写的条目编号，必须保留才能更新同一个条目
TEXT_FILES = ("使用说明.txt", "README_EN.txt")


def original_linecache():
    pyc, _ = install.read_zip()
    if install.is_patched(pyc):
        return install.extract_original(pyc)
    return pyc


def item_id():
    try:
        with open(ITEM_FILE, encoding="utf-8") as f:
            return str(json.load(f)["published_file_id"])
    except (OSError, ValueError, KeyError):
        return "__ITEM_ID__"


def fill(text, sha):
    for key, value in (("__VERSION__", VERSION), ("__ORIG_SHA256__", sha), ("__ITEM_ID__", item_id()),
                       ("__MOD_TITLE__", MOD_TITLE), ("__DONE_MSG__", DONE_MSG), ("__MOD__", MOD)):
        text = text.replace(key, value)
    return text


def read(name):
    with open(os.path.join(FILES, name), encoding="utf-8") as f:
        return f.read()


def write_text(path, text, bom=False):
    with open(path, "w", encoding="utf-8-sig" if bom else "utf-8", newline="\r\n") as f:
        f.write(text)


def build_workshop_dir(orig):
    sha = hashlib.sha256(orig).hexdigest()
    os.makedirs(WORKSHOP_DIR, exist_ok=True)
    for name in os.listdir(WORKSHOP_DIR):
        if name not in KEEP:
            path = os.path.join(WORKSHOP_DIR, name)
            shutil.rmtree(path) if os.path.isdir(path) else os.remove(path)

    mod = os.path.join(WORKSHOP_DIR, MOD)
    os.makedirs(os.path.join(mod, "payload"))
    with open(os.path.join(mod, "payload", "linecache.pyc"), "wb") as f:
        f.write(install.build_patched(orig))
    # 原始文件随包附带：最后一个 MOD 卸载时用它还原游戏（install.ps1 会校验 sha256）
    with open(os.path.join(mod, "payload", "linecache.orig.pyc"), "wb") as f:
        f.write(orig)
    shutil.copy2(os.path.join(HERE, MOD + ".py"), mod)

    for name in ("install.bat", "uninstall.bat", "preview.png"):
        shutil.copy2(os.path.join(FILES, name), WORKSHOP_DIR)
    # PowerShell 5 需要 BOM 才能正确读取中文
    write_text(os.path.join(WORKSHOP_DIR, "install.ps1"), fill(read("install.ps1"), sha), bom=True)
    for name in TEXT_FILES:
        write_text(os.path.join(WORKSHOP_DIR, name), fill(read(name), sha), bom=True)

    manifest = json.loads(fill(read("manifest.json"), sha))
    with open(os.path.join(WORKSHOP_DIR, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
    return sha


def steam_description():
    """创意工坊页面的长描述（BBCode），上传脚本使用。"""
    return fill(read("steam_description.txt"), "")


def build_zip():
    os.makedirs(RELEASE_DIR, exist_ok=True)
    zip_path = os.path.join(RELEASE_DIR, "%s_v%s.zip" % (ZIP_NAME, VERSION))
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for root, _, names in os.walk(WORKSHOP_DIR):
            for name in names:
                full = os.path.join(root, name)
                rel = os.path.relpath(full, WORKSHOP_DIR)
                if rel not in KEEP:
                    z.write(full, os.path.join(ZIP_NAME, rel))
    return zip_path


def main():
    orig = original_linecache()
    install.check_python(orig)
    sha = build_workshop_dir(orig)
    zip_path = build_zip()
    print("原始 linecache.pyc sha256:", sha)
    print("创意工坊条目编号:", item_id())
    print("创意工坊上传目录:", WORKSHOP_DIR)
    print("外部发布包:", zip_path)


if __name__ == "__main__":
    sys.exit(main())
