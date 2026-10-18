using System;
using BepInEx.Configuration;
using UnityEngine;

namespace DICEOMANCERCheatMenu
{
    public enum MoneyMode
    {
        Normal,
        NoLoss,
        Gain
    }

    // 所有配置项都会保存到 BepInEx/config/me.mengtl.plugin.cheatmenu.cfg，重启游戏后保持不变
    internal static class CheatSettings
    {
        public static readonly Rect DefaultWindowRect = new Rect(50f, 50f, 620f, 540f);

        private static ConfigFile config;

        public static ConfigEntry<KeyCode> Hotkey;
        public static ConfigEntry<float> UiScale;
        public static ConfigEntry<bool> BlockGameInput;
        private static ConfigEntry<float> windowX;
        private static ConfigEntry<float> windowY;
        private static ConfigEntry<float> windowWidth;
        private static ConfigEntry<float> windowHeight;

        public static ConfigEntry<bool> InfiniteMana;
        public static ConfigEntry<MoneyMode> Money;
        public static ConfigEntry<bool> Invincible;
        public static ConfigEntry<bool> DamageMultiplierEnabled;
        public static ConfigEntry<int> DamageMultiplier;
        public static ConfigEntry<bool> AnyManaColor;
        public static ConfigEntry<bool> AutoUpgradeCards;
        public static ConfigEntry<bool> UnlockCardCollection;
        public static ConfigEntry<bool> UnlockRelicCollection;

        public static void Bind(ConfigFile file)
        {
            config = file;

            // 沿用旧版本的配置键，升级后快捷键设置不会丢失
            Hotkey = file.Bind("config", "hotkey", KeyCode.F9, "打开/关闭修改器窗口的快捷键");

            UiScale = file.Bind("UI", "Scale", 1f,
                new ConfigDescription("界面缩放比例", new AcceptableValueRange<float>(0.5f, 3f)));
            BlockGameInput = file.Bind("UI", "BlockGameInput", true, "鼠标位于修改器窗口上时阻止点击穿透到游戏");
            windowX = file.Bind("UI", "WindowX", DefaultWindowRect.x, "窗口位置 X");
            windowY = file.Bind("UI", "WindowY", DefaultWindowRect.y, "窗口位置 Y");
            windowWidth = file.Bind("UI", "WindowWidth", DefaultWindowRect.width, "窗口宽度");
            windowHeight = file.Bind("UI", "WindowHeight", DefaultWindowRect.height, "窗口高度");

            InfiniteMana = file.Bind("Cheats", "InfiniteMana", false, "魔力不减");
            Money = file.Bind("Cheats", "MoneyMode", MoneyMode.Normal,
                "金币模式：Normal 正常 / NoLoss 金币不减 / Gain 花费金币反而增加");
            Invincible = file.Bind("Cheats", "Invincible", false, "生命不减");
            DamageMultiplierEnabled = file.Bind("Cheats", "DamageMultiplierEnabled", false, "对敌人的伤害倍率开关");
            DamageMultiplier = file.Bind("Cheats", "DamageMultiplier", 2,
                new ConfigDescription("对敌人的伤害倍率", new AcceptableValueRange<int>(1, 10)));
            AnyManaColor = file.Bind("Cheats", "AnyManaColor", false, "卡牌能量种类无要求");
            AutoUpgradeCards = file.Bind("Cheats", "AutoUpgradeCards", false, "卡牌自动强化");
            UnlockCardCollection = file.Bind("Cheats", "UnlockCardCollection", true, "卡牌图鉴全解锁");
            UnlockRelicCollection = file.Bind("Cheats", "UnlockRelicCollection", true, "遗物图鉴全解锁");
        }

        public static Rect WindowRect
        {
            get { return new Rect(windowX.Value, windowY.Value, windowWidth.Value, windowHeight.Value); }
            set
            {
                if (value == WindowRect) return;
                Batch(() =>
                {
                    windowX.Value = value.x;
                    windowY.Value = value.y;
                    windowWidth.Value = value.width;
                    windowHeight.Value = value.height;
                });
            }
        }

        public static void DisableAllCheats()
        {
            Batch(() =>
            {
                InfiniteMana.Value = false;
                Money.Value = MoneyMode.Normal;
                Invincible.Value = false;
                DamageMultiplierEnabled.Value = false;
                AnyManaColor.Value = false;
                AutoUpgradeCards.Value = false;
                UnlockCardCollection.Value = false;
                UnlockRelicCollection.Value = false;
            });
        }

        // 批量修改时只写一次配置文件
        private static void Batch(Action change)
        {
            bool saveOnSet = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;
            try
            {
                change();
            }
            finally
            {
                config.SaveOnConfigSet = saveOnSet;
            }
            config.Save();
        }
    }
}
