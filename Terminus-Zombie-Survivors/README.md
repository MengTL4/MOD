# Terminus: Zombie Survivors

## AI 托管主角（ai_takeover）

游戏里队友有自动化 AI，主角却没有。这个 MOD 在动作面板加了一个机器人按钮，让游戏自带的队友 AI 接管主角：

| 操作 | 效果 |
|---|---|
| 左键按钮 / `F6` | AI 托管本回合，行动完自动结束回合 |
| `Shift+F6` | 开启持续托管（每回合自动托管） |
| 持续托管中点按钮 / `F6` | 停止 |
| 右键按钮 | 菜单：托管本回合 / 持续托管 / AI 行为设置 |

AI 的逻辑和队友完全一样：用药 → 安全时睡觉 → 攻击僵尸/敌对 NPC → 修车 → 清障碍 → 搜刮捡物 → 继续制作/升级 → 读书、听音乐 → 缝补、修理 → 种田 → 按需吃喝。不会换地图、开车或交易。
「AI 行为设置」是游戏原生的队友自动化设置面板，只对主角生效。持续托管时 HP 低于 35% 自动停止。

### 玩家安装

- **Steam 创意工坊**：[订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3821926212)后退出游戏，打开
  `<Steam 库>\steamapps\workshop\content\1534980\3821926212`，双击 `install.bat`。
- **手动**：用 `build_release.py` 生成的 zip，解压到任意位置，双击 `install.bat`。

不需要 Python。安装器会自动找到游戏目录、先校验游戏版本再改动；卸载双击 `uninstall.bat`。
游戏或 MOD 更新后重新运行一次 `install.bat`。详见 [使用说明.txt](ai_takeover/release_files/使用说明.txt) / [README_EN.txt](ai_takeover/release_files/README_EN.txt)。

### 原理

游戏是 PyInstaller 打包的 Python 3.12 程序，官方创意工坊模组只支持 JSON/图片，不执行代码。

1. 安装器把 `_internal\base_library.zip` 里的标准库 `linecache.pyc` 换成「原样执行原字节码 + 安装一个导入钩子」的版本（原文件备份到 `<游戏目录>\ai_takeover\backup`）。
2. 游戏导入 `ui.panels.actionpanel` / `scenes.main` 时，钩子加载 `<游戏目录>\ai_takeover\ai_takeover.py`，给动作面板加按钮、给主场景加热键。
3. 点击按钮后在回合线程里复用 `actions/compaction.py` 的队友 AI 子任务控制主角，再调用游戏原本的结束回合流程。

出错时不会让游戏崩溃，错误写在 `<游戏目录>\ai_takeover\ai_takeover.log`。

### 目录

```
ai_takeover/
├── ai_takeover.py        MOD 本体（按钮、热键、AI 驱动），开头几项可配置
├── install.py            开发用安装/卸载（需 Python 3.12），直接在游戏里改 base_library.zip
├── install.bat           调用 install.py
├── uninstall.bat
├── build_release.py      生成免 Python 的发布内容（创意工坊上传目录 + zip）
└── release_files/        发布素材
    ├── install.ps1       玩家用安装器（PowerShell，自动找游戏目录、校验、备份、复制）
    ├── install.bat / uninstall.bat
    ├── manifest.json     创意工坊 manifest（描述 ≤1000 字，会显示在游戏模组管理器）
    ├── steam_description.txt  创意工坊页面描述（BBCode）
    ├── preview.png
    └── 使用说明.txt / README_EN.txt
```

### 开发与发布

需要 Python 3.12（与游戏内置版本一致，字节码才兼容），例如 `uv`：

1. 把 `ai_takeover` 文件夹放到游戏根目录（和 `Terminus.exe` 同级）。
2. 开发时安装：`uv run --no-project --python 3.12 install.py`（`--status` 查看、`--uninstall` 卸载）。改完 `ai_takeover.py` 重启游戏即生效。
3. 生成发布内容：`uv run --no-project --python 3.12 build_release.py`，输出
   - `<游戏目录>\ai_takeover_workshop\`：创意工坊上传目录（可用游戏自带的 `_internal\workshop_uploader\WorkshopUploader.exe` 上传；目录里的 `workshop_item.json` 记录条目编号，保留它才会更新同一个条目）
   - `<游戏目录>\ai_takeover_release\TerminusAITakeover_v<版本>.zip`

预编译的 `payload\linecache.pyc` 内嵌了游戏原始的 `linecache.pyc`，玩家端安装器会用 SHA256 校验游戏里的原文件，游戏升级内置 Python 后需要重新运行 `build_release.py` 出新包。

适配版本：游戏 1.3.2.12（Python 3.12）。非官方 MOD，与开发商无关。
