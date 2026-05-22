using AES_Lacrima.Services.Cemu;

namespace AES_Tests.AES_Lacrima;

public sealed class CemuGraphicPacksServiceTests
{
    [Fact]
    public void TryGetGraphicPacksForTitleId_FindsPackForMatchingTitleId()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        var rulesDirectory = Path.Combine(tempRoot, "graphicPacks", "MarioKart8", "Graphics");
        Directory.CreateDirectory(rulesDirectory);

        var rulesPath = Path.Combine(rulesDirectory, "rules.txt");
        File.WriteAllText(rulesPath, """
            [Definition]
            titleIds = 000500001010ec00,000500001010ed00
            name = Graphic Options
            path = "Mario Kart 8/Graphics"
            description = Test pack
            version = 6
            """);

        var settingsPath = Path.Combine(tempRoot, "settings.xml");
        File.WriteAllText(settingsPath, """
            <cemu>
              <content>
                <GraphicPack>
                  <Entry filename="graphicPacks/MarioKart8/Graphics/rules.txt" disabled="false" />
                </GraphicPack>
              </content>
            </cemu>
            """);

        try
        {
            var success = CemuGraphicPacksService.TryGetGraphicPacksForTitleId(
                tempRoot,
                launcherPath: null,
                "00050000-1010EC00",
                out var packs,
                out var errorMessage);

            Assert.True(success, errorMessage);
            Assert.Single(packs);
            Assert.Equal("Graphic Options", packs[0].Name);
            Assert.True(packs[0].IsEnabled);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TryGetGraphicPacksForTitleId_LoadsResolutionPresets()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        var rulesDirectory = Path.Combine(tempRoot, "graphicPacks", "TestGame_Resolution");
        Directory.CreateDirectory(rulesDirectory);

        var rulesPath = Path.Combine(rulesDirectory, "rules.txt");
        File.WriteAllText(rulesPath, """
            [Definition]
            titleIds = 0005000010145C00
            name = Resolution
            path = "Test Game/Graphics/Resolution"
            version = 4

            [Preset]
            name = 1280x720 (Default)
            $width = 1280

            [Preset]
            name = 1920x1080
            $width = 1920
            """);

        File.WriteAllText(Path.Combine(tempRoot, "settings.xml"), """
            <cemu>
              <content>
                <GraphicPack>
                  <Entry filename="graphicPacks/TestGame_Resolution/rules.txt" disabled="false">
                    <Preset category="" preset="1920x1080" />
                  </Entry>
                </GraphicPack>
              </content>
            </cemu>
            """);

        try
        {
            var success = CemuGraphicPacksService.TryGetGraphicPacksForTitleId(
                tempRoot,
                launcherPath: null,
                "00050000-10145C00",
                out var packs,
                out var errorMessage);

            Assert.True(success, errorMessage);
            var resolution = Assert.Single(packs);
            Assert.Equal("Resolution", resolution.Name);
            Assert.Single(resolution.PresetGroups);
            Assert.Contains("1920x1080", resolution.PresetGroups[0].PresetNames);
            Assert.Equal("1920x1080", resolution.PresetGroups[0].SelectedPresetName);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SaveEnabledStates_WritesPresetSelectionToSettings()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        var rulesDirectory = Path.Combine(tempRoot, "graphicPacks", "TestGame_Resolution");
        Directory.CreateDirectory(rulesDirectory);

        File.WriteAllText(Path.Combine(rulesDirectory, "rules.txt"), """
            [Definition]
            titleIds = 0005000012345678
            name = Resolution
            version = 4

            [Preset]
            name = 1280x720 (Default)

            [Preset]
            name = 1920x1080
            """);

        File.WriteAllText(Path.Combine(tempRoot, "settings.xml"), """
            <cemu>
              <content>
                <GraphicPack />
              </content>
            </cemu>
            """);

        try
        {
            CemuGraphicPacksService.SaveEnabledStates(
                tempRoot,
                launcherPath: null,
                [
                    new CemuGraphicPackToggle(
                        "graphicPacks/TestGame_Resolution/rules.txt",
                        "graphicPacks/TestGame_Resolution/rules.txt",
                        true,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [string.Empty] = "1920x1080"
                        })
                ]);

            var settingsText = File.ReadAllText(Path.Combine(tempRoot, "settings.xml"));
            Assert.Contains("graphicPacks/TestGame_Resolution/rules.txt", settingsText, StringComparison.Ordinal);
            Assert.Contains("preset=\"1920x1080\"", settingsText, StringComparison.Ordinal);
            Assert.Contains("category=\"\"", settingsText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
