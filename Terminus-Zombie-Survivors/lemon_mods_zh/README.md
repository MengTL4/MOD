# LemonCitrine 全系列 MOD 简体中文汉化

作者 LemonCitrine 的 [全部 MOD](https://steamcommunity.com/workshop/filedetails/?id=3760852788) 的简体中文翻译合集。
纯数据 MOD，走游戏官方的创意工坊模组系统，只含翻译文本。

- 创意工坊：[3826727240](https://steamcommunity.com/sharedfiles/filedetails/?id=3826727240)
- 用法：订阅本合集 + 想玩的原版 MOD，游戏语言选简体中文。装了哪些原版 MOD 就汉化哪些，没装的不受影响。

## 覆盖范围

| 文件 | 原版 MOD | 条目 |
|---|---|---|
| `cheat_traits.json` | [Cheat Traits](https://steamcommunity.com/sharedfiles/filedetails/?id=3753067305) | 24 个属性 |
| `challenge_traits.json` | [Challenge Traits](https://steamcommunity.com/sharedfiles/filedetails/?id=3753289073) | 19 个属性 |
| `infinite_perks.json` | [Infinite Perks Pack](https://steamcommunity.com/sharedfiles/filedetails/?id=3757192262) | 6 个技能 |
| `unique_perks.json` | [Unique Perks for Everyone](https://steamcommunity.com/sharedfiles/filedetails/?id=3789397954) | 13 个技能 |
| `unique_perks_lvl100.json` | [Unique Perks for Everyone- Level 100!](https://steamcommunity.com/sharedfiles/filedetails/?id=3811125649) | 9 个技能 |
| `swords.json` | [Swords](https://steamcommunity.com/sharedfiles/filedetails/?id=3762726963) | 长剑 |
| `ammo_types.json` | [Ammo Types](https://steamcommunity.com/sharedfiles/filedetails/?id=3763021505) | 2 种弹药 + 3 把枪的说明 |

[Lemon's Edits](https://steamcommunity.com/sharedfiles/filedetails/?id=3757710534) 是以上内容的整合包，文本和数据与单独的 MOD 完全相同，同样适用。
作者其余 MOD（属性上限、载具、配方、雨水收集器、帐篷、石头）只改数值或用原版物品，没有新文本。

## 翻译约定

- 术语以游戏官方简中为准：trait = 属性，trait points = 属性点数，unique perk = 固有技能，Satiety = 饱腹感 等。
- 原版职业固有技能的移植版写成「官方技能名（职业）」，如「活力源泉（运动员）」；说明沿用原版格式，数值用 `<color=GREEN>`。
- `{num}` 等占位符必须原样保留。
- 说明文字按 MOD 的实际数据和游戏代码校对，原版英文与实际效果不符时以实际为准。游戏会对部分数值封顶，例如 `companion_count` 最多 10，`search_extra_items` 和 `butcher.bonus` 最多 20。
- **不覆盖原版文本**：语言补丁是全局生效的（`Language.data[语言].update(...)`，跟哪个 MOD 无关）。Ammo Types 改写了原版的 `shotgun_desc` / `assault_rifle_desc`，合集如果翻译这两条，没装 Ammo Types 的玩家也会看到「使用霰弹」。所以这两条故意不翻。

## 目录

```
lemon_mods_zh/
├── workshop/                 创意工坊上传目录（只放这里的东西会被上传）
│   ├── manifest.json
│   ├── preview.png
│   └── lang/Simplified Chinese/*.json   每个原版 MOD 一个文件
├── steam_description.txt     创意工坊页面描述（BBCode）
└── publish.py                上传/更新条目
```

## 发布

文件夹放在游戏根目录（和 `Terminus.exe` 同级），Steam 运行中，用 Python 3.12：

```
uv run --no-project --python 3.12 python -I publish.py --dry-run          # 只检查能否连上 Steam
uv run --no-project --python 3.12 python -I publish.py "v2.0.1 更新说明"
```

脚本直接从 `Terminus.exe` 里取出游戏自带的 steamworks 绑定来上传，会更新标题、描述、标签、内容和预览图，并按 `DEPENDENCIES` 同步「必需物品」。
不要用游戏自带的 WorkshopUploader，它会用 `manifest.description` 覆盖页面上的长描述。

作者新出 MOD 时：在 `workshop/lang/Simplified Chinese/` 加一个翻译文件，在 `manifest.json` 的 `language_patches` 里登记，然后发布。

所有 MOD 内容归原作者 LemonCitrine 所有。非官方汉化，与开发商无关。
