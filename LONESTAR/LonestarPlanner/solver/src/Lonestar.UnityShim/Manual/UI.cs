namespace UnityEngine.UI
{
    /// <summary>
    /// Unity wires <see cref="fillRect"/> from the prefab; the asset exporter does not write the
    /// fields of built-in UI components, so take the child Unity's default slider uses ("Fill").
    /// Boss skills (Skill_PowerSumLocker) colour it inside the turn flow: a null here throws in the
    /// enemy's turn coroutine and the battle never reaches the player's turn.
    /// </summary>
    [ShimLenient]
    public partial class Slider
    {
        private RectTransform fill;

        public RectTransform fillRect
        {
            get => fill ??= FindFill(transform) ?? Shim.Ret<RectTransform>();
            set => fill = value;
        }

        private static RectTransform FindFill(Transform t)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                Transform c = t.GetChild(i);
                if (c.name == "Fill" && c is RectTransform r)
                {
                    return r;
                }
                RectTransform deeper = FindFill(c);
                if (deeper != null)
                {
                    return deeper;
                }
            }
            return null;
        }
    }
}
