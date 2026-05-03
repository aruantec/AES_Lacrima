using System.Reflection;
using Avalonia.Collections;
using AES_Controls.Player.Models;
using AES_Lacrima.ViewModels;

namespace AES_Lacrima.Tests;

public sealed class EmulationViewModelTests
{
    [Fact]
    public void ScanFolderForRomPaths_WiiUPackageDirectory_ReturnsFolderPath()
    {
        using var tempDir = new TempDirectory();
        var wiiuDir = Path.Combine(tempDir.Path, "SuperMario3DWorld");
        Directory.CreateDirectory(wiiuDir);
        Directory.CreateDirectory(Path.Combine(wiiuDir, "code"));
        Directory.CreateDirectory(Path.Combine(wiiuDir, "content"));
        Directory.CreateDirectory(Path.Combine(wiiuDir, "meta"));

        var method = typeof(EmulationViewModel).GetMethod("ScanFolderForRomPaths", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object?[] { tempDir.Path, new string[] { "*.wud" } });
        Assert.NotNull(result);

        var paths = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<string>>(result);
        Assert.Contains(wiiuDir, paths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanFolderForPs4RomPaths_ReturnsGameFolderAndIconCover()
    {
        using var tempDir = new TempDirectory();
        var gameFolder = Path.Combine(tempDir.Path, "CUSA12345");
        var ps4GameFolder = Path.Combine(gameFolder, "PS4_GAME");
        var usrDir = Path.Combine(ps4GameFolder, "USRDIR");
        var sceSysDir = Path.Combine(gameFolder, "sce_sys");

        Directory.CreateDirectory(usrDir);
        Directory.CreateDirectory(sceSysDir);

        File.WriteAllText(Path.Combine(usrDir, "eboot.bin"), "dummy");
        var iconPath = Path.Combine(sceSysDir, "icon0.png");
        File.WriteAllBytes(iconPath, TempDirectory.CreateTinyPngBytes());

        var scanMethod = typeof(EmulationViewModel).GetMethod("ScanFolderForPs4RomPaths", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(scanMethod);

        var scanResult = scanMethod!.Invoke(null, new object?[] { tempDir.Path });
        Assert.NotNull(scanResult);

        var paths = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<string>>(scanResult);
        Assert.Contains(gameFolder, paths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(tempDir.Path, paths, StringComparer.OrdinalIgnoreCase);

        var createMethod = typeof(EmulationViewModel).GetMethod("CreateRomItem", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(createMethod);

        var album = new EmulationAlbumItem { Title = "PlayStation 4" };
        var item = Assert.IsType<MediaItem>(createMethod!.Invoke(null, new object?[] { gameFolder, album })!);

        Assert.Equal(gameFolder, item.FileName);
        Assert.Equal(iconPath, item.LocalCoverPath);
        Assert.NotNull(item.CoverBitmap);
    }

    [Fact]
    public void ScanFolderForPs4RomPaths_PrefersSceSysIconOverOtherIcons()
    {
        using var tempDir = new TempDirectory();
        var gameFolder = Path.Combine(tempDir.Path, "CUSA54321");
        var ps4GameFolder = Path.Combine(gameFolder, "PS4_GAME");
        var usrDir = Path.Combine(ps4GameFolder, "USRDIR");
        var sceSysDir = Path.Combine(gameFolder, "sce_sys");
        var nestedArtDir = Path.Combine(ps4GameFolder, "media", "art");

        Directory.CreateDirectory(usrDir);
        Directory.CreateDirectory(sceSysDir);
        Directory.CreateDirectory(nestedArtDir);

        File.WriteAllText(Path.Combine(usrDir, "eboot.bin"), "dummy");
        var preferredIconPath = Path.Combine(sceSysDir, "icon0.png");
        var fallbackIconPath = Path.Combine(nestedArtDir, "icon0.png");
        File.WriteAllBytes(preferredIconPath, TempDirectory.CreateTinyPngBytes());
        File.WriteAllBytes(fallbackIconPath, TempDirectory.CreateTinyPngBytes());

        var createMethod = typeof(EmulationViewModel).GetMethod("CreateRomItem", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(createMethod);

        var album = new EmulationAlbumItem { Title = "PlayStation 4" };
        var item = Assert.IsType<MediaItem>(createMethod!.Invoke(null, new object?[] { gameFolder, album })!);

        Assert.Equal(preferredIconPath, item.LocalCoverPath);
        Assert.NotEqual(fallbackIconPath, item.LocalCoverPath);
    }

    [Fact]
    public void RestoreAlbumRoms_RehydratesPs4LocalCoverBitmap()
    {
        using var tempDir = new TempDirectory();
        var gameFolder = Path.Combine(tempDir.Path, "CUSA99999");
        var sceSysDir = Path.Combine(gameFolder, "sce_sys");
        Directory.CreateDirectory(sceSysDir);

        var iconPath = Path.Combine(sceSysDir, "icon0.png");
        File.WriteAllBytes(iconPath, TempDirectory.CreateTinyPngBytes());

        var vm = new EmulationViewModel();
        var savedItem = new MediaItem
        {
            FileName = gameFolder,
            Title = "Game",
            Album = "PlayStation 4",
            LocalCoverPath = iconPath
        };

        var pendingField = typeof(EmulationViewModel).GetField("_pendingAlbumRoms", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pendingField);
        pendingField!.SetValue(vm, new Dictionary<string, List<MediaItem>>(StringComparer.OrdinalIgnoreCase)
        {
            ["playstation 4"] = [savedItem]
        });

        var restoreMethod = typeof(EmulationViewModel).GetMethod("RestoreAlbumRoms", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(restoreMethod);

        var restored = Assert.IsType<AvaloniaList<MediaItem>>(restoreMethod!.Invoke(vm, new object?[] { "playstation 4", "PlayStation 4", null })!);
        Assert.Single(restored);
        Assert.Equal(iconPath, restored[0].LocalCoverPath);
        Assert.NotNull(restored[0].CoverBitmap);
    }

    [Fact]
    public void BuildAndRestoreAlbumRomMap_PersistsByImageFileName()
    {
        var vm = new EmulationViewModel();
        var album = new EmulationAlbumItem
        {
            Title = "Test Console",
            FileName = "C:\\Consoles\\test-console.png",
            Children = new AvaloniaList<MediaItem>
            {
                new MediaItem
                {
                    FileName = "C:\\Roms\\game1.nes",
                    Title = "Game 1",
                    Album = "Test Console"
                }
            }
        };
        vm.AlbumList.Add(album);

        var buildMethod = typeof(EmulationViewModel).GetMethod("BuildAlbumRomMap", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(buildMethod);

        var map = Assert.IsType<Dictionary<string, List<MediaItem>>>(buildMethod!.Invoke(vm, Array.Empty<object>())!);
        var expectedKey = "test-console.png";
        Assert.Contains(expectedKey, map.Keys, StringComparer.OrdinalIgnoreCase);

        var pendingField = typeof(EmulationViewModel).GetField("_pendingAlbumRoms", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pendingField);
        pendingField!.SetValue(vm, map);

        var restoreMethod = typeof(EmulationViewModel).GetMethod("RestoreAlbumRoms", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(restoreMethod);

        var restored = Assert.IsType<AvaloniaList<MediaItem>>(restoreMethod!.Invoke(vm, new object?[] { "test-console.png", "Test Console", null })!);
        Assert.Single(restored);
        Assert.Equal("C:\\Roms\\game1.nes", restored[0].FileName);
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch
        {
        }
    }

    public static byte[] CreateTinyPngBytes() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO1Nf5kAAAAASUVORK5CYII=");
}
