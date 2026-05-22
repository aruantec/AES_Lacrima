using AES_Lacrima.Services.Cemu;

namespace AES_Tests.AES_Lacrima;

public sealed class CemuTitleIdHelperTests
{
    [Fact]
    public void NormalizeDisplayTitleId_FormatsHexPairs()
    {
        var result = CemuTitleIdHelper.NormalizeDisplayTitleId("000500001010ec00");
        Assert.Equal("00050000-1010EC00", result);
    }

    [Fact]
    public void MatchesTitleId_UsesPackTitleIds()
    {
        var matches = CemuTitleIdHelper.MatchesTitleId(
            ["000500001010ec00", "000500001010ed00"],
            "00050000-1010ED00");

        Assert.True(matches);
    }
}
