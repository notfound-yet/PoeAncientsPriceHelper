using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class NameNormalizerTests
{
    [Theory]
    [InlineData("Support: Scattering Flame", "support scattering flame")]
    [InlineData("Lesser Jeweller's Orb", "lesser jeweller s orb")]
    [InlineData("  VERISIUM FLUX  ", "verisium flux")]
    public void Normalize_ProducesExpectedKey(string input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("1leune of the prism", "rune of the prism")]
    [InlineData("leune of confrontation", "rune of confrontation")]
    public void FixOcrArtifacts_RepairsRuneMisreads(string input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.FixOcrArtifacts(input));
    }

    [Theory]
    [InlineData("lesser jeweller sfl or l t", "lesser jeweller s orb")]
    [InlineData("greater jeweller sfl orb", "greater jeweller s orb")]
    [InlineData("lesser jeweller s orb", "lesser jeweller s orb")]
    [InlineData("vorana s saga", "vorana s saga")]
    [InlineData("greater orb of augmentation", "greater orb of augmentation")]
    [InlineData("ancient rune of decay", "ancient rune of decay")]
    public void FixOcrArtifacts_RepairsCommonMisreads(string input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.FixOcrArtifacts(input));
    }

    [Theory]
    [InlineData("lesser jeweller sfl or l t", "lesser jeweller sfl")]
    [InlineData("greater orb of augmentation", "greater orb of augmentation")]
    public void SanitizeForMatch_DropsNoiseTokens(string input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.SanitizeForMatch(input));
    }
}

public class StringSimilarityTests
{
    [Fact]
    public void Lcs_VitalityMisread()
    {
        int lcs = StringSimilarity.LcsLength("vltalltyli", "vitality");
        double score = StringSimilarity.Score("vltalltyli", "vitality");
        Assert.True(lcs >= 6, $"lcs={lcs}");
        Assert.True(score >= 0.62, $"score={score}");
    }

    [Theory]
    [InlineData("vitality", "vltalltyli", true)]
    [InlineData("vision", "viswn", true)]
    [InlineData("vitality", "blossom", false)]
    public void Score_AbsorbsOrnateFontMisreads(string expected, string ocr, bool shouldMatch)
    {
        Assert.Equal(shouldMatch, StringSimilarity.Score(ocr, expected) >= 0.62);
    }
}

public class ItemNameMatcherTests
{
    private static ExchangeItemCatalog SampleCatalog() =>
        new(new Dictionary<string, PriceEntry>(SamplePrices()));

    private static IReadOnlyDictionary<string, PriceEntry> SamplePrices() =>
        new Dictionary<string, PriceEntry>
        {
            ["lesser jeweller s orb"] = new(0.1m, 1m),
            ["greater jeweller s orb"] = new(0.5m, 5m),
            ["greater orb of augmentation"] = new(0.2m, 2m),
            ["greater vision rune"] = new(1m, 10m),
            ["greater rebirth rune"] = new(1m, 10m),
            ["exalted orb"] = new(2m, 20m),
            ["rune of vitality"] = new(0.3m, 3m),
            ["rune of the blossom"] = new(1m, 10m),
            ["armourer s scrap"] = new(0.1m, 1m),
            ["vorana s saga"] = new(5m, 50m),
            ["greater exalted orb"] = new(0.5m, 5m),
            ["divine orb"] = new(1m, 10m),
            ["rune of the prism"] = new(0.8m, 8m),
        };

    [Theory]
    [InlineData("lesser jeweller s orb", "lesser jeweller s orb", true)]
    [InlineData("lesser jeweller sfl or l t", "lesser jeweller s orb", true)]
    [InlineData("greater viswn rune", "greater vision rune", false)]
    [InlineData("greater vision rune", "greater vision rune", true)]
    [InlineData("rune of vltalltyli", "rune of vitality", false)]
    [InlineData("rune of the blossom", "rune of the blossom", true)]
    public void TryMatch_ResolvesOcrToPriceKey(string ocr, string expectedKey, bool exact)
    {
        var prices = SampleCatalog();
        Assert.True(ItemNameMatcher.TryMatch(ocr, prices, out var key, out _, out var isExact));
        Assert.Equal(expectedKey, key);
        Assert.Equal(exact, isExact);
    }

    [Theory]
    [InlineData("vorana s saga", "vorana s saga", true)]
    [InlineData("greater exalted orb", "greater exalted orb", true)]
    [InlineData("divine orb", "divine orb", true)]
    [InlineData("1leune of the prism", "rune of the prism", true)]
    public void TryMatch_ExpeditionAndCurrencyItems(string ocr, string expectedKey, bool exact)
    {
        var prices = SampleCatalog();
        Assert.True(ItemNameMatcher.TryMatch(ocr, prices, out var key, out _, out var isExact));
        Assert.Equal(expectedKey, key);
        Assert.Equal(exact, isExact);
    }

    [Fact]
    public void TryMatch_DoesNotConfuseSimilarItems()
    {
        var prices = SampleCatalog();
        Assert.True(ItemNameMatcher.TryMatch("greater vision rune", prices, out var key, out _, out _));
        Assert.Equal("greater vision rune", key);

        Assert.True(ItemNameMatcher.TryMatch("greater rebirth rune", prices, out key, out _, out _));
        Assert.Equal("greater rebirth rune", key);
    }

    [Fact]
    public void TryMatch_UnknownItem_ReturnsFalse()
    {
        Assert.False(ItemNameMatcher.TryMatch("totally unknown item xyz", SampleCatalog(),
            out _, out _, out _));
    }
}
