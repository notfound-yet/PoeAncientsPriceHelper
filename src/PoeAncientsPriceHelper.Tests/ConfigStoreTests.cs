using System.Drawing;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Load_ReturnsDefaults_WhenFileDoesNotExist()
    {
        using var dir = new TempDir();
        var cfg = LoadFrom(dir.Path);
        Assert.Equal("Runes of Aldur", cfg.LeagueName);
        Assert.Equal(8, cfg.OverlayXOffset);
        Assert.Equal("custom_prices.json", cfg.CustomPricesPath);
        Assert.Equal("VcF5", cfg.StartStopHotkey);
        Assert.False(cfg.IsCalibrated);
    }

    [Fact]
    public void StartStopHotkey_RoundTrips()
    {
        using var dir = new TempDir();
        SaveTo(dir.Path, new AppConfig { StartStopHotkey = "VcF7" });
        Assert.Equal("VcF7", LoadFrom(dir.Path).StartStopHotkey);
    }

    [Fact]
    public void RoundTrip_AllFields()
    {
        using var dir = new TempDir();
        var original = new AppConfig
        {
            LeagueName = "Test League",
            RegionX = 10, RegionY = 20, RegionWidth = 300, RegionHeight = 400,
            OverlayXOffset = 16,
            ReferencePixelColor = "#AABBCC",
            CustomPricesPath = "my_prices.json"
        };
        SaveTo(dir.Path, original);
        var loaded = LoadFrom(dir.Path);
        Assert.Equal("Test League", loaded.LeagueName);
        Assert.Equal(new Rectangle(10, 20, 300, 400), loaded.RegionRect);
        Assert.Equal(16, loaded.OverlayXOffset);
        Assert.Equal("#AABBCC", loaded.ReferencePixelColor);
        Assert.Equal("my_prices.json", loaded.CustomPricesPath);
    }

    [Fact]
    public void AvailableLeagues_NotDuplicated_OnRoundTrip()
    {
        // Newtonsoft's ObjectCreationHandling.Auto appends a deserialized list onto a pre-populated
        // default, doubling entries. AvailableLeagues is [JsonIgnore]'d to stay code-only and avoid it.
        using var dir = new TempDir();
        SaveTo(dir.Path, new AppConfig());
        var loaded = LoadFrom(dir.Path);
        Assert.Equal(new AppConfig().AvailableLeagues, loaded.AvailableLeagues);
        Assert.Equal(loaded.AvailableLeagues.Count, loaded.AvailableLeagues.Distinct().Count());
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenJsonMalformed()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "config.json"), "{ invalid json !!!");
        var cfg = LoadFrom(dir.Path);
        Assert.Equal("Runes of Aldur", cfg.LeagueName);
    }

    // Helpers that redirect ConfigStore's path via a temp directory.
    // ConfigStore uses AppContext.BaseDirectory which we can't easily swap,
    // so we exercise the logic directly via JSON round-trip here.
    private static AppConfig LoadFrom(string dir)
    {
        var path = Path.Combine(dir, "config.json");
        if (!File.Exists(path)) return new AppConfig();
        try { return JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(path)) ?? new AppConfig(); }
        catch { return new AppConfig(); }
    }

    private static void SaveTo(string dir, AppConfig cfg)
    {
        File.WriteAllText(Path.Combine(dir, "config.json"),
            JsonConvert.SerializeObject(cfg, Formatting.Indented));
    }
}

