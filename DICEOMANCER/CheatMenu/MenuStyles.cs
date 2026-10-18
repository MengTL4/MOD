using System.Collections.Generic;
using UnityEngine;

namespace DICEOMANCERCheatMenu
{
    // 修改器界面的配色与样式。GUIStyle 只能在 OnGUI 中创建，因此由窗口在首次绘制时构造。
    internal sealed class MenuStyles
    {
        private static readonly Color TextColor = new Color(0.92f, 0.93f, 0.96f);
        private static readonly Color HintColor = new Color(0.58f, 0.62f, 0.70f);
        private static readonly Color AccentColor = new Color(0.36f, 0.62f, 1f);
        private static readonly Color WindowColor = new Color(0.10f, 0.11f, 0.14f, 0.97f);
        private static readonly Color BorderColor = new Color(0.28f, 0.31f, 0.40f, 1f);
        private static readonly Color TitleColor = new Color(0.15f, 0.17f, 0.23f, 1f);
        private static readonly Color PanelColor = new Color(0.14f, 0.15f, 0.20f, 1f);
        private static readonly Color RowOddColor = new Color(0.12f, 0.13f, 0.17f, 1f);
        private static readonly Color InputColor = new Color(0.07f, 0.08f, 0.10f, 1f);

        private readonly List<Texture2D> textures = new List<Texture2D>();

        public readonly GUIStyle Window;
        public readonly GUIStyle Title;
        public readonly GUIStyle CloseButton;
        public readonly GUIStyle Tab;
        public readonly GUIStyle Section;
        public readonly GUIStyle FeatureName;
        public readonly GUIStyle Hint;
        public readonly GUIStyle Value;
        public readonly GUIStyle Card;
        public readonly GUIStyle ListBackground;
        public readonly GUIStyle RowEven;
        public readonly GUIStyle RowOdd;
        public readonly GUIStyle Index;
        public readonly GUIStyle RowLabel;
        public readonly GUIStyle Empty;
        public readonly GUIStyle Button;
        public readonly GUIStyle RowButton;
        public readonly GUIStyle DangerButton;
        public readonly GUIStyle RowDangerButton;
        public readonly GUIStyle SwitchOn;
        public readonly GUIStyle SwitchOff;
        public readonly GUIStyle TextField;
        public readonly GUIStyle Placeholder;
        public readonly GUIStyle StatusBar;
        public readonly GUIStyle Status;
        public readonly Texture2D Grip;

        public MenuStyles()
        {
            GUISkin skin = GUI.skin;

            Window = new GUIStyle(skin.window)
            {
                border = new RectOffset(1, 1, 1, 1),
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0)
            };
            Window.normal.background = Window.onNormal.background = Bordered(WindowColor, BorderColor);

            Title = Text(skin.label, 15, TextColor, TextAnchor.MiddleLeft, FontStyle.Bold);
            Title.padding = new RectOffset(12, 40, 0, 0);
            Title.normal.background = Solid(TitleColor);

            CloseButton = SolidButton(skin.button, TitleColor, new Color(0.78f, 0.27f, 0.27f), new Color(0.62f, 0.20f, 0.20f));
            CloseButton.fontSize = 18;
            CloseButton.padding = new RectOffset(0, 0, 0, 2);

            Tab = SolidButton(skin.button, new Color(0.18f, 0.20f, 0.26f), new Color(0.24f, 0.27f, 0.35f), new Color(0.16f, 0.18f, 0.23f));
            Tab.onNormal.background = Tab.onHover.background = Tab.onActive.background = Solid(new Color(0.22f, 0.42f, 0.78f));
            Tab.onNormal.textColor = Tab.onHover.textColor = Tab.onActive.textColor = Color.white;
            Tab.fontStyle = FontStyle.Bold;

            Section = Text(skin.label, 14, AccentColor, TextAnchor.MiddleLeft, FontStyle.Bold);
            Section.margin = new RectOffset(2, 2, 8, 4);

            FeatureName = Text(skin.label, 14, TextColor, TextAnchor.MiddleLeft, FontStyle.Bold);
            Hint = Text(skin.label, 12, HintColor, TextAnchor.MiddleLeft, FontStyle.Normal);
            Hint.wordWrap = true;
            Value = Text(skin.label, 14, TextColor, TextAnchor.MiddleCenter, FontStyle.Bold);

            Card = new GUIStyle
            {
                padding = new RectOffset(12, 10, 8, 8),
                margin = new RectOffset(0, 0, 0, 6)
            };
            Card.normal.background = Solid(PanelColor);

            ListBackground = new GUIStyle();
            ListBackground.normal.background = Solid(new Color(0.08f, 0.09f, 0.12f, 1f));

            RowEven = new GUIStyle { padding = new RectOffset(8, 6, 3, 3) };
            RowEven.normal.background = Solid(PanelColor);
            RowOdd = new GUIStyle(RowEven);
            RowOdd.normal.background = Solid(RowOddColor);

