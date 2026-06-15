namespace PoeAncientsPriceHelper;

using System.Collections.ObjectModel;

// Closed vocabulary of exchange items (canonical normalized names from poe.ninja/cache).
// Built automatically whenever prices load — not a hardcoded enum.
internal sealed class ExchangeItemCatalog
{
    private static readonly string[] StructuralPrefixes =
    [
        "ancient rune of ",
        "greater rune of ",
        "rune of ",
        "greater orb of ",
        "orb of ",
        "uncut skill gem level ",
        "uncut spirit gem level ",
        "uncut support gem level ",
    ];

    private readonly IReadOnlyDictionary<string, PriceEntry> _prices;
    private readonly string[] _names;
    private readonly Dictionary<string, string[]> _prefixIndex;

    internal static ExchangeItemCatalog Empty { get; } =
        new(new ReadOnlyDictionary<string, PriceEntry>(new Dictionary<string, PriceEntry>()));

    internal ExchangeItemCatalog(IReadOnlyDictionary<string, PriceEntry> prices)
    {
        _prices = prices;
        _names = prices.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        _prefixIndex = BuildPrefixIndex(_names);
    }

    internal IReadOnlyList<string> KnownNames => _names;
    internal int Count => _names.Length;

    internal bool TryGetPrice(string key, out PriceEntry entry) => _prices.TryGetValue(key, out entry!);

    internal string[] KeysWithPrefix(string prefix) =>
        _prefixIndex.TryGetValue(prefix, out var keys) ? keys : [];

    internal IEnumerable<string> CandidateKeysFor(string name)
    {
        foreach (var prefix in StructuralPrefixes.OrderByDescending(p => p.Length))
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            foreach (var key in KeysWithPrefix(prefix))
                yield return key;
            yield break;
        }

        var first = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is { Length: >= 4 })
        {
            int yielded = 0;
            foreach (var key in _names)
            {
                if (!key.StartsWith(first + " ", StringComparison.Ordinal) &&
                    !key.Equals(first, StringComparison.Ordinal)) continue;
                yield return key;
                if (++yielded >= 80) yield break;
            }
            if (yielded > 0) yield break;
        }

        foreach (var key in _names)
            yield return key;
    }

    private static Dictionary<string, string[]> BuildPrefixIndex(string[] names)
    {
        var dict = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var prefix in StructuralPrefixes)
        {
            var list = names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (list.Length > 0) dict[prefix] = list;
        }
        return dict;
    }
}
