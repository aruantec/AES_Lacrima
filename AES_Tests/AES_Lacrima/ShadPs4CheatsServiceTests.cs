using AES_Lacrima.Services.ShadPs4;

namespace AES_Tests.AES_Lacrima;

public class ShadPs4CheatsServiceTests
{
    [Fact]
    public void FindCheatFiles_MatchesTitleIdAndVersionPatterns()
    {
        var root = Path.Combine(Path.GetTempPath(), "aes_shadps4_cheats_" + Guid.NewGuid().ToString("N"));
        var cheatsDir = Path.Combine(root, "user", "cheats");
        Directory.CreateDirectory(cheatsDir);

        File.WriteAllText(Path.Combine(cheatsDir, "CUSA07023_01.03.json"), "{}");
        File.WriteAllText(Path.Combine(cheatsDir, "CUSA07023_01.03_2.json"), "{}");
        File.WriteAllText(Path.Combine(cheatsDir, "CUSA99999_02.00.json"), "{}");

        try
        {
            var matches = ShadPs4CheatsService.FindCheatFiles(root, "CUSA07023", "01.03");
            Assert.Equal(2, matches.Count);
            Assert.Contains(matches, item => string.Equals(item.FileName, "CUSA07023_01.03.json", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(matches, item => string.Equals(item.FileName, "CUSA07023_01.03_2.json", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadCheatDocument_ParsesMods()
    {
        var path = Path.Combine(Path.GetTempPath(), "aes_shadps4_cheat_doc_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path,
            """
            {
              "name": "Sonic Mania",
              "id": "CUSA07023",
              "version": "01.03",
              "mods": [
                {
                  "name": "Infinite Rings",
                  "type": "checkbox",
                  "memory": [
                    { "offset": "450759", "on": "01", "off": "00" }
                  ]
                }
              ],
              "credits": [ "Talixme" ]
            }
            """);

        try
        {
            var document = ShadPs4CheatsService.LoadCheatDocument(path);
            Assert.NotNull(document);
            Assert.Equal("CUSA07023", document!.Id);
            Assert.Single(document.Mods);
            Assert.Equal("Infinite Rings", document.Mods[0].Name);
            Assert.Equal("checkbox", document.Mods[0].Type);
            Assert.Single(document.Mods[0].Memory);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
