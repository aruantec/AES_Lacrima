using AES_Lacrima.Services.Rpcs3;

namespace AES_Tests.AES_Lacrima;

public sealed class Rpcs3PatchesServiceTests
{
    [Fact]
    public void GetPatchesForTitleId_MatchesSerialFromGamesBlock()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var patchPath = Rpcs3PatchesService.GetPatchYmlPath(tempRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(patchPath)!);
            File.WriteAllText(patchPath, """
                Version: 1.2
                Anchors:
                  des_US100title: &des_US100title
                    "Demon's Souls":
                      BLUS30443: [ 01.00 ]
                PPU-83681f6110d33442329073b72b8dc88a2f677172:
                  "Unlock FPS":
                    Games: *des_US100title
                    Author: "Whatcookie"
                    Notes: "Unlock frame rate"
                    Patch Version: 2.1
                    Patch:
                      - [ be32, 0x00000000, 0x00000001 ]
                """);

            var patches = Rpcs3PatchesService.GetPatchesForTitleId(tempRoot, "BLUS30443");

            Assert.Single(patches);
            Assert.Equal("Unlock FPS", patches[0].Name);
            Assert.Equal("BLUS30443", patches[0].Serial);
            Assert.Equal("01.00", patches[0].AppVersion);
            Assert.Equal("Whatcookie", patches[0].Author);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetPatchesForTitleId_MatchesBles01227_WithSecondaryAnchorsBlock()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var patchPath = Rpcs3PatchesService.GetPatchYmlPath(tempRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(patchPath)!);
            File.WriteAllText(patchPath, """
                Version: 1.2
                Anchors:
                  otherAnchor: &otherAnchor
                    "Other Game":
                      BLUS00000: [ 01.00 ]
                PPU-firsthash:
                  "Placeholder":
                    Games:
                      "Other":
                        BLUS00001: [ 01.00 ]
                    Patch:
                      - [ be32, 0x00000000, 0x00000001 ]
                Anchors:
                  asuraW_EUtitle: &asuraW_EUtitle
                    "Asura's Wrath":
                      BLES01227: [ All ]
                PPU-69f53b470d81ea961c1c2ff264ade6ab8077d2a1:
                  "Unlock FPS":
                    Games: *asuraW_EUtitle
                    Author: "Whatcookie, Mew21"
                    Patch Version: 1.0
                    Patch:
                      - [ load, 0x00000000 ]
                """);

            var patches = Rpcs3PatchesService.GetPatchesForTitleId(tempRoot, "BLES01227");

            Assert.NotEmpty(patches);
            Assert.Contains(patches, p => p.Name == "Unlock FPS" && p.Serial == "BLES01227");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveEmulatorDirectory_PrefersManagedFolderWhenLauncherIsInSubdirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var patchPath = Rpcs3PatchesService.GetPatchYmlPath(tempRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(patchPath)!);
            File.WriteAllText(patchPath, "Version: 1.2\n");

            var launcherDir = Path.Combine(tempRoot, "bin");
            Directory.CreateDirectory(launcherDir);
            var launcherPath = Path.Combine(launcherDir, "rpcs3.exe");
            File.WriteAllText(launcherPath, string.Empty);

            var resolved = Rpcs3PatchesService.ResolveEmulatorDirectory(preferredDirectory: null, launcherPath);

            Assert.Equal(tempRoot, resolved, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TryGetPatchesForTitleId_ReturnsErrorWhenPatchFileMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var success = Rpcs3PatchesService.TryGetPatchesForTitleId(
                tempRoot,
                "BLES01227",
                appVersion: null,
                out var patches,
                out var errorMessage);

            Assert.False(success);
            Assert.Empty(patches);
            Assert.Contains("patch.yml", errorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SaveEnabledStates_WritesPatchConfigEnabledFlag()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var patchPath = Rpcs3PatchesService.GetPatchYmlPath(tempRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(patchPath)!);
            File.WriteAllText(patchPath, """
                Version: 1.2
                PPU-testhash:
                  "Test Patch":
                    Games:
                      "Test Game":
                        BLUS99999: [ 01.00 ]
                    Patch:
                      - [ be32, 0x00000000, 0x00000001 ]
                """);

            var definition = Rpcs3PatchesService.GetPatchesForTitleId(tempRoot, "BLUS99999").Single();
            var entryKey = Rpcs3PatchesService.BuildEntryKey(definition);

            Rpcs3PatchesService.SaveEnabledStates(tempRoot, [
                new Rpcs3PatchToggle(
                    entryKey,
                    definition.PpuHash,
                    definition.Name,
                    definition.GameTitle,
                    definition.Serial,
                    definition.AppVersion,
                    true)
            ]);

            var configPath = Rpcs3PatchesService.GetPatchConfigPath(tempRoot);
            Assert.True(File.Exists(configPath));
            var text = File.ReadAllText(configPath);
            Assert.Contains("Enabled: true", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("BLUS99999", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("...", text, StringComparison.Ordinal);

            var enabledMap = Rpcs3PatchesService.BuildEnabledStateMap(
                [definition],
                configPath);

            Assert.True(enabledMap[entryKey]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
