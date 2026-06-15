namespace PoeAncientsPriceHelper;

internal static class ItemNameMatcher
{
    private const double FuzzyThreshold = 0.84;
    private const double TokenThreshold = 0.75;
    private const double StructuralThreshold = 0.62;

    private static readonly string[] StructuralPrefixes =
    [
        "ancient rune of ",
        "greater rune of ",
        "rune of ",
        "greater orb of ",
        "orb of ",
    ];

    internal static bool TryMatch(
        string ocrName,
        ExchangeItemCatalog catalog,
        out string matchedKey,
        out PriceEntry entry,
        out bool exact)
    {
        matchedKey = ocrName;
        entry = default!;
        exact = false;

        if (catalog.Count == 0) return false;

        var candidates = BuildCandidates(ocrName);
        foreach (var candidate in candidates)
        {
            if (catalog.TryGetPrice(candidate, out var hit))
            {
                matchedKey = candidate;
                entry = hit;
                // Treat artifact-corrected and canonical keys as exact so a single clean read locks.
                exact = candidate == ocrName ||
                        candidate == NameNormalizer.FixOcrArtifacts(ocrName) ||
                        candidate == NameNormalizer.SanitizeForMatch(NameNormalizer.FixOcrArtifacts(ocrName));
                return true;
            }
        }

        var primary = candidates[0];

        if (TryStructuralMatch(primary, catalog) is { } structuralKey &&
            catalog.TryGetPrice(structuralKey, out var structuralEntry))
        {
            matchedKey = structuralKey;
            entry = structuralEntry;
            exact = StringSimilarity.Score(primary, structuralKey) >= 0.88;
            return true;
        }

        if (primary.Length >= 10)
        {
            string? prefixKey = null;
            foreach (var key in catalog.CandidateKeysFor(primary))
            {
                if (!key.StartsWith(primary, StringComparison.Ordinal)) continue;
                if (prefixKey is null || key.Length < prefixKey.Length) prefixKey = key;
            }
            if (prefixKey is not null && catalog.TryGetPrice(prefixKey, out var prefixEntry))
            {
                matchedKey = prefixKey;
                entry = prefixEntry;
                exact = primary.Length >= 12;
                return true;
            }
        }

        if (primary.Length >= 6 && BestFuzzy(catalog, primary) is { } fuzzyKey &&
            catalog.TryGetPrice(fuzzyKey, out var fuzzyEntry))
        {
            matchedKey = fuzzyKey;
            entry = fuzzyEntry;
            return true;
        }

        foreach (var candidate in candidates.Skip(1))
        {
            if (candidate.Length < 6) continue;
            if (BestTokenMatch(catalog, candidate) is { } tokenKey &&
                catalog.TryGetPrice(tokenKey, out var tokenEntry))
            {
                matchedKey = tokenKey;
                entry = tokenEntry;
                return true;
            }
        }

        return false;
    }

    private static string? TryStructuralMatch(string name, ExchangeItemCatalog catalog)
    {
        foreach (var prefix in StructuralPrefixes.OrderByDescending(p => p.Length))
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var suffix = name[prefix.Length..];
            if (suffix.Length < 4) continue;

            string? best = null;
            double bestScore = StructuralThreshold;
            foreach (var key in catalog.KeysWithPrefix(prefix))
            {
                var keySuffix = key[prefix.Length..];
                double score = StringSimilarity.Score(suffix, keySuffix);
                if (score > bestScore) { bestScore = score; best = key; }
            }
            if (best is not null) return best;
        }
        return null;
    }

    private static List<string> BuildCandidates(string ocrName)
    {
        var fixed_ = NameNormalizer.FixOcrArtifacts(ocrName);
        var sanitized = NameNormalizer.SanitizeForMatch(fixed_);
        var sanitizedRaw = NameNormalizer.SanitizeForMatch(ocrName);

        var list = new List<string>(4) { fixed_ };
        if (sanitized != fixed_) list.Add(sanitized);
        if (sanitizedRaw != fixed_ && sanitizedRaw != sanitized) list.Add(sanitizedRaw);
        if (!list.Contains(ocrName)) list.Insert(0, ocrName);
        return list;
    }

    private static string? BestFuzzy(ExchangeItemCatalog catalog, string name)
    {
        string? best = null;
        double bestScore = FuzzyThreshold;
        int maxLenDelta = name.Length >= 15 ? 8 : 5;
        int scanned = 0;

        foreach (var key in catalog.CandidateKeysFor(name))
        {
            if (Math.Abs(key.Length - name.Length) > maxLenDelta) continue;
            double score = StringSimilarity.Score(name, key);
            if (score > bestScore) { bestScore = score; best = key; }
            if (++scanned >= 120) break;
        }
        return best;
    }

    private static string? BestTokenMatch(ExchangeItemCatalog catalog, string name)
    {
        var ocrTokens = SignificantTokens(name);
        if (ocrTokens.Count == 0) return null;

        string? best = null;
        double bestScore = TokenThreshold;
        int scanned = 0;

        foreach (var key in catalog.CandidateKeysFor(name))
        {
            var keyTokens = SignificantTokens(key);
            if (keyTokens.Count == 0) continue;

            int matched = 0;
            foreach (var kt in keyTokens)
            {
                if (ocrTokens.Contains(kt)) { matched++; continue; }
                foreach (var ot in ocrTokens)
                {
                    if (TokenSimilar(ot, kt)) { matched++; break; }
                }
            }

            double score = (double)matched / keyTokens.Count;
            if (score > bestScore) { bestScore = score; best = key; }
            if (++scanned >= 120) break;
        }
        return best;
    }

    private static HashSet<string> SignificantTokens(string name)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.Length >= 3 || t is "of" or "s")
                set.Add(t);
        }
        return set;
    }

    private static bool TokenSimilar(string a, string b)
    {
        if (a == b) return true;
        int maxLen = Math.Max(a.Length, b.Length);
        int allowedDelta = maxLen >= 8 ? 4 : 2;
        if (Math.Abs(a.Length - b.Length) > allowedDelta) return false;
        return StringSimilarity.Score(a, b) >= (maxLen >= 8 ? 0.58 : 0.72);
    }
}
