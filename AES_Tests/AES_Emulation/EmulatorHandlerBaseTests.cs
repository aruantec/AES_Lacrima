using AES_Emulation.EmulationHandlers;
using AES_Lacrima.Tests;

namespace AES_Emulation.Tests;

public sealed class EmulatorHandlerBaseTests
{
    [Fact]
    public void BuildStartInfo_LinuxDesktopEntry_UsesExecLineAndExpandsUrlFieldCode()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var tempDirectory = new TempDirectory();
        var desktopPath = Path.Combine(tempDirectory.Path, "snes9x-flatpak.desktop");
        var romPath = Path.Combine(tempDirectory.Path, "Super Mario World.sfc");

        File.WriteAllText(desktopPath, """
[Desktop Entry]
Type=Application
Exec=flatpak run org.snes9x.Snes9x %U
""");

        var handler = new TestHandler();
        var startInfo = handler.BuildStartInfo(desktopPath, romPath, false);

        Assert.Equal("flatpak", startInfo.FileName);
        Assert.Equal(tempDirectory.Path, startInfo.WorkingDirectory);
        Assert.Equal(new[]
        {
            "run",
            "--env=GDK_BACKEND=x11",
            "--env=SDL_VIDEODRIVER=x11",
            "--env=QT_QPA_PLATFORM=xcb",
            "org.snes9x.Snes9x",
            new Uri(Path.GetFullPath(romPath)).AbsoluteUri
        }, startInfo.ArgumentList.ToArray());
    }

    [Fact]
    public void BuildStartInfo_LinuxDesktopEntry_AppendsRomPathWhenNoFieldCodeIsPresent()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var tempDirectory = new TempDirectory();
        var desktopPath = Path.Combine(tempDirectory.Path, "snes9x-flatpak.desktop");
        var romPath = Path.Combine(tempDirectory.Path, "Chrono Trigger.sfc");

        File.WriteAllText(desktopPath, """
[Desktop Entry]
Type=Application
Exec=env flatpak run org.snes9x.Snes9x
""");

        var handler = new TestHandler();
        var startInfo = handler.BuildStartInfo(desktopPath, romPath, false);

        Assert.Equal("env", startInfo.FileName);
        Assert.Equal(new[]
        {
            "flatpak",
            "run",
            "--env=GDK_BACKEND=x11",
            "--env=SDL_VIDEODRIVER=x11",
            "--env=QT_QPA_PLATFORM=xcb",
            "org.snes9x.Snes9x",
            romPath
        }, startInfo.ArgumentList.ToArray());
    }

    private sealed class TestHandler : EmulatorHandlerBase
    {
        public override string HandlerId => "test";

        public override string SectionKey => "TEST";

        public override string SectionTitle => "Test";

        public override string DisplayName => "Test Emulator";

        public override bool CanHandleAlbumTitle(string? albumTitle) => false;
    }
}

public sealed class RetroArchHandlerTests
{
    [Fact]
    public void BuildStartInfo_LinuxDesktopEntry_PreservesFlatpakRunPrefixForSelectedCore()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var tempDirectory = new TempDirectory();
        var desktopPath = Path.Combine(tempDirectory.Path, "retroarch.desktop");
        var romPath = Path.Combine(tempDirectory.Path, "Super Mario World.sfc");
        var coresDirectory = Path.Combine(tempDirectory.Path, "cores");
        var corePath = Path.Combine(coresDirectory, "snes9x_libretro.so");

        Directory.CreateDirectory(coresDirectory);
        File.WriteAllText(corePath, string.Empty);
        File.WriteAllText(desktopPath, """
[Desktop Entry]
Type=Application
Exec=flatpak run org.libretro.RetroArch %U
""");

        var startInfo = RetroArchHandler.Instance.BuildStartInfo(desktopPath, romPath, true, null, "snes9x_libretro.so");
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("flatpak", startInfo.FileName);
        Assert.Equal("run", arguments[0]);
        Assert.Contains("--env=GDK_BACKEND=x11", arguments);
        Assert.Contains("--env=SDL_VIDEODRIVER=x11", arguments);
        Assert.DoesNotContain("--env=QT_QPA_PLATFORM=xcb", arguments);

        var appIdIndex = Array.IndexOf(arguments, "org.libretro.RetroArch");
        Assert.True(appIdIndex > 0);
        Assert.True(appIdIndex > Array.IndexOf(arguments, "--env=SDL_VIDEODRIVER=x11"));
        Assert.Contains("--fullscreen", arguments);

        var appendConfigIndex = Array.IndexOf(arguments, "--appendconfig");
        Assert.True(appendConfigIndex >= 2);
        Assert.True(File.Exists(arguments[appendConfigIndex + 1]));

        var coreIndex = Array.IndexOf(arguments, "-L");
        Assert.True(coreIndex >= 2);
        Assert.Equal(corePath, arguments[coreIndex + 1]);

        Assert.Equal(romPath, arguments[^1]);
    }
}