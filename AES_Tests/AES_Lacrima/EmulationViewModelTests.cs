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
        Assert.True(map.ContainsKey("test-console.png"));

        var pendingField = typeof(EmulationViewModel).GetField("_pendingAlbumRoms", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pendingField);
        pendingField!.SetValue(vm, map);

        var restoreMethod = typeof(EmulationViewModel).GetMethod("RestoreAlbumRoms", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(restoreMethod);

        var restored = Assert.IsType<AvaloniaList<MediaItem>>(restoreMethod!.Invoke(vm, new object[] { "test-console.png", "Test Console", null })!);
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
}
