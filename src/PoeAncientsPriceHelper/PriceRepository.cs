using System.Collections.ObjectModel;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PoeAncientsPriceHelper;

// DivineValue  = price in divine orbs (primaryValue from API)
// ExaltedValue = DivineValue * core.rates.exalted (computed, for display when < 1 divine)
internal sealed record PriceEntry(decimal DivineValue, decimal ExaltedValue);

internal sealed class PriceRepository : IDisposable
{
    private readonly HttpClient _http;
    private volatile IReadOnlyDictionary<string, PriceEntry> _prices =
        new ReadOnlyDictionary<string, PriceEntry>(new Dictionary<string, PriceEntry>());
    private volatile ExchangeItemCatalog _catalog = ExchangeItemCatalog.Empty;
    private System.Threading.Timer? _timer;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _fetching;

    public IReadOnlyDictionary<string, PriceEntry> Prices => _prices;
    public ExchangeItemCatalog Catalog => _catalog;
    public DateTime? LastFetchedAt { get; private set; }
    public int ItemCount => _prices.Count;
    public double? LastFetchDurationMs { get; private set; }
    public bool IsFetching => _fetching;
    public bool IsFromCache { get; private set; }

    public event Action? PricesUpdated;

    private static readonly string[] ExchangeTypes = ["Verisium", "Runes", "Expedition", "Currency", "UncutGems"];

    public PriceRepository(HttpClient http) => _http = http;

    public bool TryLoadFromCache(AppConfig config)
    {
        var cache = PriceCacheStore.Load(config.LeagueName);
        if (cache is null) return false;

        var dict = PriceCacheStore.ToDictionary(cache);
        ApplyCustomOverride(dict, config.CustomPricesPath);
        PublishPrices(dict, cache.FetchedAt, fromCache: true);
        return true;
    }

    public async Task RefreshAsync(AppConfig config)
    {
        await FetchAndMergeAsync(config, _cts.Token);
    }

    public async Task InitialFetchAsync(AppConfig config) =>
        await RefreshAsync(config);

    public void ConfigureAutoRefresh(AppConfig config)
    {
        _timer?.Dispose();
        _timer = null;
        if (!config.AutoRefreshPrices || config.AutoRefreshIntervalMinutes <= 0) return;

        var interval = TimeSpan.FromMinutes(config.AutoRefreshIntervalMinutes);
        _timer = new System.Threading.Timer(
            _ => Task.Run(() => FetchAndMergeAsync(config, _cts.Token)),
            null, interval, interval);
    }

    private async Task FetchAndMergeAsync(AppConfig config, CancellationToken ct)
    {
        _fetching = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var dict = new Dictionary<string, PriceEntry>();
            foreach (var type in ExchangeTypes)
            {
                var entries = await FetchTypeAsync(config.LeagueName, type, ct);
                foreach (var (name, entry) in entries)
                    dict[name] = entry;
            }
            ApplyCustomOverride(dict, config.CustomPricesPath);
            var fetchedAt = DateTime.Now;
            PublishPrices(dict, fetchedAt, fromCache: false);
            LastFetchDurationMs = sw.Elapsed.TotalMilliseconds;
            PriceCacheStore.Save(config.LeagueName, _prices, fetchedAt);
            PricesUpdated?.Invoke();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PriceRepository] fetch failed: {ex.Message}");
        }
        finally
        {
            _fetching = false;
        }
    }

    private void PublishPrices(Dictionary<string, PriceEntry> dict, DateTime fetchedAt, bool fromCache)
    {
        _prices = new ReadOnlyDictionary<string, PriceEntry>(dict);
        _catalog = new ExchangeItemCatalog(_prices);
        LastFetchedAt = fetchedAt;
        IsFromCache = fromCache;
    }

    private async Task<Dictionary<string, PriceEntry>> FetchTypeAsync(string league, string type, CancellationToken ct)
    {
        var slug = league.Replace(" ", "").ToLowerInvariant();
        var typeSlug = type.ToLowerInvariant();
        var url = $"https://poe.ninja/poe2/api/economy/exchange/current/overview?league={Uri.EscapeDataString(league)}&type={type}";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Referer",
            $"https://poe.ninja/poe2/economy/{slug}/{typeSlug}");

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"[PriceRepository] {type}: HTTP {(int)resp.StatusCode}");
            return [];
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseResponse(json);
    }

    private static Dictionary<string, PriceEntry> ParseResponse(string json)
    {
        var result = new Dictionary<string, PriceEntry>();
        try
        {
            var obj = JObject.Parse(json);

            var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (obj["items"] is JArray itemsArr)
                foreach (var item in itemsArr)
                {
                    var id = item["id"]?.Value<string>();
                    var name = item["name"]?.Value<string>();
                    if (id is not null && name is not null) nameMap[id] = name;
                }

            var core = obj["core"];
            var primary = core?["primary"]?.Value<string>() ?? "divine";
            var rates = core?["rates"];
            var divinePerPrimary = primary == "divine" ? 1m : rates?["divine"]?.Value<decimal>() ?? 0m;
            var exaltedPerPrimary = primary == "exalted" ? 1m : rates?["exalted"]?.Value<decimal>() ?? 1m;

            if (obj["lines"] is not JArray lines) return result;
            foreach (var line in lines)
            {
                var id = line["id"]?.Value<string>();
                if (id is null || !nameMap.TryGetValue(id, out var name)) continue;
                var primaryValue = line["primaryValue"]?.Value<decimal>() ?? 0m;
                var divineValue = primaryValue * divinePerPrimary;
                var exaltedValue = Math.Round(primaryValue * exaltedPerPrimary, 1);
                var key = NormalizeName(name);
                if (!string.IsNullOrEmpty(key))
                    result[key] = new PriceEntry(divineValue, exaltedValue);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PriceRepository] parse failed: {ex.Message}");
        }
        return result;
    }

    private static void ApplyCustomOverride(Dictionary<string, PriceEntry> dict, string path)
    {
        try
        {
            var fullPath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, path);
            if (!File.Exists(fullPath)) return;
            var json = File.ReadAllText(fullPath);
            var overrides = JsonConvert.DeserializeObject<Dictionary<string, CustomPriceEntry>>(json);
            if (overrides is null) return;
            foreach (var (rawKey, entry) in overrides)
            {
                var key = NormalizeName(rawKey);
                if (!string.IsNullOrEmpty(key))
                    dict[key] = new PriceEntry(entry.DivineValue, entry.ExaltedValue);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PriceRepository] custom override failed: {ex.Message}");
        }
    }

    internal static string NormalizeName(string name) => NameNormalizer.Normalize(name);

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _timer = null;
        _cts.Dispose();
    }

    private sealed class CustomPriceEntry
    {
        public decimal DivineValue { get; set; }
        public decimal ExaltedValue { get; set; }
    }
}
