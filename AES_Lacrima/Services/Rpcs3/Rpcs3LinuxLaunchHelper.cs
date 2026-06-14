using System;
using System.Diagnostics;
using AES_Emulation.Linux;

namespace AES_Lacrima.Services.Rpcs3;

/// <summary>
/// Linux-specific RPCS3 launch preparation so configs, patches, and PPU probes use the AES-managed tree.
/// </summary>
public static class Rpcs3LinuxLaunchHelper
{
    public static void PrepareLaunch(
        ProcessStartInfo startInfo,
        string? emulatorDirectory,
        string? flatpakAppId = null,
        string? gamePath = null)
    {
        if (!OperatingSystem.IsLinux())
            return;

        if (!string.IsNullOrWhiteSpace(flatpakAppId))
        {
            FlatpakLaunchHelper.Apply(startInfo, flatpakAppId, gamePath, emulatorDirectory);
            return;
        }

        LinuxAppImageLaunchHelper.PrepareDirectExtractAndRunLaunch(startInfo);
    }
}
