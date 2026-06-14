using AES_Lacrima.Services.Dolphin;

namespace AES_Lacrima.Tests;

public sealed class DolphinGameIniServiceTests
{
    [Fact]
    public void BuildGeckoDownloadCandidateIds_IncludesRegionalVariants()
    {
        var candidates = DolphinGameIniService.BuildGeckoDownloadCandidateIds("GF7P01").ToList();

        Assert.Equal("GF7P01", candidates[0]);
        Assert.Contains("GF7E01", candidates);
        Assert.Contains("GF7J01", candidates);
    }

    [Fact]
    public void BuildGeckoDownloadCandidateIds_DeduplicatesPrimaryRegion()
    {
        var candidates = DolphinGameIniService.BuildGeckoDownloadCandidateIds("GZLE01").ToList();

        Assert.Equal("GZLE01", candidates[0]);
        Assert.Equal(1, candidates.Count(id => string.Equals(id, "GZLE01", StringComparison.OrdinalIgnoreCase)));
    }
}
