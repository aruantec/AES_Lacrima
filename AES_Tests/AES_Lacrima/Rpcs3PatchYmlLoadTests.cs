using AES_Lacrima.Services.Rpcs3;
using YamlDotNet.RepresentationModel;

namespace AES_Tests.AES_Lacrima;

public sealed class Rpcs3PatchYmlLoadTests
{
    private const string UserPatchPath =
        @"C:\Users\Admin\AppData\Local\AES_Lacrima\Emulators\PS3\RPCS3\patches\patch.yml";

    [Fact]
    public void NormalizePatchYamlContent_MergesDuplicateRootKeys()
    {
        var normalized = Rpcs3PatchesService.NormalizePatchYamlContentForLoading("""
            Version: 1.2
            Anchors:
              first: &first
                "Game A":
                  BLUS00001: [ 01.00 ]
            PPU-testhash:
              "Patch A":
                Games:
                  "Game A":
                    BLUS00001: [ 01.00 ]
                Patch:
                  - [ be32, 0, 1 ]
            Anchors:
              second: &second
                "Game B":
                  BLES01227: [ All ]
            PPU-testhash:
              "Patch B":
                Games:
                  "Game B":
                    BLES01227: [ All ]
                Patch:
                  - [ be32, 0, 2 ]
            """);

        Assert.Single(normalized.Split('\n').Where(static line => line.StartsWith("Anchors:", StringComparison.Ordinal)));
        Assert.Single(normalized.Split('\n').Where(static line => line.StartsWith("PPU-testhash:", StringComparison.Ordinal)));
        Assert.Contains("Patch B", normalized, StringComparison.Ordinal);
        Assert.Contains("second: &second", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void LenientLoader_LoadsPatchWithDuplicatePpuHashBlocks()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var patchPath = Rpcs3PatchesService.GetPatchYmlPath(tempRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(patchPath)!);
            File.WriteAllText(patchPath, """
                Version: 1.2
                PPU-13e4d4244f118d4305110d0218be9e8fa3fa27a87:
                  "First Patch":
                    Games:
                      "Test":
                        BLES01227: [ All ]
                    Patch:
                      - [ be32, 0, 1 ]
                PPU-13e4d4244f118d4305110d0218be9e8fa3fa27a87:
                  "Second Patch":
                    Games:
                      "Test":
                        BLES01227: [ All ]
                    Patch:
                      - [ be32, 0, 2 ]
                """);

            var success = Rpcs3PatchYamlLoader.TryLoadRoot(patchPath, out var root, out var error);

            Assert.True(success, error);
            Assert.NotNull(root);
            var patches = Rpcs3PatchesService.GetPatchesForTitleId(tempRoot, "BLES01227");
            Assert.Equal(2, patches.Count);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LenientLoader_LoadsUserPatchFile_WithoutYamlErrors()
    {
        if (!File.Exists(UserPatchPath))
            return;

        var success = Rpcs3PatchYamlLoader.TryLoadRoot(UserPatchPath, out var root, out var error);

        Assert.True(success, error);
        Assert.NotNull(root);
    }

    [Fact]
    public void TryGetPatchesForTitleId_FindsAllAsuraPatches_InUserPatchFile()
    {
        if (!File.Exists(UserPatchPath))
            return;

        var emulatorRoot = Path.GetDirectoryName(Path.GetDirectoryName(UserPatchPath))!;
        var success = Rpcs3PatchesService.TryGetPatchesForTitleId(
            emulatorRoot,
            "BLES01227",
            appVersion: null,
            out var patches,
            out var errorMessage);

        Assert.True(success, errorMessage);
        Assert.True(patches.Count >= 8, $"Expected at least 8 patches, got {patches.Count}. {errorMessage}");

        var gameTitles = patches.Select(static p => p.GameTitle).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Contains(gameTitles, t => t.Contains("Asura", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryGetPatchesForTitleId_FindsBles01227_InUserPatchFile()
    {
        if (!File.Exists(UserPatchPath))
            return;

        var emulatorRoot = Path.GetDirectoryName(Path.GetDirectoryName(UserPatchPath))!;
        var success = Rpcs3PatchesService.TryGetPatchesForTitleId(
            emulatorRoot,
            "BLES01227",
            appVersion: null,
            out var patches,
            out var errorMessage);

        Assert.True(success, errorMessage);
        Assert.NotEmpty(patches);
        Assert.Contains(patches, p => p.Name == "Unlock FPS");
    }
}