            Index = Text(skin.label, 12, HintColor, TextAnchor.MiddleLeft, FontStyle.Normal);
            Index.fixedHeight = FilteredListView.RowHeight - 6f;
            RowLabel = Text(skin.label, 14, TextColor, TextAnchor.MiddleLeft, FontStyle.Normal);
            RowLabel.fixedHeight = FilteredListView.RowHeight - 6f;
            RowLabel.clipping = TextClipping.Clip;
            RowLabel.wordWrap = false;
            Empty = Text(skin.label, 13, HintColor, TextAnchor.MiddleCenter, FontStyle.Normal);
            Empty.padding = new RectOffset(10, 10, 24, 24);
            Empty.wordWrap = true;

            Button = SolidButton(skin.button, new Color(0.22f, 0.25f, 0.32f), new Color(0.29f, 0.33f, 0.42f), new Color(0.18f, 0.20f, 0.26f));
            RowButton = new GUIStyle(Button)
            {
                fixedHeight = FilteredListView.RowHeight - 6f,
                margin = new RectOffset(4, 0, 0, 0),
                fontSize = 12
            };
            DangerButton = SolidButton(Button, new Color(0.70f, 0.24f, 0.24f), new Color(0.80f, 0.30f, 0.30f), new Color(0.60f, 0.20f, 0.20f));
            RowDangerButton = SolidButton(RowButton, new Color(0.70f, 0.24f, 0.24f), new Color(0.80f, 0.30f, 0.30f), new Color(0.60f, 0.20f, 0.20f));
            SwitchOn = SolidButton(skin.button, new Color(0.18f, 0.56f, 0.34f), new Color(0.22f, 0.64f, 0.40f), new Color(0.15f, 0.48f, 0.29f));
            SwitchOn.fontStyle = FontStyle.Bold;
            SwitchOff = SolidButton(skin.button, new Color(0.26f, 0.27f, 0.32f), new Color(0.32f, 0.34f, 0.40f), new Color(0.22f, 0.23f, 0.27f));
            SwitchOff.normal.textColor = SwitchOff.hover.textColor = HintColor;

            TextField = new GUIStyle(skin.textField)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 8, 3, 3),
                border = new RectOffset(1, 1, 1, 1)
            };
            TextField.normal.background = TextField.hover.background = Bordered(InputColor, BorderColor);
            TextField.focused.background = TextField.onFocused.background = Bordered(InputColor, AccentColor);
            TextField.normal.textColor = TextField.hover.textColor = TextField.focused.textColor = TextColor;
            Placeholder = new GUIStyle(TextField);
            Placeholder.normal.background = null;
            Placeholder.normal.textColor = new Color(HintColor.r, HintColor.g, HintColor.b, 0.7f);

            StatusBar = new GUIStyle { padding = new RectOffset(8, 4, 2, 2) };
            StatusBar.normal.background = Solid(TitleColor);
            Status = Text(skin.label, 13, TextColor, TextAnchor.MiddleLeft, FontStyle.Normal);
            Status.wordWrap = false;
            Status.clipping = TextClipping.Clip;

            Grip = CreateGrip(12, new Color(0.55f, 0.60f, 0.70f, 0.8f));
        }

        // 场景切换时 Resources.UnloadUnusedAssets 可能卸载贴图，需要重新创建样式
        public bool IsValid
        {
            get
            {
                for (int i = 0; i < textures.Count; i++)
                {
                    if (textures[i] == null) return false;
                }
                return true;
            }
        }

        public void Destroy()
        {
            foreach (Texture2D texture in textures)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
            textures.Clear();
        }

        private static GUIStyle Text(GUIStyle source, int fontSize, Color color, TextAnchor alignment, FontStyle fontStyle)
        {
            var style = new GUIStyle(source)
            {
                fontSize = fontSize,
                alignment = alignment,
                fontStyle = fontStyle,
                wordWrap = false
            };
            style.normal.textColor = color;
            return style;
        }

        private GUIStyle SolidButton(GUIStyle source, Color normal, Color hover, Color active)
        {
            var style = new GUIStyle(source)
            {
                border = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.MiddleCenter,
                fontSize = source.fontSize > 0 ? source.fontSize : 13
            };
            style.normal.background = Solid(normal);
            style.hover.background = Solid(hover);
            style.active.background = Solid(active);
            style.focused.background = style.normal.background;
            style.onNormal.background = style.normal.background;
            style.onHover.background = style.hover.background;
            style.onActive.background = style.active.background;
            style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = TextColor;
            style.onNormal.textColor = style.onHover.textColor = style.onActive.textColor = TextColor;
            return style;
        }

        private Texture2D Solid(Color color)
        {
            Texture2D texture = NewTexture(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private Texture2D Bordered(Color fill, Color border)
        {
            Texture2D texture = NewTexture(3, 3);
            for (int x = 0; x < 3; x++)
            {
                for (int y = 0; y < 3; y++)
                {
                    texture.SetPixel(x, y, x == 1 && y == 1 ? fill : border);
                }
            }
            texture.Apply();
            return texture;
        }

        private Texture2D CreateGrip(int size, Color color)
        {
            Texture2D texture = NewTexture(size, size);
            var clear = new Color(0f, 0f, 0f, 0f);
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    // 右下角的斜纹（贴图 y 轴向上）
                    int d = x - y;
                    texture.SetPixel(x, y, d > 0 && d % 4 == 2 ? color : clear);
                }
            }
            texture.Apply();
            return texture;
        }

        private Texture2D NewTexture(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            textures.Add(texture);
            return texture;
        }
    }
}
