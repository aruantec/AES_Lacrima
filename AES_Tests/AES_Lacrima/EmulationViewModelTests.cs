using System.Reflection;
using System.Diagnostics;
using Avalonia.Collections;
using AES_Controls.Player.Models;
using AES_Emulation.Controls;
using AES_Emulation.EmulationHandlers;
using AES_Lacrima.Services;
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

        var method = typeof(EmulationViewModel)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == "ScanFolderForRomPaths" && m.GetParameters().Length == 2);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object?[] { tempDir.Path, new string[] { "*.wud" } });
        Assert.NotNull(result);

        var paths = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<string>>(result);
        Assert.Contains(wiiuDir, paths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanFolderForRomPaths_Ps4InstalledGameDirectory_ReturnsFolderPath()
    {
        using var tempDir = new TempDirectory();
        var ps4Dir = Path.Combine(tempDir.Path, "CUSA00001");
        Directory.CreateDirectory(ps4Dir);
        Directory.CreateDirectory(Path.Combine(ps4Dir, "sce_sys"));
        File.WriteAllText(Path.Combine(ps4Dir, "eboot.bin"), "test");
        File.WriteAllText(Path.Combine(ps4Dir, "sce_sys", "icon0.png"), "png");

        var method = typeof(EmulationViewModel)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == "ScanFolderForRomPaths" && m.GetParameters().Length == 3);

        var result = method.Invoke(null, new object?[] { tempDir.Path, "PlayStation 4", new string[] { "*.pkg" } });
        Assert.NotNull(result);

        var paths = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<string>>(result);
        Assert.Contains(ps4Dir, paths, StringComparer.OrdinalIgnoreCase);
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

    [Fact]
    public void GetPreferredIconPath_PrefersSceSysIconForPs4InstalledGame()
    {
        using var tempDir = new TempDirectory();
        var ps4Dir = Path.Combine(tempDir.Path, "CUSA00002");
        var sceSysDir = Path.Combine(ps4Dir, "sce_sys");
        Directory.CreateDirectory(sceSysDir);
        File.WriteAllText(Path.Combine(ps4Dir, "eboot.bin"), "test");

        var rootIcon = Path.Combine(ps4Dir, "icon0.png");
        var sceSysIcon = Path.Combine(sceSysDir, "icon0.png");
        File.WriteAllText(rootIcon, "root");
        File.WriteAllText(sceSysIcon, "sce_sys");

        var iconPath = Ps4InstalledGameHelper.GetPreferredIconPath(ps4Dir);

        Assert.Equal(sceSysIcon, iconPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmulatorHandlerRegistry_PlayStation4_IncludesShadPs4Handler()
    {
        var handlers = EmulatorHandlerRegistry.GetHandlersForSection("PlayStation 4");

        Assert.Contains(handlers, handler => string.Equals(handler.HandlerId, "shadps4-qtlauncher", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShadPs4Handler_BuildStartInfo_UsesQtLauncherDefaultVersionAndGamePath()
    {
        var tempExe = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".exe");
        File.WriteAllText(tempExe, string.Empty);

        try
        {
            var gamePath = @"C:\Games\PS4\CUSA00900";
            ProcessStartInfo startInfo = ShadPs4Handler.Instance.BuildStartInfo(tempExe, gamePath, false);

            Assert.Equal(tempExe, startInfo.FileName);
            Assert.Equal(["-d", "-g", gamePath], startInfo.ArgumentList);
        }
        finally
        {
            try
            {
                File.Delete(tempExe);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ShadPs4Handler_PrefersDirectCompositionCapture()
    {
        Assert.Equal(EmulatorCaptureMode.DirectComposition, ShadPs4Handler.Instance.PreferredCaptureMode);
        Assert.True(ShadPs4Handler.Instance.ForceUseTargetClientAreaCapture);
        Assert.True(ShadPs4Handler.Instance.IsWindowEmbeddingSupported);
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
