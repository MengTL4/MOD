namespace Lean.Localization
{
    /// <summary>
    /// No translation tables are loaded headless. A missing translation comes back as the fallback,
    /// else the key itself — never "": KeyWord.LoopKeyWord splits every keyword's translated name and
    /// matches it inside descriptions, and an empty name matched everything and made
    /// <c>string.Replace("")</c> throw inside BattleEventPanel.Init — a bounty carrying a battle event
    /// (e.g. enemy 102 with event 10006) never reached its first player turn. Only text shown to
    /// the player is affected.
    /// </summary>
    [UnityEngine.ShimLenient]
    public partial class LeanLocalization
    {
        public static string GetTranslationText(string name, string fallback = default, bool replaceTokens = true) =>
            string.IsNullOrEmpty(fallback) ? name ?? string.Empty : fallback;
    }
}
