using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace AES_Emulation.Linux;

[SupportedOSPlatform("linux")]
public static class FlatpakLaunchHelper
{
    public static bool IsFlatpakAvailable() => LinuxFlatpakApplicationService.IsFlatpakAvailable();

    public static void Apply(ProcessStartInfo startInfo, string flatpakAppId)
    {
        var flatpakPath = LinuxFlatpakApplicationService.FindFlatpakExecutable();
        if (flatpakPath == null || string.IsNullOrWhiteSpace(flatpakAppId))
            return;

        var forwardedArgs = new List<string>();
        foreach (var arg in startInfo.ArgumentList)
            forwardedArgs.Add(arg);

        startInfo.ArgumentList.Clear();
        startInfo.FileName = flatpakPath;
        startInfo.UseShellExecute = false;
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(flatpakAppId);

        // Arguments after the app id are forwarded to the application. Do not insert `--` here:
        // flatpak would pass it through and break option parsing in apps like Dolphin (`-b` becomes a file path).
        foreach (var arg in forwardedArgs)
            startInfo.ArgumentList.Add(arg);
    }
}
