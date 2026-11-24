# -*- coding: utf-8 -*-
"""LONESTAR 战前摆放/数据模型统计（只读 data/ 与 decompiled/，不改任何源码）。

用法：
  python scripts/analysis/shipunit_stats.py            # 打印 markdown 风格统计
  python scripts/analysis/shipunit_stats.py --json     # 额外输出机器可读 json 到 stdout 末尾

数据来源：
  data/export/csv/*.txt                     游戏数据库 CSV（首行表头，第二行类型）
  data/export/scriptableassets/shipassets/  PlayerShipAsset：船体格 coors + 初始驻留单位
  data/export/scriptableassets/enemyshipassets/ EnemyShipAsset：敌方布局 + AI 序列

注意：CSV 里存在整行空白（导出分隔残留），bool 列空白语义见 DataShipUnitManager：
      bool.TryParse 失败 => InGame = true。即空白 == true，'FALSE' == false。
"""
import json
import os
import re
import sys
from collections import Counter

import pandas as pd

ROOT = r"F:\LonestarSolver"
CSV = os.path.join(ROOT, "data", "export", "csv")
SHIP_ASSETS = os.path.join(ROOT, "data", "export", "scriptableassets", "shipassets")
ENEMY_ASSETS = os.path.join(ROOT, "data", "export", "scriptableassets", "enemyshipassets")


def load(name):
    """读 CSV：跳过第 2 行类型定义，丢弃全空行。"""
    df = pd.read_csv(os.path.join(CSV, name + ".txt"), header=0,
                     encoding="utf-8-sig", dtype=str,
                     keep_default_na=False, skiprows=[1])
    df = df[df["ID"].astype(str).str.strip() != ""].copy()
    return df


def in_game(v):
    # 空白表示 true（bool.TryParse 失败时取默认 true），仅显式 FALSE 为 false
    return str(v).strip().upper() != "FALSE"


def ints(v):
    return [int(x) for x in str(v).split(";") if str(x).strip() != ""]


def short(path):
    return re.sub(r"^.*/", "", str(path))


def md_table(header, rows):
    out = ["| " + " | ".join(header) + " |",
           "|" + "|".join(["---"] * len(header)) + "|"]
    out += ["| " + " | ".join(str(c) for c in r) + " |" for r in rows]
    return "\n".join(out)


def stats_shipunit():
    df = load("shipunit")
    print("\n## 1. shipunit.csv（单位 / 模块）\n")
    lv1 = df[df["Lv"] == "1"]
    ids = df["ID"].nunique()
    print(md_table(["项", "值"], [
        ["数据行数（含各等级）", len(df)],
        ["唯一单位 ID 数", ids],
        ["Lv=1 的模板数（= 可获得的独立单位定义为 1 个）", len(lv1)],
        ["每个 ID 的等级行数分布", dict(Counter(df.groupby("ID").size()))],
        ["可升级（>=2 个等级）的单位数", (df.groupby("ID").size() > 1).sum()],
        ["最高等级", df["Lv"].astype(int).max()],
    ]))

    print("\n### 稀有度（Rare，0=白 1=蓝 2=紫）\n")
    rare = Counter(df["Rare"])
    for k in sorted(rare):
        print(f"- Rare={k}: {rare[k]} 行 ({rare[k]/len(df)*100:.1f}%)")

    print("\n### 类型（Type，1=Cannon 炮/可装能量，2=Support 支援/不可装能量）\n")
    for k, v in sorted(Counter(df["Type"]).items()):
        print(f"- Type={k}: {v} 行")

    print("\n### 能量槽（PowerSlot：每格一个颜色需求 1=白 2=蓝 3=紫）\n")
    slots = df["PowerSlot"].map(lambda v: ints(v))
    print(md_table(["槽数", "行数", "占比"], [
        [n, c, f"{c/len(df)*100:.1f}%"] for n, c in sorted(Counter(slots.map(len)).items())
    ]))
    combos = Counter("".join(str(x) for x in sorted(s)) if s else "(无槽)" for s in slots)
    print("\n槽颜色组合 Top（排序后拼接，1=白 2=蓝 3=紫）：\n")
    rows = [[k, v] for k, v in combos.most_common(12)]
    print(md_table(["颜色组合", "行数"], rows))

    print("\n### 装备占用 EquiptLimit（LoadLimit 消耗）\n")
    print(md_table(["EquiptLimit", "行数"], [[k, v] for k, v in sorted(Counter(df["EquiptLimit"]).items())]))

    print("\n### 出处 GainType / 解锁 / InGame\n")
    print(md_table(["GainType", "行数", "含义（见 WantedProcess）"], [
        ["0", Counter(df["GainType"])["0"], "可出现在初始 unitPool（商店/奖励）"],
        ["1", Counter(df["GainType"])["1"], "也可出现在 unitPool"],
        ["2", Counter(df["GainType"])["2"], "非池（事件/特殊）"],
        ["3", Counter(df["GainType"])["3"], "非池"],
        ["4", Counter(df["GainType"])["4"], "起始自带（如 11001 基础核心）"],
        ["5", Counter(df["GainType"])["5"], "敌方专用/不可获得"],
    ]))
    ing = sum(1 for v in df["InGame"] if in_game(v))
    print(f"\n- InGame==true 行数: {ing}/{len(df)}（空白视为 true）")
    print(f"- 仅 Lv=1 且 InGame 且 GainType in (0,1) 的“可获得模板”数: "
          f"{len(lv1[(lv1['InGame'].map(in_game)) & (lv1['GainType'].isin(['0', '1']))])}")

    print("\n### 技能（SkillPath 去目录名）Top 20（全表）\n")
    sk = Counter(short(s) for s in df["SkillPath"])
    print(md_table(["Skill", "行数"], [[k, v] for k, v in sk.most_common(20)]))
    print(f"\n- 不同技能实现数（含空/Skill_Null）: {df['SkillPath'].nunique()}")
    print(f"- 无技能（空或 Skill_Null）行数: "
          f"{sum(1 for s in df['SkillPath'] if short(s) in ('', 'Skill_Null'))}")

    obtain = lv1[(lv1["InGame"].map(in_game)) & (lv1["GainType"].isin(["0", "1"]))]
    print("\n### 可获得单位（Lv1, InGame, GainType 0/1）技能 Top 15\n")
    print(md_table(["Skill", "行数"],
                   [[k, v] for k, v in Counter(short(s) for s in obtain["SkillPath"]).most_common(15)]))
    print(f"\n可获得模板的稀有度分布: {dict(Counter(obtain['Rare']))}")
    print(f"可获得模板的槽数分布: {dict(sorted(Counter(obtain['PowerSlot'].map(lambda v: len(ints(v)))).items()))}")
    print(f"可获得模板的类型分布: {dict(Counter(obtain['Type']))}")

    print("\n### Propertys 附加键 / Genera（船体流派）\n")
    keys = Counter()
    for v in df["Propertys"]:
        for part in str(v).split(";"):
            if part.strip():
                keys[part.split(":")[0]] += 1
    print(f"- Propertys 键: {dict(keys.most_common())}"
          f"（PA=额外威力 PowerAdded，其余为技能运行时动态值键）")
    print(f"- Genera（1-3 个船 ID，单位只出现在这些船的池里；-1 表示通用）: "
          f"{dict(Counter(g for v in df['Genera'] for g in ints(v)).most_common())}")
    return df


