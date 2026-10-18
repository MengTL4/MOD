# -*- coding: utf-8 -*-
r"""
生成发布内容（免 Python 安装）。需要 Python 3.12 运行：

    uv run --no-project --python 3.12 build_release.py

输出：
  <游戏目录>\ai_takeover_workshop\                 创意工坊上传目录（保留 workshop_item.json）
  <游戏目录>\ai_takeover_release\TerminusAITakeover_v<版本>.zip   外部发布用，内容相同
"""

import hashlib
import json
import os
import shutil
import sys
import zipfile

import install

VERSION = "1.1"
HERE = install.HERE
FILES = os.path.join(HERE, "release_files")
WORKSHOP_DIR = os.path.join(install.GAME_DIR, "ai_takeover_workshop")
RELEASE_DIR = os.path.join(install.GAME_DIR, "ai_takeover_release")
KEEP = {"workshop_item.json"}   # 上传工具写的条目编号，必须保留才能更新同一个条目


def original_linecache():
    pyc, _ = install.read_zip()
    if install.is_patched(pyc):
        return install.extract_original(pyc)
    return pyc


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

    mod = os.path.join(WORKSHOP_DIR, "ai_takeover")
    os.makedirs(os.path.join(mod, "payload"))
    with open(os.path.join(mod, "payload", "linecache.pyc"), "wb") as f:
        f.write(install.build_patched(orig))
    shutil.copy2(os.path.join(HERE, "ai_takeover.py"), mod)

    for name in ("install.bat", "uninstall.bat", "preview.png"):
        shutil.copy2(os.path.join(FILES, name), WORKSHOP_DIR)
    write_text(os.path.join(WORKSHOP_DIR, "install.ps1"), read("install.ps1").replace("__ORIG_SHA256__", sha), bom=True)
    for name in ("使用说明.txt", "README_EN.txt"):
        write_text(os.path.join(WORKSHOP_DIR, name), read(name), bom=True)

    manifest = json.loads(read("manifest.json").replace("__VERSION__", VERSION))
    with open(os.path.join(WORKSHOP_DIR, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
    return sha


def build_zip():
    os.makedirs(RELEASE_DIR, exist_ok=True)
    zip_path = os.path.join(RELEASE_DIR, "TerminusAITakeover_v%s.zip" % VERSION)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for root, _, names in os.walk(WORKSHOP_DIR):
            for name in names:
                full = os.path.join(root, name)
                rel = os.path.relpath(full, WORKSHOP_DIR)
                if rel not in KEEP:
                    z.write(full, os.path.join("TerminusAITakeover", rel))
    return zip_path


def main():
    orig = original_linecache()
    install.check_python(orig)
    sha = build_workshop_dir(orig)
    zip_path = build_zip()
    print("原始 linecache.pyc sha256:", sha)
    print("创意工坊上传目录:", WORKSHOP_DIR)
    print("外部发布包:", zip_path)


if __name__ == "__main__":
    sys.exit(main())
