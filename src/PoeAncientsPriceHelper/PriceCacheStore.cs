using Newtonsoft.Json;

namespace PoeAncientsPriceHelper;

internal sealed class PriceCacheFile
{
    public string League { get; set; } = "";
    public DateTime FetchedAt { get; set; }
    public Dictionary<string, CachedPriceEntry> Items { get; set; } = new();
}

internal sealed class CachedPriceEntry
{
    public decimal DivineValue { get; set; }
    public decimal ExaltedValue { get; set; }
}

internal static class PriceCacheStore
{
    private static string CacheDirectory =>
        Path.Combine(AppContext.BaseDirectory, "cache");

    internal static string CachePathForLeague(string league) =>
        Path.Combine(CacheDirectory, $"price_cache_{LeagueSlug(league)}.json");

    internal static string LeagueSlug(string league) =>
        Regex.Replace(league.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');

    internal static PriceCacheFile? Load(string league)
    {
        try
        {
            var path = CachePathForLeague(league);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var cache = JsonConvert.DeserializeObject<PriceCacheFile>(json);
            if (cache is null || cache.Items.Count == 0) return null;
            if (!string.Equals(cache.League, league, StringComparison.OrdinalIgnoreCase)) return null;
            return cache;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PriceCacheStore] load failed: {ex.Message}");
            return null;
        }
    }

    internal static void Save(string league, IReadOnlyDictionary<string, PriceEntry> prices, DateTime fetchedAt)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var cache = new PriceCacheFile
            {
                League = league,
                FetchedAt = fetchedAt,
                Items = prices.ToDictionary(
                    kv => kv.Key,
                    kv => new CachedPriceEntry
                    {
                        DivineValue = kv.Value.DivineValue,
                        ExaltedValue = kv.Value.ExaltedValue,
                    }),
            };
            var path = CachePathForLeague(league);
            File.WriteAllText(path, JsonConvert.SerializeObject(cache, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PriceCacheStore] save failed: {ex.Message}");
        }
    }

    internal static Dictionary<string, PriceEntry> ToDictionary(PriceCacheFile cache) =>
        cache.Items.ToDictionary(
            kv => kv.Key,
            kv => new PriceEntry(kv.Value.DivineValue, kv.Value.ExaltedValue));
}
