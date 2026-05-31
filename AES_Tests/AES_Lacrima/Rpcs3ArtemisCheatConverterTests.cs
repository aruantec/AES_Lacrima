using AES_Lacrima.Services.Rpcs3;

namespace AES_Tests.AES_Lacrima;

public sealed class Rpcs3ArtemisCheatConverterTests
{
    private const string SampleRawCheats = """
        Memory Infinite Ammo + No Reload
        0
        jgduff1
        0 326D5FAE 000D
        #
        ASM Infinite Ammo + No Reload
        0
        jgduff1
        0 00153524 60000000
        #
        Infinite Ammo
        0
        jgduff1
        0 001539A0 60000000
        0 00153998 7D264810
        0 002CEADC 60000000
        #
        """;

    [Fact]
    public void ParseRawCheats_ParsesMultipleBlocks()
    {
        var cheats = Rpcs3ArtemisCheatConverter.ParseRawCheats(SampleRawCheats);

        Assert.Equal(3, cheats.Count);
        Assert.Equal("Memory Infinite Ammo + No Reload", cheats[0].Name);
        Assert.Equal("jgduff1", cheats[0].Author);
        Assert.Single(cheats[0].Lines);
        Assert.Equal(0x326D5FAEu, cheats[0].Lines[0].Address);
        Assert.Equal(0x000Du, cheats[0].Lines[0].Value);

        Assert.Equal("Infinite Ammo", cheats[2].Name);
        Assert.Equal(3, cheats[2].Lines.Count);
    }

    [Fact]
    public void BuildYamlChunk_WritesRpcs3PatchEntries()
    {
        var cheats = Rpcs3ArtemisCheatConverter.ParseRawCheats(SampleRawCheats);
        var yaml = Rpcs3ArtemisCheatConverter.BuildYamlChunk(
            cheats.Take(1).ToArray(),
            "PPU-deadbeeffeedfacecafebabecafebabe",
            "BLUS30443",
            "Resident Evil",
            "01.00");

        Assert.Contains("PPU-deadbeeffeedfacecafebabecafebabe:", yaml, StringComparison.Ordinal);
        Assert.Contains("\"Memory Infinite Ammo + No Reload\":", yaml, StringComparison.Ordinal);
        Assert.Contains("\"Resident Evil (Artemis)\":", yaml, StringComparison.Ordinal);
        Assert.Contains("BLUS30443: [ 01.00 ]", yaml, StringComparison.Ordinal);
        Assert.Contains("Author: \"jgduff1\"", yaml, StringComparison.Ordinal);
        Assert.Contains("[ be32, 0x326D5FAE, 0x0000000D ]", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportRawCheats_WritesToImportedPatchFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var importedPath = Rpcs3PatchesService.GetPatchYmlPath(tempRoot, Rpcs3PatchCatalog.Imported);
            Directory.CreateDirectory(Path.GetDirectoryName(importedPath)!);

            File.WriteAllText(importedPath, """
                Version: 1.2
                PPU-existinghash:
                  "Existing Cheat":
                    Games:
                      "Resident Evil (Artemis)":
                        BLUS30443: [ 01.00 ]
                    Patch:
                      - [ be32, 0x00000001, 0x00000001 ]
                """);

            var result = Rpcs3ArtemisCheatImportService.ImportRawCheats(
                tempRoot,
                "BLUS30443",
                "Resident Evil",
                "01.00",
                SampleRawCheats,
                "PPU-existinghash");

            Assert.True(result.Success);
            Assert.Equal(3, result.CheatsImported);
            Assert.True(File.Exists(importedPath));
            Assert.False(File.Exists(Rpcs3PatchesService.GetPatchYmlPath(tempRoot, Rpcs3PatchCatalog.ArtemisCheats)));

            var loaded = Rpcs3PatchesService.GetPatchesForTitleId(
                tempRoot,
                "BLUS30443",
                catalog: Rpcs3PatchCatalog.ArtemisCheats);

            Assert.Contains(loaded, patch => patch.Name == "Memory Infinite Ammo + No Reload");
            Assert.Contains(loaded, patch => patch.Name == "ASM Infinite Ammo + No Reload");
            Assert.Contains(loaded, patch => patch.Name == "Infinite Ammo");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
