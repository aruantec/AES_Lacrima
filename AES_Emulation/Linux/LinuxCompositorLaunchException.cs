using System;

namespace AES_Emulation.Linux;

public sealed class LinuxCompositorLaunchException : Exception
{
    public const string MissingBinaryMessage =
        "gamescope was not found. On Linux, emulators run inside gamescope. " +
        "Install it from Settings → Libraries or via your system package manager.";

    public LinuxCompositorLaunchException(string message) : base(message)
    {
    }

    public LinuxCompositorLaunchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
