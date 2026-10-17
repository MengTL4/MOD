using System;
using System.Collections.Generic;
using UnityEngine;

namespace DICEOMANCERCheatMenu
{
    // 带搜索过滤和虚拟滚动的列表：只绘制可见范围内的行，条目再多也不会拖慢界面。
    // 过滤结果和可见范围只在 Layout 事件中更新，保证同一帧内 Layout 与 Repaint 的控件数量一致。
    internal sealed class FilteredListView
    {
        public const float RowHeight = 28f;
        private const int OverscanRows = 2;

        private static readonly List<string> indexLabels = new List<string>();

        private readonly List<object> items = new List<object>();
        private readonly List<string> names = new List<string>();
        private readonly List<string> scratchNames = new List<string>();
        private readonly List<int> filtered = new List<int>();
        private string appliedFilter = string.Empty;
        private bool dirty = true;
        private int firstVisible;
        private Vector2 scroll;

        public string Filter = string.Empty;

        public int Count
        {
            get { return items.Count; }
        }

        public static string IndexLabel(int index)
        {
            while (indexLabels.Count <= index) indexLabels.Add("No." + (indexLabels.Count + 1));
            return indexLabels[index];
        }

        public void SetItems(object[] source, Func<object, string> nameOf)
        {
            scratchNames.Clear();
            for (int i = 0; i < source.Length; i++) scratchNames.Add(nameOf(source[i]));
            if (IsSameAs(source)) return;

            items.Clear();
            items.AddRange(source);
            names.Clear();
            names.AddRange(scratchNames);
            dirty = true;
        }

        public void Draw(MenuStyles styles, Action<int, object, string> drawRow, string emptyText, float maxViewHeight)
        {
            if (Event.current.type == EventType.Layout)
            {
                if (dirty || appliedFilter != Filter) Rebuild();
                firstVisible = Mathf.Max(0, Mathf.FloorToInt(scroll.y / RowHeight) - OverscanRows);
            }

            scroll = GUILayout.BeginScrollView(scroll, false, false, GUI.skin.horizontalScrollbar,
                GUI.skin.verticalScrollbar, styles.ListBackground, GUILayout.ExpandHeight(true));

            if (filtered.Count == 0)
            {
                GUILayout.Label(items.Count == 0 ? emptyText : "没有符合搜索条件的结果", styles.Empty);
            }
            else
            {
                int first = Mathf.Min(firstVisible, filtered.Count);
                int last = Mathf.Min(filtered.Count, first + Mathf.CeilToInt(maxViewHeight / RowHeight) + OverscanRows * 2);

                if (first > 0) GUILayout.Space(first * RowHeight);
                for (int k = first; k < last; k++)
                {
                    int index = filtered[k];
                    GUILayout.BeginHorizontal(k % 2 == 0 ? styles.RowEven : styles.RowOdd, GUILayout.Height(RowHeight));
                    drawRow(index, items[index], names[index]);
                    GUILayout.EndHorizontal();
                }
                if (last < filtered.Count) GUILayout.Space((filtered.Count - last) * RowHeight);
            }

            GUILayout.EndScrollView();
        }

        private bool IsSameAs(object[] source)
        {
            if (dirty || source.Length != items.Count) return false;
            for (int i = 0; i < source.Length; i++)
            {
                if (!ReferenceEquals(source[i], items[i]) || scratchNames[i] != names[i]) return false;
            }
            return true;
        }

        private void Rebuild()
        {
            if (appliedFilter != Filter) scroll = Vector2.zero;
            appliedFilter = Filter;
            dirty = false;

            filtered.Clear();
            string keyword = Filter.Trim();
            int number;
            bool byNumber = int.TryParse(keyword, out number);
            for (int i = 0; i < items.Count; i++)
            {
                if (keyword.Length == 0
                    || (byNumber && number == i + 1)
                    || (names[i] != null && names[i].IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    filtered.Add(i);
                }
            }
        }
    }
}
