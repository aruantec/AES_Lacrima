using AES_Lacrima.Services.ShadPs4;

using log4net;
using AES_Core.Logging;
namespace AES_Tests.AES_Lacrima;

public class ShadPs4ContentDownloadServiceTests
{
    private static readonly ILog Log = LogHelper.For<ShadPs4ContentDownloadServiceTests>();
    [Fact]
    public void GetPatchesRepositoryDirectory_UsesRepositoryFolderUnderUserPatches()
    {
        var path = ShadPs4ContentDownloadService.GetPatchesRepositoryDirectory(@"C:\shadPS4", "GoldHEN");
        Assert.Equal(Path.Combine(@"C:\shadPS4", "user", "patches", "GoldHEN"), path);
    }

    [Fact]
    public void CreatePatchFilesJson_WritesTitleIdsFromXml()
    {
        var root = Path.Combine(Path.GetTempPath(), "aes_shadps4_patches_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var xmlPath = Path.Combine(root, "Sample.xml");
        File.WriteAllText(xmlPath,
            """
            <Patch>
              <TitleID>
                <ID>CUSA12345</ID>
              </TitleID>
            </Patch>
            """);

        try
        {
            ShadPs4ContentDownloadService.CreatePatchFilesJson(root);
            var jsonPath = Path.Combine(root, "files.json");
            Assert.True(File.Exists(jsonPath));
            var json = File.ReadAllText(jsonPath);
            Assert.Contains("CUSA12345", json, StringComparison.Ordinal);
            Assert.Contains("Sample.xml", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }
    }

    [Fact]
    public void FindPatchFile_UsesFilesJsonMapping()
    {
        var emulatorDir = Path.Combine(Path.GetTempPath(), "aes_shadps4_emulator_" + Guid.NewGuid().ToString("N"));
        var repoDir = ShadPs4ContentDownloadService.GetPatchesRepositoryDirectory(emulatorDir, "shadPS4");
        Directory.CreateDirectory(repoDir);

        var xmlPath = Path.Combine(repoDir, "Game.xml");
        File.WriteAllText(xmlPath, "<Patch><TitleID><ID>CUSA99999</ID></TitleID></Patch>");
        File.WriteAllText(Path.Combine(repoDir, "files.json"), """{"Game.xml":["CUSA99999"]}""");

        try
        {
            var match = ShadPs4PatchesService.FindPatchFile(emulatorDir, "CUSA99999");
            Assert.NotNull(match);
            Assert.Equal(xmlPath, match!.FilePath);
        }
        finally
        {
            try
            {
                Directory.Delete(emulatorDir, true);
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }
    }
}
