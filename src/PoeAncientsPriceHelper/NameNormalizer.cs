namespace PoeAncientsPriceHelper;

internal static class NameNormalizer
{
    private static readonly HashSet<string> KeepShortTokens = new(StringComparer.Ordinal)
        { "of", "s", "x" };

    internal static string Normalize(string text)
    {
        var s = text.ToLowerInvariant();
        s = Regex.Replace(s, @"[^\w\s]", " ");
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }

    // Repairs common PoE-font OCR slips before price lookup. Applied after quantity/noise stripping.
    internal static string FixOcrArtifacts(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return normalized;

        var s = normalized;

        // Possessive misread glued to the next token: "jeweller sfl" (1–2 junk letters, no space).
        // "vorana s saga" has a space after the possessive s, so it is left untouched.
        s = Regex.Replace(s, @"(\w+)\s+s[a-z]{1,2}(?=\s|$)", "$1 s");

        // "Rune" misread as "leune" / "1leune" (PoE font: r↔l, missing x).
        s = Regex.Replace(s, @"\b1?leune\b", "rune");

        // "Orb" split across tokens: "or l t", "or l", "or b", "orlt" style garbage.
        s = Regex.Replace(s, @"\bor\s+[a-z]\s+[a-z]\b", "orb");
        s = Regex.Replace(s, @"\bor\s+[a-z]\b(?!\s*of\b)", "orb");

        // Trailing 1–2 letter tokens after the real name ("… vitality li").
        s = Regex.Replace(s, @"\s+[a-z]{1,2}$", "");

        // Drop isolated 1-char tokens (OCR noise), but keep possessive "s".
        s = Regex.Replace(s, @"(?<=\s)(?!s)[a-z](?=\s)", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    // Removes OCR noise tokens while keeping short grammatical words that appear in item names.
    internal static string SanitizeForMatch(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return normalized;

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(tokens.Length);
        foreach (var t in tokens)
        {
            if (t.Length >= 3 || KeepShortTokens.Contains(t))
                kept.Add(t);
        }
        return string.Join(' ', kept);
    }
}
