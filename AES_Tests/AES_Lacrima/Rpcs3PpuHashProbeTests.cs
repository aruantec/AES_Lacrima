using AES_Lacrima.Services.Rpcs3;

namespace AES_Tests.AES_Lacrima;

public sealed class Rpcs3PpuHashProbeTests
{
    [Fact]
    public void TryParsePpuHashFromLogText_FindsHashAfterMatchingBoot()
    {
        const string log = """
            ·! 0:00:25.371094 SYS: Emulator::BootGame: path='F:/Games/Resident Evil 0 [NPEB02226]/PS3_GAME', title_id='NPEB02226', direct=0
            ·W 0:00:25.620260 ppu_loader: PPU executable hash: PPU-dad3dd5ab44e07205beb6c6fef2be189b8f830b1
            ·! 0:00:40.000000 SYS: Emulator::BootGame: path='F:/Games/Other [BLUS30443]/PS3_GAME', title_id='BLUS30443', direct=0
            ·W 0:00:40.500000 ppu_loader: PPU executable hash: PPU-83681f6110d33442329073b72b8dc88a2f677172
            """;

        var hash = Rpcs3PpuHashProbeService.TryParsePpuHashFromLogText(log, "NPEB02226");

        Assert.Equal("PPU-dad3dd5ab44e07205beb6c6fef2be189b8f830b1", hash);
    }

    [Fact]
    public void TryResolvePrimaryPpuHash_PrefersLogOverImportedCheats()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var importedPath = Rpcs3PatchesService.GetPatchYmlPath(tempRoot, Rpcs3PatchCatalog.Imported);
            Directory.CreateDirectory(Path.GetDirectoryName(importedPath)!);

            File.WriteAllText(importedPath, """
                Version: 1.2
                PPU-wronghash000000000000000000000000000000000000:
                  "Infinite Ammo":
                    Games:
                      "Resident Evil 0 (Artemis)":
                        NPEB02226: [ All ]
                    Patch:
                      - [ be32, 0x001539A0, 0x60000000 ]
                """);

            File.WriteAllText(Path.Combine(tempRoot, "RPCS3.log"), """
                ·! 0:00:25.371094 SYS: Emulator::BootGame: title_id='NPEB02226', direct=0
                ·W 0:00:25.620260 ppu_loader: PPU executable hash: PPU-dad3dd5ab44e07205beb6c6fef2be189b8f830b1
                """);

            var hash = Rpcs3PatchesService.TryResolvePrimaryPpuHash(tempRoot, "NPEB02226");

            Assert.Equal("PPU-dad3dd5ab44e07205beb6c6fef2be189b8f830b1", hash);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TryResolvePrimaryPpuHash_PrefersOfficialPatchWhenLogMissing()
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
                PPU-officialhash000000000000000000000000000000000:
                  "Unlock FPS":
                    Games:
                      "Resident Evil 0":
                        NPEB02226: [ 01.00 ]
                    Patch:
                      - [ be32, 0x00000000, 0x00000001 ]
                """);

            File.WriteAllText(importedPath, """
                Version: 1.2
                PPU-wronghash000000000000000000000000000000000000:
                  "Infinite Ammo":
                    Games:
                      "Resident Evil 0 (Artemis)":
                        NPEB02226: [ All ]
                    Patch:
                      - [ be32, 0x001539A0, 0x60000000 ]
                """);

            var hash = Rpcs3PatchesService.TryResolvePrimaryPpuHash(tempRoot, "NPEB02226");

            Assert.Equal("PPU-officialhash000000000000000000000000000000000", hash);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TryResolvePrimaryPpuHash_ExcludesImportedWhenRequested()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var importedPath = Rpcs3PatchesService.GetPatchYmlPath(tempRoot, Rpcs3PatchCatalog.Imported);
            Directory.CreateDirectory(Path.GetDirectoryName(importedPath)!);

            File.WriteAllText(importedPath, """
                Version: 1.2
                PPU-wronghash000000000000000000000000000000000000:
                  "Infinite Ammo":
                    Games:
                      "Resident Evil 0 (Artemis)":
                        NPEB02226: [ All ]
                    Patch:
                      - [ be32, 0x001539A0, 0x60000000 ]
                """);

            var hash = Rpcs3PatchesService.TryResolvePrimaryPpuHash(
                tempRoot,
                "NPEB02226",
                includeUserPatchCatalogs: false);

            Assert.Null(hash);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
