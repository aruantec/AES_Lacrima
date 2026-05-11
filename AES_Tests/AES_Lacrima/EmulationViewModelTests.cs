using System.Collections.Generic;
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
    public void ScanFolderForRomPaths_Ps3InstalledGameDirectory_ReturnsFolderPath()
    {
        using var tempDir = new TempDirectory();
        var ps3Dir = Path.Combine(tempDir.Path, "BLES00001");
        var ps3GameDir = Path.Combine(ps3Dir, "PS3_GAME");
        Directory.CreateDirectory(ps3GameDir);
        File.WriteAllText(Path.Combine(ps3GameDir, "ICON0.PNG"), "icon");
        File.WriteAllText(Path.Combine(ps3GameDir, "PIC1.PNG"), "back");

        var method = typeof(EmulationViewModel)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == "ScanFolderForRomPaths" && m.GetParameters().Length == 3);

        var result = method.Invoke(null, new object?[] { tempDir.Path, "PlayStation 3", new string[] { "*.iso" } });
        Assert.NotNull(result);

        var paths = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<string>>(result);
        Assert.Contains(ps3Dir, paths, StringComparer.OrdinalIgnoreCase);
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
    public void GetPreferredIconPath_PrefersPs3GameFolderArtwork()
    {
        using var tempDir = new TempDirectory();
        var ps3Dir = Path.Combine(tempDir.Path, "BLES00002");
        var ps3GameDir = Path.Combine(ps3Dir, "PS3_GAME");
        Directory.CreateDirectory(ps3GameDir);

        var iconPath = Path.Combine(ps3GameDir, "ICON0.PNG");
        var backCoverPath = Path.Combine(ps3GameDir, "PIC1.PNG");
        File.WriteAllText(iconPath, "icon");
        File.WriteAllText(backCoverPath, "back");

        Assert.Equal(iconPath, Ps3InstalledGameHelper.GetPreferredIconPath(ps3Dir), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(backCoverPath, Ps3InstalledGameHelper.GetPreferredBackCoverPath(ps3Dir), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetTitleId_ReadsTitleIdFromParamSfo()
    {
        using var tempDir = new TempDirectory();
        var ps3Dir = Path.Combine(tempDir.Path, "BLES12345", "PS3_GAME");
        Directory.CreateDirectory(ps3Dir);

        File.WriteAllBytes(Path.Combine(ps3Dir, "PARAM.SFO"), CreateParamSfo("BLES12345"));

        Assert.Equal("BLES12345", Ps3InstalledGameHelper.GetTitleId(Path.Combine(tempDir.Path, "BLES12345")));
        Assert.True(Ps3InstalledGameHelper.IsInstalledGameFolder(Path.Combine(tempDir.Path, "BLES12345")));
    }

    [Fact]
    public void GetTitleName_ReadsTitleNameFromParamSfo()
    {
        using var tempDir = new TempDirectory();
        var ps3Dir = Path.Combine(tempDir.Path, "BLES12345", "PS3_GAME");
        Directory.CreateDirectory(ps3Dir);

        File.WriteAllBytes(Path.Combine(ps3Dir, "PARAM.SFO"), CreateParamSfo("BLES12345", "Metal Gear Solid 4"));

        Assert.Equal("Metal Gear Solid 4", Ps3InstalledGameHelper.GetTitleName(Path.Combine(tempDir.Path, "BLES12345")));
    }

    [Fact]
    public void EmulatorHandlerRegistry_PlayStation4_IncludesShadPs4Handler()
    {
        var handlers = EmulatorHandlerRegistry.GetHandlersForSection("PlayStation 4");

        Assert.Contains(handlers, handler => string.Equals(handler.HandlerId, "shadps4-qtlauncher", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmulatorHandlerRegistry_PlayStation3_IncludesRpcs3Handler()
    {
        var handlers = EmulatorHandlerRegistry.GetHandlersForSection("PlayStation 3");

        Assert.Contains(handlers, handler => string.Equals(handler.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmulatorHandlerRegistry_NintendoSwitch_IncludesEdenHandler()
    {
        var handlers = EmulatorHandlerRegistry.GetHandlersForSection("Nintendo Switch");

        Assert.Contains(handlers, handler => string.Equals(handler.HandlerId, "eden", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmulatorHandlerRegistry_FinalBurnNeo_IncludesFbNeoHandler()
    {
        var handlers = EmulatorHandlerRegistry.GetHandlersForSection("Final Burn Neo");

        Assert.Contains(handlers, handler => string.Equals(handler.HandlerId, "fbneo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmulatorHandlerRegistry_FinalBurnNeo_IncludesRetroArchHandler()
    {
        var handlers = EmulatorHandlerRegistry.GetHandlersForSection("Final Burn Neo");

        Assert.Contains(handlers, handler => string.Equals(handler.HandlerId, "retroarch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FbNeoHandler_BuildStartInfo_UsesGameNameFromZipPath()
    {
        var tempExe = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".exe");
        File.WriteAllText(tempExe, string.Empty);

        try
        {
            var romPath = @"C:\Games\FBN\game.zip";
            var startInfo = FbNeoHandler.Instance.BuildStartInfo(tempExe, romPath, false);

            Assert.Equal(tempExe, startInfo.FileName);
            Assert.Equal(["game", "-w"], startInfo.ArgumentList);
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
    public void FbNeoHandler_BuildStartInfo_UsesGameNameFromDirectoryPath()
    {
        var tempExe = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".exe");
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(tempExe, string.Empty);
        File.WriteAllText(Path.Combine(tempDir, "game.zip"), string.Empty);

        try
        {
            var startInfo = FbNeoHandler.Instance.BuildStartInfo(tempExe, tempDir, true);

            Assert.Equal(tempExe, startInfo.FileName);
            Assert.Equal(["game", "-w"], startInfo.ArgumentList);
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

            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ShadPs4Handler_BuildStartInfo_UsesQtLauncherDefaultVersionAndGamePath()
    {
        var tempExe = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + "QtLauncher.exe");
        File.WriteAllText(tempExe, string.Empty);

        try
        {
            using var tempGameDir = new TempDirectory();
            var gamePath = Path.Combine(tempGameDir.Path, "CUSA00900");
            Directory.CreateDirectory(Path.Combine(gamePath, "sce_sys"));
            File.WriteAllText(Path.Combine(gamePath, "eboot.bin"), string.Empty);
            File.WriteAllText(Path.Combine(gamePath, "sce_sys", "param.sfo"), string.Empty);

            ProcessStartInfo startInfo = ShadPs4Handler.Instance.BuildStartInfo(tempExe, gamePath, false);

            Assert.Equal("cmd.exe", startInfo.FileName);
            Assert.StartsWith("/d /c call \"", startInfo.Arguments);

            var scriptPathStart = "/d /c call \"".Length;
            var scriptPathEnd = startInfo.Arguments!.LastIndexOf('"');
            Assert.True(scriptPathEnd > scriptPathStart);

            var scriptPath = startInfo.Arguments.Substring(scriptPathStart, scriptPathEnd - scriptPathStart);
            var scriptText = File.ReadAllText(scriptPath);
            Assert.Contains("1> \"%SHADPS4_TRANSCRIPT%\" 2>&1", scriptText);
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

    [Fact]
    public void Rpcs3Handler_BuildStartInfo_UsesNoGuiBootFlags()
    {
        var tempExe = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".exe");
        File.WriteAllText(tempExe, string.Empty);

        try
        {
            var gamePath = @"C:\Games\PS3\BLES00001";
            ProcessStartInfo startInfo = Rpcs3Handler.Instance.BuildStartInfo(tempExe, gamePath, true);

            Assert.Equal(tempExe, startInfo.FileName);
            Assert.Equal("--no-gui", startInfo.ArgumentList[0]);
            Assert.Equal(gamePath, startInfo.ArgumentList[1]);
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
    public void Rpcs3Handler_PrefersDirectCompositionCapture()
    {
        Assert.Equal(EmulatorCaptureMode.DirectComposition, Rpcs3Handler.Instance.PreferredCaptureMode);
        Assert.True(Rpcs3Handler.Instance.HideUntilCaptured);
        Assert.True(Rpcs3Handler.Instance.IsWindowEmbeddingSupported);
    }

    private static byte[] CreateParamSfo(string titleId, string? titleName = null)
    {
        const uint magic = 0x46535000;
        const uint version = 0x00000101;
        const uint headerSize = 20;
        var entries = new List<(string Key, string Value)>
        {
            ("TITLE_ID", titleId),
        };

        if (!string.IsNullOrWhiteSpace(titleName))
        {
            entries.Add(("TITLE", titleName));
        }

        var keyBytes = new List<byte>();
        var dataBytes = new List<byte>();
        var serializedEntries = new List<(ushort KeyOffset, uint DataLength, uint DataOffset)>();

        foreach (var entry in entries)
        {
            var keyOffset = (ushort)keyBytes.Count;
            keyBytes.AddRange(System.Text.Encoding.ASCII.GetBytes(entry.Key + "\0"));

            var valueBytes = System.Text.Encoding.UTF8.GetBytes(entry.Value + "\0");
            var dataOffset = (uint)dataBytes.Count;
            dataBytes.AddRange(valueBytes);

            serializedEntries.Add((keyOffset, (uint)valueBytes.Length, dataOffset));
        }

        var keyTableOffset = headerSize + (uint)(serializedEntries.Count * 16);
        var dataTableOffset = keyTableOffset + (uint)keyBytes.Count;

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(magic);
            writer.Write(version);
            writer.Write(keyTableOffset);
            writer.Write(dataTableOffset);
            writer.Write((uint)serializedEntries.Count);

            foreach (var entry in serializedEntries)
            {
                writer.Write(entry.KeyOffset);
                writer.Write((byte)0x04);
                writer.Write((byte)0);
                writer.Write(entry.DataLength);
                writer.Write(entry.DataLength);
                writer.Write(entry.DataOffset);
            }

            writer.Write(keyBytes.ToArray());
            writer.Write(dataBytes.ToArray());
        }

        return stream.ToArray();
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
