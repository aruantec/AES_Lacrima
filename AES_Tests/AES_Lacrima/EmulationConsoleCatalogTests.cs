using AES_Lacrima.Services;

namespace AES_Lacrima.Tests;

public sealed class EmulationConsoleCatalogTests
{
    [Theory]
    [InlineData("ARCADE", "Arcade")]
    [InlineData("FBN", "Final Burn Neo")]
    [InlineData("SNES", "Super Nintendo")]
    [InlineData("PSX", "PlayStation")]
    [InlineData("GENESIS", "Sega Genesis")]
    [InlineData("WII", "Nintendo Wii")]
    public void SupportsArcadePillarboxRemoval_ReturnsTrue_ForEmulationSectionsWithPillarboxedGames(
        string sectionKey,
        string sectionTitle)
    {
        Assert.True(EmulationConsoleCatalog.SupportsArcadePillarboxRemoval(sectionKey, sectionTitle));
    }

    [Theory]
    [InlineData("STEAM", "Steam")]
    public void SupportsArcadePillarboxRemoval_ReturnsFalse_ForSteamSection(
        string sectionKey,
        string sectionTitle)
    {
        Assert.False(EmulationConsoleCatalog.SupportsArcadePillarboxRemoval(sectionKey, sectionTitle));
    }

    [Theory]
    [InlineData("ARCADE", "Arcade")]
    [InlineData("FBN", "Final Burn Neo")]
    public void DefaultsAggressivePillarboxRemoval_ReturnsTrue_ForArcadeStyleSections(
        string sectionKey,
        string sectionTitle)
    {
        Assert.True(EmulationConsoleCatalog.DefaultsAggressivePillarboxRemoval(sectionKey, sectionTitle));
    }

    [Theory]
    [InlineData("SNES", "Super Nintendo")]
    [InlineData("PS2", "PlayStation 2")]
    public void DefaultsAggressivePillarboxRemoval_ReturnsFalse_ForOtherSections(
        string sectionKey,
        string sectionTitle)
    {
        Assert.False(EmulationConsoleCatalog.DefaultsAggressivePillarboxRemoval(sectionKey, sectionTitle));
    }

    [Theory]
    [InlineData("ARCADE", "Arcade")]
    [InlineData("FBN", "Final Burn Neo")]
    public void IsArcadeStyleSection_ReturnsTrue_ForArcadeAndFbNeo(
        string sectionKey,
        string sectionTitle)
    {
        Assert.True(EmulationConsoleCatalog.IsArcadeStyleSection(sectionKey, sectionTitle));
    }

    [Theory]
    [InlineData("SNES", "Super Nintendo")]
    public void IsArcadeStyleSection_ReturnsFalse_ForOtherSections(
        string sectionKey,
        string sectionTitle)
    {
        Assert.False(EmulationConsoleCatalog.IsArcadeStyleSection(sectionKey, sectionTitle));
    }
}
