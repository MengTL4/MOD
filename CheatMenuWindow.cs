using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace DICEOMANCERCheatMenu
{
    // 修改器主窗口。
    // 所有会改变游戏状态的操作都先放进队列，在下一次 Update 中执行，避免在 OnGUI 中途修改列表导致布局错乱。
    internal sealed class CheatMenuWindow
    {
        private enum Tab
        {
            Cheats,
            Deck,
            CardCollection,
            RelicCollection,
            Settings
        }

        private static readonly string[] TabNames = { "常用功能", "当前卡组", "卡牌图鉴", "遗物图鉴", "设置" };
        private static readonly string[] MoneyModeNames = { "正常", "金币不减", "花费变获得" };
        private static readonly object AddAllRelicsKey = new object();
        private static readonly object DisableAllKey = new object();
        private static readonly Color SuccessColor = new Color(0.45f, 0.86f, 0.55f);
        private static readonly Color ErrorColor = new Color(1f, 0.47f, 0.47f);
        private static readonly Color InfoColor = new Color(0.75f, 0.82f, 0.95f);

        private const float TitleHeight = 32f;
        private const float ContentPadding = 10f;
        private const float MinWidth = 480f;
        private const float MinHeight = 360f;
        private const float GripSize = 14f;
        private const float DeckRefreshInterval = 0.5f;
        private const float StatusDuration = 4f;
        private const float ConfirmTimeout = 3f;
        private const float ScaleStep = 0.1f;
        private const int ResizeControlHint = 0x43484D52;

        private readonly int windowId = DICEOMANCERCheatMenu.PluginGuid.GetHashCode();
        private readonly string title = "骰子浪游者内置修改器  v" + DICEOMANCERCheatMenu.PluginVersion;
        private readonly Queue<Func<string>> pendingActions = new Queue<Func<string>>();
        private readonly List<UnityEngine.EventSystems.EventSystem> blockedEventSystems =
            new List<UnityEngine.EventSystems.EventSystem>();
        private readonly FilteredListView deckList = new FilteredListView();
        private readonly FilteredListView cardList = new FilteredListView();
        private readonly FilteredListView relicList = new FilteredListView();
        private readonly GUI.WindowFunction drawWindow;
        private readonly Action<int, object, string> drawDeckRow;
        private readonly Action<int, object, string> drawCardRow;
        private readonly Action<int, object, string> drawRelicRow;
        private readonly Func<object, string> deckCardName = CheatActions.GetDeckCardName;
        private readonly Func<object, string> collectionCardName = CheatActions.GetCollectionCardName;
        private readonly Func<object, string> relicName = CheatActions.GetRelicName;

        private MenuStyles styles;
        private Rect windowRect;
        private Rect? pendingWindowRect;
        private Vector2 resizeStartMouse;
        private Vector2 resizeStartSize;
        private bool visible;
        private Tab tab;
        private Tab? pendingTab;
        private bool waitingForHotkey;
        private bool inRun;
        private Vector2 cheatsScroll;
        private Vector2 settingsScroll;
        private float nextDeckRefresh;
        private int seenCardVersion = -1;
        private int seenRelicVersion = -1;
        private object confirmKey;
        private float confirmTime;
        private string statusText = string.Empty;
        private Color statusColor = InfoColor;
        private float statusTime = float.NegativeInfinity;
        private KeyCode hintKey = KeyCode.None;
        private string hintText = string.Empty;

        public CheatMenuWindow()
        {
            windowRect = CheatSettings.WindowRect;
            drawWindow = DrawWindow;
            drawDeckRow = DrawDeckRow;
            drawCardRow = DrawCardRow;
            drawRelicRow = DrawRelicRow;
        }

        public void Update()
        {
            if (!waitingForHotkey && Input.GetKeyDown(CheatSettings.Hotkey.Value)) SetVisible(!visible);

            while (pendingActions.Count > 0) RunAction(pendingActions.Dequeue());

            if (!visible) return;
            RefreshData();
            UpdateInputBlocking();
        }

        public void OnGUI()
        {
            if (!visible) return;
            if (styles == null || !styles.IsValid)
            {
                if (styles != null) styles.Destroy();
                styles = new MenuStyles();
            }

            Event e = Event.current;
            if (waitingForHotkey && e.type == EventType.KeyDown && e.keyCode != KeyCode.None)
            {
                CaptureHotkey(e.keyCode);
                e.Use();
            }

            float scale = CheatSettings.UiScale.Value;
            Matrix4x4 previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            if (pendingWindowRect.HasValue)
            {
                windowRect = pendingWindowRect.Value;
                pendingWindowRect = null;
            }
            windowRect = ClampToScreen(GUI.Window(windowId, windowRect, drawWindow, GUIContent.none, styles.Window), scale);

            GUI.matrix = previousMatrix;
        }

        public void Shutdown()
        {
            ReleaseEventSystems();
            CheatSettings.WindowRect = windowRect;
            if (styles != null) styles.Destroy();
        }

        private void SetVisible(bool value)
        {
            if (visible == value) return;
            visible = value;
            waitingForHotkey = false;
            confirmKey = null;
            if (visible)
            {
                nextDeckRefresh = 0f;
            }
            else
            {
                ReleaseEventSystems();
                CheatSettings.WindowRect = windowRect;
            }
        }

        private void RunAction(Func<string> action)
        {
            try
            {
                ShowStatus(action(), SuccessColor);
            }
            catch (InvalidOperationException e)
            {
                ShowStatus(e.Message, ErrorColor);
            }
            catch (Exception e)
            {
                DICEOMANCERCheatMenu.Log.LogError(e);
                ShowStatus("操作失败：" + e.Message, ErrorColor);
            }
            nextDeckRefresh = 0f;
        }

        private void ShowStatus(string text, Color color)
        {
            statusText = text ?? string.Empty;
            statusColor = color;
            statusTime = Time.unscaledTime;
        }

        // 只刷新当前页签需要的数据，卡组每 0.5 秒同步一次，而不是每次 OnGUI 都去读取
        private void RefreshData()
        {
            switch (tab)
            {
                case Tab.Deck:
                    if (Time.unscaledTime < nextDeckRefresh) break;
                    nextDeckRefresh = Time.unscaledTime + DeckRefreshInterval;
                    deckList.SetItems(CheatActions.GetDeck(out inRun), deckCardName);
                    break;
                case Tab.CardCollection:
                    if (seenCardVersion == CheatActions.CardCollectionVersion) break;
                    seenCardVersion = CheatActions.CardCollectionVersion;
                    cardList.SetItems(CheatActions.CardCollection, collectionCardName);
                    break;
                case Tab.RelicCollection:
                    if (seenRelicVersion == CheatActions.RelicCollectionVersion) break;
                    seenRelicVersion = CheatActions.RelicCollectionVersion;
                    relicList.SetItems(CheatActions.RelicCollection, relicName);
                    break;
            }
        }

        // 鼠标悬停在窗口上时临时禁用游戏的 EventSystem，防止点击穿透到窗口下方的游戏界面
        private void UpdateInputBlocking()
        {
            if (!CheatSettings.BlockGameInput.Value)
            {
                ReleaseEventSystems();
                return;
            }

            float scale = CheatSettings.UiScale.Value;
            Vector3 mouse = Input.mousePosition;
            var guiMouse = new Vector2(mouse.x / scale, (Screen.height - mouse.y) / scale);
            if (!windowRect.Contains(guiMouse) && GUIUtility.hotControl == 0)
            {
                ReleaseEventSystems();
                return;
            }

            UnityEngine.EventSystems.EventSystem current = UnityEngine.EventSystems.EventSystem.current;
            if (current != null && current.enabled)
            {
                current.enabled = false;
                blockedEventSystems.Add(current);
            }
        }

        private void ReleaseEventSystems()
        {
            foreach (UnityEngine.EventSystems.EventSystem eventSystem in blockedEventSystems)
            {
                if (eventSystem != null) eventSystem.enabled = true;
            }
            blockedEventSystems.Clear();
        }

        private void CaptureHotkey(KeyCode key)
        {
            waitingForHotkey = false;
            if (key == KeyCode.Escape)
            {
                ShowStatus("已取消修改快捷键", InfoColor);
                return;
            }
            CheatSettings.Hotkey.Value = key;
            ShowStatus("快捷键已设置为 " + key, SuccessColor);
        }

        private static Rect ClampToScreen(Rect rect, float scale)
        {
            float screenWidth = Screen.width / scale;
            float screenHeight = Screen.height / scale;
            rect.width = Mathf.Clamp(rect.width, Mathf.Min(MinWidth, screenWidth), screenWidth);
            rect.height = Mathf.Clamp(rect.height, Mathf.Min(MinHeight, screenHeight), screenHeight);
            rect.x = Mathf.Clamp(rect.x, 0f, screenWidth - rect.width);
            rect.y = Mathf.Clamp(rect.y, 0f, screenHeight - rect.height);
            return rect;
        }

        private void DrawWindow(int id)
        {
            // 页签切换延迟到 Layout 事件生效，保证同一事件内控件结构不变
            if (Event.current.type == EventType.Layout && pendingTab.HasValue)
            {
                tab = pendingTab.Value;
                pendingTab = null;
                confirmKey = null;
                nextDeckRefresh = 0f;
                GUIUtility.keyboardControl = 0;
            }

            float width = windowRect.width;
            float height = windowRect.height;
            HandleResize(width, height);

            GUI.Label(new Rect(0f, 0f, width, TitleHeight), title, styles.Title);
            if (GUI.Button(new Rect(width - 34f, 5f, 28f, TitleHeight - 10f), "×", styles.CloseButton)) SetVisible(false);

            GUILayout.BeginArea(new Rect(ContentPadding, TitleHeight + 8f, width - ContentPadding * 2f,
                height - TitleHeight - 8f - ContentPadding));

            int selected = GUILayout.Toolbar((int)tab, TabNames, styles.Tab, GUILayout.Height(30f));
            if (selected != (int)tab) pendingTab = (Tab)selected;
            GUILayout.Space(8f);

            switch (tab)
            {
                case Tab.Cheats:
                    DrawCheatsTab();
                    break;
                case Tab.Deck:
                    DrawDeckTab();
                    break;
                case Tab.CardCollection:
                    DrawCardCollectionTab();
                    break;
                case Tab.RelicCollection:
                    DrawRelicCollectionTab();
                    break;
                case Tab.Settings:
                    DrawSettingsTab();
                    break;
            }

            GUILayout.Space(6f);
            DrawStatusBar();
            GUILayout.EndArea();

            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(new Rect(width - GripSize - 3f, height - GripSize - 3f, GripSize, GripSize), styles.Grip);
            }
            GUI.DragWindow(new Rect(0f, 0f, width - 40f, TitleHeight));
        }

        // 拖动窗口右下角调整大小
        private void HandleResize(float width, float height)
        {
            var grip = new Rect(width - GripSize - 6f, height - GripSize - 6f, GripSize + 6f, GripSize + 6f);
            int controlId = GUIUtility.GetControlID(ResizeControlHint, FocusType.Passive);
            Event e = Event.current;
            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && grip.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = controlId;
                        resizeStartMouse = e.mousePosition;
                        resizeStartSize = new Vector2(width, height);
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlId)
                    {
                        Vector2 size = resizeStartSize + (e.mousePosition - resizeStartMouse);
                        pendingWindowRect = new Rect(windowRect.x, windowRect.y,
                            Mathf.Max(MinWidth, size.x), Mathf.Max(MinHeight, size.y));
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
            }
        }

        private void DrawCheatsTab()
        {
            cheatsScroll = GUILayout.BeginScrollView(cheatsScroll, GUILayout.ExpandHeight(true));

            GUILayout.Label("战斗", styles.Section);
            DrawSwitch(CheatSettings.InfiniteMana, "魔力不减", "出牌时不消耗魔力", typeof(InfiniteManaPatch));
            DrawSwitch(CheatSettings.Invincible, "生命不减", "玩家不会损失生命值", typeof(LoseLifePatch));
            DrawDamageMultiplier();

            GUILayout.Label("资源", styles.Section);
            DrawMoneyMode();

            GUILayout.Label("卡牌", styles.Section);
            DrawSwitch(CheatSettings.AnyManaColor, "卡牌能量种类无要求",
                "之后生成的卡牌可用任意颜色的魔力打出", typeof(AnyManaColorPatch));
            DrawSwitch(CheatSettings.AutoUpgradeCards, "卡牌自动强化",
                "卡组中的卡牌会自动升级（每张卡只强化一次）", typeof(AutoUpgradePatch));

            GUILayout.Label("图鉴", styles.Section);
            DrawSwitch(CheatSettings.UnlockCardCollection, "卡牌图鉴全解锁",
                "图鉴中显示全部卡牌，便于在“卡牌图鉴”页添加", typeof(CardUnlockPatch));
            DrawSwitch(CheatSettings.UnlockRelicCollection, "遗物图鉴全解锁",
                "图鉴中显示全部遗物，便于在“遗物图鉴”页添加", typeof(RelicUnlockPatch));

            GUILayout.EndScrollView();
        }

        private void DrawSwitch(ConfigEntry<bool> entry, string name, string description, Type patchType)
        {
            BeginRow(name, description, CheatPatches.IsAvailable(patchType));
            bool value = SwitchButton(entry.Value);
            if (value != entry.Value) entry.Value = value;
            EndRow();
        }

        private void DrawDamageMultiplier()
        {
            int multiplier = CheatSettings.DamageMultiplier.Value;
            bool enabled = CheatSettings.DamageMultiplierEnabled.Value;
            BeginRow("伤害倍率", "对敌人造成的伤害 ×" + multiplier + "，不影响玩家受到的伤害",
                CheatPatches.IsAvailable(typeof(LoseLifePatch)));

            bool rowEnabled = GUI.enabled;
            GUI.enabled = rowEnabled && enabled;
            GUILayout.BeginVertical(GUILayout.Width(130f));
            GUILayout.FlexibleSpace();
            int value = Mathf.RoundToInt(GUILayout.HorizontalSlider(multiplier, 1f, 10f));
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            GUILayout.Label("×" + multiplier, styles.Value, GUILayout.Width(40f), GUILayout.Height(30f));
            GUI.enabled = rowEnabled;
            if (value != multiplier) CheatSettings.DamageMultiplier.Value = value;

            bool toggled = SwitchButton(enabled);
            if (toggled != enabled) CheatSettings.DamageMultiplierEnabled.Value = toggled;
            EndRow();
        }

        private void DrawMoneyMode()
        {
            MoneyMode mode = CheatSettings.Money.Value;
            string description = mode == MoneyMode.Gain ? "花费金币时反而获得等量金币"
                : mode == MoneyMode.NoLoss ? "花费金币时金币不会减少" : "游戏原始逻辑";
            BeginRow("金币", description, CheatPatches.IsAvailable(typeof(MoneyPatch)));
            int selected = GUILayout.Toolbar((int)mode, MoneyModeNames, styles.Tab, GUILayout.Width(270f), GUILayout.Height(30f));
            if (selected != (int)mode) CheatSettings.Money.Value = (MoneyMode)selected;
            EndRow();
        }

        private void DrawDeckTab()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(inRun ? "当前卡组 · 共 " + deckList.Count + " 张" : "当前卡组", styles.Section);
            GUILayout.FlexibleSpace();
            DrawSearchField(deckList);
            if (GUILayout.Button("刷新", styles.Button, GUILayout.Width(56f), GUILayout.Height(26f))) nextDeckRefresh = 0f;
            GUILayout.EndHorizontal();
            GUILayout.Label("“复制”会再加入一张相同的卡牌；“删除”需要再点一次确认。", styles.Hint);

            deckList.Draw(styles, drawDeckRow,
                inRun ? "当前卡组为空" : "未进入对局：开始一局游戏后即可在这里管理卡组", windowRect.height);
        }

        private void DrawCardCollectionTab()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("卡牌图鉴 · 当前页 " + cardList.Count + " 张", styles.Section);
            GUILayout.FlexibleSpace();
            DrawSearchField(cardList);
            GUILayout.EndHorizontal();
            GUILayout.Label("在游戏中打开卡牌图鉴并翻页，这里会自动同步当前页的卡牌；需要先进入对局才能添加。", styles.Hint);

            cardList.Draw(styles, drawCardRow, "暂无数据：请先在游戏中打开卡牌图鉴页面", windowRect.height);
        }

        private void DrawRelicCollectionTab()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("遗物图鉴 · 当前分类 " + relicList.Count + " 件", styles.Section);
            GUILayout.FlexibleSpace();
            DrawSearchField(relicList);
            bool previous = GUI.enabled;
            GUI.enabled = previous && relicList.Count > 0;
            if (ConfirmButton(AddAllRelicsKey, "全部获得", "确认全部获得", styles.Button, GUILayout.Width(110f), GUILayout.Height(26f)))
            {
                pendingActions.Enqueue(CheatActions.AddAllRelics);
            }
            GUI.enabled = previous;
            GUILayout.EndHorizontal();
            GUILayout.Label("在游戏中打开遗物图鉴并切换稀有度分类，这里会自动同步；“全部获得”会获得当前分类下的所有遗物。", styles.Hint);

            relicList.Draw(styles, drawRelicRow, "暂无数据：请先在游戏中打开遗物图鉴页面", windowRect.height);
        }

        private void DrawDeckRow(int index, object card, string name)
        {
            DrawRowLabel(index, name, "-- 无效卡牌 --");
            bool previous = GUI.enabled;
            GUI.enabled = previous && IsAlive(card);
            if (GUILayout.Button("复制", styles.RowButton, GUILayout.Width(56f)))
            {
                pendingActions.Enqueue(() => CheatActions.CopyCard(card));
            }
            if (ConfirmButton(card, "删除", "确认删除", styles.RowButton, GUILayout.Width(76f)))
            {
                pendingActions.Enqueue(() => CheatActions.RemoveCard(card));
            }
            GUI.enabled = previous;
        }

        private void DrawCardRow(int index, object card, string name)
        {
            DrawRowLabel(index, name, "-- 无效卡牌 --");
            bool previous = GUI.enabled;
            GUI.enabled = previous && IsAlive(card);
            if (GUILayout.Button("添加到卡组", styles.RowButton, GUILayout.Width(96f)))
            {
                pendingActions.Enqueue(() => CheatActions.AddCollectionCard(card));
            }
            GUI.enabled = previous;
        }

        private void DrawRelicRow(int index, object relic, string name)
        {
            DrawRowLabel(index, name, "-- 无效遗物 --");
            bool previous = GUI.enabled;
            GUI.enabled = previous && IsAlive(relic);
            if (GUILayout.Button("获得", styles.RowButton, GUILayout.Width(64f)))
            {
                pendingActions.Enqueue(() => CheatActions.AddRelic(relic));
            }
            GUI.enabled = previous;
        }

        private void DrawRowLabel(int index, string name, string fallback)
        {
            GUILayout.Label(FilteredListView.IndexLabel(index), styles.Index, GUILayout.Width(52f));
            GUILayout.Label(string.IsNullOrEmpty(name) ? fallback : name, styles.RowLabel);
        }

        private void DrawSettingsTab()
        {
            settingsScroll = GUILayout.BeginScrollView(settingsScroll, GUILayout.ExpandHeight(true));

            GUILayout.Label("快捷键", styles.Section);
            BeginRow("显示/隐藏窗口",
                waitingForHotkey ? "请按下新的按键，按 Esc 取消" : "当前快捷键：" + CheatSettings.Hotkey.Value, true);
            if (GUILayout.Button(waitingForHotkey ? "等待按键…" : "修改", waitingForHotkey ? styles.SwitchOn : styles.Button,
                GUILayout.Width(90f), GUILayout.Height(30f)))
            {
                waitingForHotkey = !waitingForHotkey;
                GUIUtility.keyboardControl = 0;
            }
            EndRow();

            GUILayout.Label("界面", styles.Section);
            float scale = CheatSettings.UiScale.Value;
            BeginRow("界面缩放", "高分辨率屏幕下文字太小时可以调大", true);
            if (GUILayout.Button("-", styles.Button, GUILayout.Width(32f), GUILayout.Height(30f))) SetScale(scale - ScaleStep);
            GUILayout.Label(Mathf.RoundToInt(scale * 100f) + "%", styles.Value, GUILayout.Width(56f), GUILayout.Height(30f));
            if (GUILayout.Button("+", styles.Button, GUILayout.Width(32f), GUILayout.Height(30f))) SetScale(scale + ScaleStep);
            if (GUILayout.Button("重置", styles.Button, GUILayout.Width(56f), GUILayout.Height(30f))) SetScale(1f);
            EndRow();

            BeginRow("阻止点击穿透", "鼠标在窗口上时，点击不会触发窗口下方的游戏按钮", true);
            bool block = SwitchButton(CheatSettings.BlockGameInput.Value);
            if (block != CheatSettings.BlockGameInput.Value) CheatSettings.BlockGameInput.Value = block;
            EndRow();

            BeginRow("窗口位置与大小", "拖动标题栏移动窗口，拖动右下角调整大小", true);
            if (GUILayout.Button("恢复默认", styles.Button, GUILayout.Width(90f), GUILayout.Height(30f)))
            {
                pendingWindowRect = CheatSettings.DefaultWindowRect;
            }
            EndRow();

            GUILayout.Label("其他", styles.Section);
            BeginRow("刷新名称缓存", "切换游戏语言后，图鉴中的名称没有更新时使用", true);
            if (GUILayout.Button("刷新", styles.Button, GUILayout.Width(90f), GUILayout.Height(30f)))
            {
                CheatActions.ClearNameCache();
                ShowStatus("名称缓存已清空", InfoColor);
            }
            EndRow();

            BeginRow("一键关闭全部功能", "关闭所有修改功能，恢复游戏原始逻辑", true);
            if (ConfirmButton(DisableAllKey, "全部关闭", "确认关闭", styles.Button, GUILayout.Width(90f), GUILayout.Height(30f)))
            {
                CheatSettings.DisableAllCheats();
                ShowStatus("已关闭全部功能", SuccessColor);
            }
            EndRow();

            GUILayout.Label("版本 " + DICEOMANCERCheatMenu.PluginVersion + " · 配置文件：BepInEx/config/"
                + DICEOMANCERCheatMenu.PluginGuid + ".cfg", styles.Hint);

            GUILayout.EndScrollView();
        }

        private void SetScale(float scale)
        {
            CheatSettings.UiScale.Value = Mathf.Round(scale * 10f) / 10f;
        }

        private void DrawStatusBar()
        {
            GUILayout.BeginHorizontal(styles.StatusBar, GUILayout.Height(26f));

            float age = Time.unscaledTime - statusTime;
            bool showStatus = age < StatusDuration;
            Color previousColor = GUI.color;
            if (showStatus)
            {
                GUI.color = new Color(previousColor.r, previousColor.g, previousColor.b,
                    previousColor.a * Mathf.Clamp01((StatusDuration - age) / 0.6f));
            }
            styles.Status.normal.textColor = statusColor;
            GUILayout.Label(showStatus ? statusText : string.Empty, styles.Status, GUILayout.Height(22f));
            GUI.color = previousColor;

            GUILayout.FlexibleSpace();
            GUILayout.Label(HotkeyHint(), styles.Hint, GUILayout.Height(22f));
            GUILayout.Space(GripSize);
            GUILayout.EndHorizontal();
        }

        private string HotkeyHint()
        {
            if (hintKey != CheatSettings.Hotkey.Value)
            {
                hintKey = CheatSettings.Hotkey.Value;
                hintText = "按 " + hintKey + " 显示/隐藏";
            }
            return hintText;
        }

        private void DrawSearchField(FilteredListView list)
        {
            list.Filter = GUILayout.TextField(list.Filter, 40, styles.TextField, GUILayout.Width(180f), GUILayout.Height(26f));
            if (Event.current.type == EventType.Repaint && list.Filter.Length == 0)
            {
                GUI.Label(GUILayoutUtility.GetLastRect(), "搜索名称或编号", styles.Placeholder);
            }
        }

        // 功能行：左侧名称与说明，右侧放置控件
        private void BeginRow(string name, string description, bool available)
        {
            GUILayout.BeginHorizontal(styles.Card);
            GUILayout.BeginVertical();
            GUILayout.Label(name, styles.FeatureName);
            GUILayout.Label(available ? description : "当前游戏版本不支持此功能", styles.Hint);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUI.enabled = available;
        }

        private void EndRow()
        {
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private bool SwitchButton(bool on)
        {
            bool clicked = GUILayout.Button(on ? "已开启" : "已关闭", on ? styles.SwitchOn : styles.SwitchOff,
                GUILayout.Width(80f), GUILayout.Height(30f));
            return clicked ? !on : on;
        }

        // 危险操作需要在 3 秒内点击两次
        private bool ConfirmButton(object key, string label, string confirmLabel, GUIStyle style, params GUILayoutOption[] options)
        {
            bool armed = ReferenceEquals(confirmKey, key) && Time.unscaledTime - confirmTime < ConfirmTimeout;
            GUIStyle armedStyle = style == styles.RowButton ? styles.RowDangerButton : styles.DangerButton;
            if (!GUILayout.Button(armed ? confirmLabel : label, armed ? armedStyle : style, options)) return false;

            if (armed)
            {
                confirmKey = null;
                return true;
            }
            confirmKey = key;
            confirmTime = Time.unscaledTime;
            return false;
        }

        private static bool IsAlive(object item)
        {
            var unityObject = item as UnityEngine.Object;
            return ReferenceEquals(unityObject, null) ? item != null : unityObject != null;
        }
    }
}
