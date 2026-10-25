# Terminus: Zombie Survivors

| MOD | 类型 | 创意工坊 | 说明 |
|---|---|---|---|
| [AI 托管主角](ai_takeover/README.md)（`ai_takeover`） | Python 注入 | [3821926212](https://steamcommunity.com/sharedfiles/filedetails/?id=3821926212) | 一键让游戏自带的队友 AI 接管主角行动一回合，支持持续托管 |
| [队伍背包共享](shared_inventory/README.md)（`shared_inventory`） | Python 注入 | [3825232685](https://steamcommunity.com/sharedfiles/filedetails/?id=3825232685) | 主角和队友的背包合并成一个，制作、烹饪、用药、队友 AI 都从全队物品里找 |
| [LemonCitrine 全系列 MOD 汉化](lemon_mods_zh/README.md)（`lemon_mods_zh`） | 官方数据 MOD | [3826727240](https://steamcommunity.com/sharedfiles/filedetails/?id=3826727240) | 作者 LemonCitrine 所有 MOD 的简体中文翻译合集 |

两个 Python 注入 MOD 可以同时使用。各自的用法、配置、排错见各目录的 README。

## 玩家安装（Python 注入 MOD）

游戏官方的创意工坊模组只支持 JSON/图片，不执行代码，所以这两个 MOD 需要手动运行一次安装器，模组管理器里的开关对它们无效。

- **Steam 创意工坊**：订阅后退出游戏，打开 `<Steam 库>\steamapps\workshop\content\1534980\<条目编号>`，双击 `install.bat`。
- **手动**：用 `build_release.py` 生成的 zip，解压到任意位置，双击 `install.bat`。

不需要 Python。安装器会自动找到游戏目录、先校验游戏版本再改动；卸载双击 `uninstall.bat`。
游戏或 MOD 更新后重新运行一次 `install.bat`。

## 原理：共用的 MOD 加载器

游戏是 PyInstaller 打包的 Python 3.12 程序，游戏逻辑是 `Terminus.exe` 里 PYZ 中的普通字节码。

1. 安装器把 `_internal\base_library.zip` 里的标准库 `linecache.pyc` 换成「原样执行原字节码 + 一个 MOD 加载器」的版本（标记 `TERMINUS_AI_TAKEOVER_BOOTSTRAP_V2_MODLOADER`）。
2. 加载器加载游戏目录下所有 `<名字>\<名字>.py` 形式的 MOD，读取 MOD 的 `PATCH_TARGETS`，在这些游戏模块导入完成后调用 `on_module_loaded(name, module)` 给游戏类打补丁。
3. MOD 目录里有 `DISABLED` 文件时跳过该 MOD。卸载时如果还有别的 MOD 在用加载器，只生成 `DISABLED`；最后一个卸载的 MOD 用安装包里的原版 `linecache.pyc`（SHA256 校验过）还原游戏。

**新 MOD 的安装器必须装这个 V2 加载器**，不能另写只加载自己的引导代码，否则会把其他 MOD 顶掉。
两个 MOD 的 `release_files/install.ps1` 是同一个模板，修改时保持一致。

出错时不会让游戏崩溃，错误写在 `<游戏目录>\<MOD>\<MOD>.log`。

## 开发与发布

需要 Python 3.12（与游戏内置版本一致，字节码才兼容），例如 `uv`：

1. 把 MOD 文件夹放到游戏根目录（和 `Terminus.exe` 同级）。
2. 开发时安装：`uv run --no-project --python 3.12 install.py`（`--status` 查看、`--uninstall` 卸载）。改完 `<MOD>.py` 重启游戏即生效。
3. 改 `build_release.py` 里的 `VERSION`，生成发布内容：`uv run --no-project --python 3.12 build_release.py`，输出
   - `<游戏目录>\<MOD>_workshop\`：创意工坊上传目录（目录里的 `workshop_item.json` 记录条目编号，保留它才会更新同一个条目）
   - `<游戏目录>\<MOD>_release\<名字>_v<版本>.zip`
4. `release_files` 里的占位符在构建时填入：`__VERSION__`、`__ORIG_SHA256__`、`__ITEM_ID__`、`__MOD__`、`__MOD_TITLE__`、`__DONE_MSG__`。

5. 上传到创意工坊（`tools` 文件夹也放在游戏根目录，Steam 运行中）：

   ```
   uv run --no-project --python 3.12 python -I ..\tools\workshop_upload.py <MOD> --dry-run      # 只检查
   uv run --no-project --python 3.12 python -I ..\tools\workshop_upload.py <MOD> "更新说明"
   ```

   [`tools/workshop_upload.py`](tools/workshop_upload.py) 直接从 `Terminus.exe` 里取游戏自带的 steamworks 绑定，上传 `<MOD>_workshop\`，
   页面描述取自 `build_release.steam_description()`；标题、标签、可见性保持不变。上传前会检查上传目录的版本和 `VERSION` 一致。
   汉化合集的 [`lemon_mods_zh/publish.py`](lemon_mods_zh/publish.py) 也用它。

预编译的 `payload\linecache.pyc` 内嵌了游戏原始的 `linecache.pyc`，玩家端安装器会用 SHA256 校验游戏里的原文件，游戏升级内置 Python 后需要重新运行 `build_release.py` 出新包。

不要用游戏自带的 `_internal\workshop_uploader\WorkshopUploader.exe`，它会用 `manifest.description` 覆盖页面上的长描述。
刚上传后页面会显示「隐藏」，那是 Steam 在审核；审核期间再提交更新会在提交时报 `ACCESS_DENIED`，等审核结束再传即可。

适配版本：游戏 1.3.2.12（Python 3.12）。非官方 MOD，与开发商无关。
