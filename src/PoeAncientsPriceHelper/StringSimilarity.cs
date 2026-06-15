namespace PoeAncientsPriceHelper;

internal static class StringSimilarity
{
    internal static double Score(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1.0;

        int dist = ScanEngine.Levenshtein(a, b);
        double lev = 1.0 - (double)dist / Math.Max(a.Length, b.Length);
        int lcs = LcsLength(a, b);
        double lcsMax = (double)lcs / Math.Max(a.Length, b.Length);
        // min-length ratio rescues ornate-font misreads with extra tail chars ("vltalltyli" → "vitality").
        double lcsMin = (double)lcs / Math.Min(a.Length, b.Length);
        return Math.Max(lev, Math.Max(lcsMax, lcsMin * 0.92));
    }

    internal static int LcsLength(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                curr[j] = a[i - 1] == b[j - 1]
                    ? prev[j - 1] + 1
                    : Math.Max(prev[j], curr[j - 1]);
            }
            (prev, curr) = (curr, prev);
            Array.Clear(curr);
        }
        return prev[b.Length];
    }
}
