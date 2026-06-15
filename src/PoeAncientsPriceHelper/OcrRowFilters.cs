namespace PoeAncientsPriceHelper;

internal static class OcrRowFilters
{
    // Title / chrome lines that are not exchange rewards.
    internal static bool IsPanelChrome(string normalizedName)
    {
        if (string.IsNullOrWhiteSpace(normalizedName)) return true;
        if (normalizedName.Contains("runeshape")) return true;
        if (normalizedName.Contains("combination") &&
            !normalizedName.Contains("orb") &&
            !normalizedName.Contains("rune") &&
            !normalizedName.Contains("saga") &&
            !normalizedName.Contains("gem"))
            return true;
        return false;
    }

    internal static bool LooksLikeReward(string normalizedName) =>
        !IsPanelChrome(normalizedName) &&
        normalizedName.Length >= 6 &&
        NameNormalizer.Normalize(normalizedName).Length >= 6;

    internal static bool IsRuneshapePanel(IEnumerable<OcrRow> rows) =>
        rows.Any(r => r.NormalizedName.Contains("runeshape"));
}
