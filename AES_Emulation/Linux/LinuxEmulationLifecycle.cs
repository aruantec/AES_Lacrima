namespace AES_Emulation.Linux;

/// <summary>
/// Coarse lifecycle flags for Linux emulation teardown paths.
/// </summary>
public static class LinuxEmulationLifecycle
{
    public static bool IsApplicationExitInProgress { get; set; }

    /// <summary>
    /// Set while a gamescope/emulator session is winding down so capture and input
    /// paths can stop acquiring frames before compositor teardown races them.
    /// </summary>
    public static bool IsEmulatorSessionShutdownInProgress { get; set; }
}
