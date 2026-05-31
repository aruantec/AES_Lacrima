using AES_Lacrima.Services.Rpcs3;

namespace AES_Tests.AES_Lacrima;

public sealed class Rpcs3ArtemisCheatsTests
{
    [Fact]
    public void IsArtemisCheatPatch_DetectsArtemisGameTitleMarker()
    {
        var artemis = new Rpcs3PatchDefinition(
            "PPU-test",
            "Infinite HP",
            "Demons Souls (Artemis)",
            "BLUS30443",
            "01.00",
            null,
            null,
            null,
            null);

        var official = artemis with { GameTitle = "Demon's Souls" };

        Assert.True(Rpcs3PatchesService.IsArtemisCheatPatch(artemis));
        Assert.False(Rpcs3PatchesService.IsArtemisCheatPatch(official));
    }

    [Fact]
    public void TryGetPatchesForTitleId_ArtemisCatalog_ReadsImportedPatchFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var officialPath = Rpcs3PatchesService.GetPatchYmlPath(tempRoot, Rpcs3PatchCatalog.Official);
            var importedPath = Rpcs3PatchesService.GetPatchYmlPath(tempRoot, Rpcs3PatchCatalog.Imported);
            Directory.CreateDirectory(Path.GetDirectoryName(officialPath)!);

            File.WriteAllText(officialPath, """
                Version: 1.2
                PPU-officialhash:
                  "Unlock FPS":
                    Games:
                      "Demon's Souls":
                        BLUS30443: [ 01.00 ]
                    Patch:
                      - [ be32, 0x00000000, 0x00000001 ]
                """);

            File.WriteAllText(importedPath, """
                Version: 1.2
                PPU-artemishash:
                  "Infinite HP":
                    Games:
                      "Demons Souls (Artemis)":
                        BLUS30443: [ 01.00 ]
                    Patch:
                      - [ be32, 0x00000000, 0x00000002 ]
                """);

            var officialPatches = Rpcs3PatchesService.GetPatchesForTitleId(
                tempRoot,
                "BLUS30443",
                catalog: Rpcs3PatchCatalog.Official);
            var artemisPatches = Rpcs3PatchesService.GetPatchesForTitleId(
                tempRoot,
                "BLUS30443",
                catalog: Rpcs3PatchCatalog.ArtemisCheats);

            Assert.Single(officialPatches);
            Assert.Equal("Unlock FPS", officialPatches[0].Name);

            Assert.Single(artemisPatches);
            Assert.Equal("Infinite HP", artemisPatches[0].Name);
            Assert.True(Rpcs3PatchesService.IsArtemisCheatPatch(artemisPatches[0]));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void StripVersionHeader_RemovesLeadingVersionLine()
    {
        const string input = """
            Version: 1.2
            Anchors:
              test: &test
                "Game":
                  BLUS00000: [ 01.00 ]
            """;

        var stripped = Rpcs3ArtemisCheatsDownloadService.StripVersionHeader(input);

        Assert.DoesNotContain("Version:", stripped, StringComparison.Ordinal);
        Assert.Contains("Anchors:", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSourceFilesForTitleId_FindsDemonSoulsPack()
    {
        var files = await Rpcs3ArtemisCheatsDownloadService.FindSourceFilesForTitleIdAsync("BLUS30443");

        Assert.Contains(files, file => file.Contains("BLUS30443", StringComparison.OrdinalIgnoreCase));
    }
}
