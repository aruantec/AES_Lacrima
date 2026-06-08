namespace AES_Emulation.Linux;

/// <summary>
/// Coarse lifecycle flags for Linux emulation teardown paths.
/// </summary>
public static class LinuxEmulationLifecycle
{
    public static bool IsApplicationExitInProgress { get; set; }
}
