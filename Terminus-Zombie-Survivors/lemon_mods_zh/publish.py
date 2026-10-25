"""更新创意工坊条目（LemonCitrine 全系列 MOD 汉化合集）。

上传 workshop\\ 目录，同时更新标题、标签、页面描述，并按 DEPENDENCIES 同步「必需物品」。
Steam 相关的部分在 ..\\tools\\workshop_upload.py（所有 MOD 共用）。
需要 Python 3.12，Steam 处于运行并登录条目所有者的账号：
    uv run --no-project --python 3.12 python -I publish.py "更新说明"
    uv run --no-project --python 3.12 python -I publish.py --dry-run    只检查能否连上 Steam，不上传

workshop_item.json 记录条目编号和 Steam 上当前设置的「必需物品」；不存在时用下面的 ITEM_ID。
"""
import json, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), "tools"))
import workshop_upload  # noqa: E402

CONTENT = os.path.join(HERE, "workshop")
ITEM_ID = 3826727240
TITLE = "LemonCitrine 全系列MOD 简体中文汉化 / LemonCitrine Mods - Simplified Chinese"
TAGS = ["data", "traits", "perks", "Weapons"]
DEPENDENCIES = []  # 必需物品；合集覆盖多个可选 MOD，不强制任何一个
ITEM_FILE = os.path.join(HERE, "workshop_item.json")

change_note = sys.argv[1] if len(sys.argv) > 1 else "update"
ws = workshop_upload.Workshop()
print("Steam user:", ws.steam_id)
if change_note == "--dry-run":
    sys.exit(0)

item = json.load(open(ITEM_FILE)) if os.path.exists(ITEM_FILE) else {"published_file_id": ITEM_ID, "dependencies": []}
file_id = item["published_file_id"]
description = open(os.path.join(HERE, "steam_description.txt"), encoding="utf-8").read()
ws.submit(file_id, CONTENT, os.path.join(CONTENT, "preview.png"), description, change_note, title=TITLE, tags=TAGS)
item["dependencies"] = ws.sync_dependencies(file_id, DEPENDENCIES, item.get("dependencies", []))
json.dump(item, open(ITEM_FILE, "w"))
print(f"https://steamcommunity.com/sharedfiles/filedetails/?id={file_id}")