def stats_ship():
    df = load("ship")
    print("\n## 2. ship.csv（玩家船，共 %d 行；InGame 为 TRUE 的才是可玩船）\n" % len(df))
    rows = []
    for _, r in df.iterrows():
        pw = ints(r["PowerCoreWeight"])
        cw = ints(r["PowerColorWeight"])
        rows.append([
            r["ID"], short(r["Name"]), r["InGame"], r["HP"], r["Move"], short(r["ShipSkill"]),
            short(r["ShipDataPath"]), "".join(map(str, pw)), "".join(map(str, cw)),
        ])
    print(md_table(["ID", "Name key", "InGame", "HP", "Move", "ShipSkill", "Asset", "PowerCoreWeight", "PowerColorWeight"], rows))
    print("\n说明：PowerCoreWeight=[1,1,1,1,1] 经 PlayerShipData.InitCore 展开为「威力 1..5 各 1 张」的抽卡袋；")
    print("PowerColorWeight=[3,2,1] 展开为「白 3 / 蓝 2 / 紫 1」的颜色袋。二者每回合抽 3 张、首回合 4 张、手牌上限 10。")


def asset_cells(path):
    with open(path, encoding="utf-8") as f:
        obj = json.load(f)
    fld = obj.get("fields", obj)
    cells = fld.get("shipCellAssets") or []
    return fld, cells


def stats_player_assets():
    print("\n## 3. PlayerShipAsset（真实船体格；data/export/scriptableassets/shipassets）\n")
    rows = []
    for name in sorted(os.listdir(SHIP_ASSETS)):
        if not name.endswith(".asset.json"):
            continue
        fld, cells = asset_cells(os.path.join(SHIP_ASSETS, name))
        size = fld.get("Size") or [0, 0]
        coors = fld.get("coors") or []
        none_cells = sum(1 for c in cells if int(c.get("unitID", 0)) == -1)
        pre = [c for c in cells if int(c.get("unitID", 0)) > 0]
        rows.append([
            name.replace(".asset.json", ""), f"{size[0]}x{size[1]}", len(coors), len(cells),
            none_cells, len(coors) - len(cells), len(pre),
            ",".join(f"{c['unitID']}@Lv{c.get('unitLv',1)}" for c in pre[:6]) + ("..." if len(pre) > 6 else ""),
        ])
    print(md_table(["Asset", "Size", "coors(可用格)", "gridArray", "不可用格(-1)",
                    "尺寸空洞(不在coors)", "初始驻留单位", "初始单位(截断)"], rows))
    print("\n说明：coors 之外、或 unitID==-1 的格子 ShipCellStatus=None（不能放单位）；")
    print("unitID>0 表示该船默认已有单位（如 JX_001 6 个 lv2 单位），战前布局从这份默认布局改起。")


