using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class OcrRowFiltersTests
{
    [Theory]
    [InlineData("runeshape combinations", true)]
    [InlineData("combinations", true)]
    [InlineData("vorana s saga", false)]
    [InlineData("greater exalted orb", false)]
    public void IsPanelChrome_IdentifiesTitleLines(string name, bool chrome)
    {
        Assert.Equal(chrome, OcrRowFilters.IsPanelChrome(name));
    }

    [Fact]
    public void LooksLikeReward_AcceptsItemRows()
    {
        Assert.True(OcrRowFilters.LooksLikeReward("vorana s saga"));
        Assert.True(OcrRowFilters.LooksLikeReward("greater exalted orb"));
        Assert.False(OcrRowFilters.LooksLikeReward("runeshape combinations"));
    }
}
