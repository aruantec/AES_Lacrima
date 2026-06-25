using System;
using AES_Emulation.EmulationHandlers;
using Xunit;

namespace AES_Tests.AES_Emulation;

public sealed class RetroArchInputConfigTests
{
    [Fact]
    public void GetRetroArchSystemDirectories_includes_appimage_portable_config_root()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var launcherPath = "/tmp/RetroArch-Linux-x86_64.AppImage";
        var directories = RetroArchHandler.GetRetroArchSystemDirectories(launcherPath);

        Assert.Contains(
            "/tmp/RetroArch-Linux-x86_64.AppImage.home/.config/retroarch/system",
            directories);
    }

    [Fact]
    public void GetRetroArchLogFilePaths_includes_appimage_portable_config_root()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var launcherPath = "/tmp/RetroArch-Linux-x86_64.AppImage";
        var paths = RetroArchHandler.GetRetroArchLogFilePaths(launcherPath);

        Assert.Contains(
            "/tmp/RetroArch-Linux-x86_64.AppImage.home/.config/retroarch/retroarch-launch.log",
            paths);
    }
}