def stats_enemy():
    df = load("enemyship")
    print("\n## 4. enemyship.csv（敌方船）\n")
    ing = df[df["InGame"].map(in_game)]
    print(md_table(["项", "值"], [
        ["总行数", len(df)],
        ["InGame==true", len(ing)],
        ["EnemyType 分布（0=普通 1=精英 2=Boss）", dict(Counter(ing["EnemyType"]))],
        ["EnemyPhaseType 分布（决定进哪个阶段的随机池）", dict(sorted(Counter(ing["EnemyPhaseType"]).items(), key=lambda x: int(x[0] or 0)))],
        ["UnlockLV 分布", dict(sorted(Counter(ing["UnlockLV"]).items(), key=lambda x: int(x[0] or 0)))],
        ["HP 最小值/中位/最大", (ing["HP"].astype(int).min(), int(ing["HP"].astype(int).median()), ing["HP"].astype(int).max())],
        ["KeyUnitIDs 非空的敌人", int(sum(1 for v in ing["KeyUnitIDs"] if str(v).strip()))],
        ["OnlyPro 非空（限定某艘玩家船）", int(sum(1 for v in ing["OnlyPro"] if str(v).strip()))],
    ]))
    names = sorted(os.listdir(ENEMY_ASSETS))
    rows = []
    for name in names[:8]:
        fld, cells = asset_cells(os.path.join(ENEMY_ASSETS, name))
        size = fld.get("Size") or [0, 0]
        units = sum(1 for c in cells if int(c.get("unitID", 0)) > 0)
        rows.append([name.replace(".asset.json", ""), f"{size[0]}x{size[1]}", len(cells), units,
                     fld.get("controller", "")])
    print("\n敌方布局来自 EnemyShipAsset（每格 unitID/Lv + 每格 AIPowers/AIOrder + moves/notices + controller）：\n")
    print(md_table(["Asset", "Size", "格数", "单位数", "AI controller"], rows))
    print(f"\n（enemyshipassets 共 {len(names)} 个资产文件；HP/BreakCount 有 _1 变体对应 run 内的难度强化）")


def stats_other():
    print("\n## 5. treasure / pilot / talent\n")
    tr = load("treasure")
    print(md_table(["treasure 项", "值"], [
        ["总行数", len(tr)],
        ["InGame==true", int(sum(1 for v in tr["InGame"] if in_game(v)))],
        ["稀有度分布", dict(sorted(Counter(tr["Rare"]).items()))],
        ["BattleRelate=true（直接影响战斗）", int(sum(1 for v in tr["BattleRelate"] if in_game(v)))],
        ["有 SkillPath（被动技能）", int(sum(1 for v in tr["SkillPath"] if str(v).strip()))],
        ["GainType 分布", dict(Counter(tr["GainType"]))],
    ]))
    pl = load("pilot")
    ta = load("talent")
    print(md_table(["pilot/talent 项", "值"], [
        ["pilot 行数", len(pl)],
        ["pilot 带 Talent 的", int(sum(1 for v in pl["Talent"] if str(v).strip()))],
        ["RandomSlotNum 分布", dict(Counter(pl["RandomSlotNum"]))],
        ["talent 行数", len(ta)],
        ["talent Type 分布", dict(Counter(ta["Type"]))],
        ["talent InProcess==true", int(sum(1 for v in ta["InProcess"] if in_game(v)))],
    ]))
    print("\n说明：talent ID 10001 起是 TA_* 被动技能（TalentSkill 钩子）；pilot 的 Talent 是固定天赋，"
          "RandomSlotNum > 0 时开局还会随机抽 0-2 个随机天赋（Define.randomTalentLimit=[5,10]）。")


def stats_search_space():
    print("\n## 6. 摆放搜索空间（单场战斗、固定船体与单位集合）\n")
    rows = []
    for n in (9, 12, 15):
        for k in range(3, 7):
            p = 1
            for i in range(k):
                p *= (n - i)
            if n == 9 and k == 9:
                p = 362880
            rows.append([f"{n} 格船体", k, f"{p:,}"])
    print(md_table(["船体格", "摆放单位数 k", "有序单射数 P(n,k)（仅位置）"], rows))
    print(f"\n- 3x3（9 格）k=4: {9*8*7*6:,}；k=5: {9*8*7*6*5:,}；k=6: {9*8*7*6*5*4:,}")
    print("- 每格还要叠加：单位等级（1-3）× 槽改造 slotRefitTypes（0-6 槽的可改造组合）× 颜色/威力核心配置，"
          "决策空间再乘 1-2 个数量级；但**同一行的单位集合可交换**（行内位置对行总威力等价），可按行聚合先降维。")
    print("- 另外战前布局并非唯一决策：开局手牌由 PowerCore/PowerColorCore 洗袋决定（见 ship.csv），"
          "真实求解应按「种子 + 布局」评估期望。")


def main():
    print("# LONESTAR shipunit / ship / enemyship 数据统计（由 scripts/analysis/shipunit_stats.py 生成）")
    stats_shipunit()
    stats_ship()
    stats_player_assets()
    stats_enemy()
    stats_other()
    stats_search_space()


if __name__ == "__main__":
    main()
