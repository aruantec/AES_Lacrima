using AES_Emulation.EmulationHandlers;
using AES_Lacrima.Tests;

namespace AES_Emulation.Tests;

public sealed class ShadPs4HandlerTests
{
    [Fact]
    public void GetHandler_ReturnsShadPs4ForPlayStation4Section()
    {
        var handler = EmulatorHandlerRegistry.GetHandler("PlayStation 4");

        Assert.Same(ShadPs4Handler.Instance, handler);
    }

    [Fact]
    public void BuildStartInfo_AppendsFullscreenBeforeEbootBin()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var tempDirectory = new TempDirectory();
        var launcherPath = Path.Combine(tempDirectory.Path, "shadps4.desktop");
        var romPath = Path.Combine(tempDirectory.Path, "CUSA01623");
        Directory.CreateDirectory(romPath);
        File.WriteAllText(Path.Combine(romPath, "eboot.bin"), string.Empty);

        File.WriteAllText(launcherPath, """
[Desktop Entry]
Type=Application
        Exec=flatpak run net.shadps4.shadps4-qtlauncher
""");

        var startInfo = ShadPs4Handler.Instance.BuildStartInfo(launcherPath, romPath, true);

        Assert.Equal("flatpak", startInfo.FileName);
        Assert.Equal(new[]
        {
            "run",
            "--env=GDK_BACKEND=x11",
            "--env=SDL_VIDEODRIVER=x11",
            "--env=QT_QPA_PLATFORM=xcb",
            "net.shadps4.shadps4-qtlauncher",
            "-e",
            "default",
            "-g",
            Path.Combine(romPath, "eboot.bin"),
            "--",
            "--fullscreen",
            "true"
        }, startInfo.ArgumentList.ToArray());
    }

    [Fact]
    public void BuildStartInfo_UsesEbootBinWhenGameFolderIsProvided()
    {
        var handler = ShadPs4Handler.Instance;

        using var tempDirectory = new TempDirectory();
        var gameFolder = Path.Combine(tempDirectory.Path, "CUSA01623");
        Directory.CreateDirectory(gameFolder);
        File.WriteAllText(Path.Combine(gameFolder, "eboot.bin"), string.Empty);

        var startInfo = handler.BuildStartInfo("shadPS4QtLauncher", gameFolder, false);

        Assert.Equal(new[]
        {
            "-e",
            "default",
            "-g",
            Path.Combine(gameFolder, "eboot.bin")
        }, startInfo.ArgumentList.ToArray());
    }
}