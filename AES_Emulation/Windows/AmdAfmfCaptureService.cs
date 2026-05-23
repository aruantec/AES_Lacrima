using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AES_Emulation.Windows;

/// <summary>
/// Best-effort AMD Fluid Motion Frames (AFMF) helpers for the capture portal.
/// AFMF is applied by the AMD driver; this type configures compatibility and optional ADLX enablement.
/// </summary>
public static class AmdAfmfCaptureService
{
    private const string AdlxDllName = "adlx.dll";

    public static bool IsAmdGpu(string? renderer, string? vendor)
    {
        static bool ContainsAmd(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            (value.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
             value.Contains("ATI", StringComparison.OrdinalIgnoreCase) ||
             value.Contains("Radeon", StringComparison.OrdinalIgnoreCase));

        return ContainsAmd(vendor) || ContainsAmd(renderer);
    }

    public static string GetDriverSetupHint() =>
        "Enable Fluid Motion Frames for AES_Lacrima.exe in AMD Software: Gaming > Games > AES_Lacrima, " +
        "or globally under Graphics > AMD Fluid Motion Frames. Use borderless/windowed capture with VSync disabled.";

    public static bool IsAdlxAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (NativeLibrary.TryLoad(AdlxDllName, out _))
            return true;

        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemAdlx = Path.Combine(systemRoot, AdlxDllName);
        return File.Exists(systemAdlx);
    }

    /// <summary>
    /// Attempts to enable AFMF via ADLX when the DLL is present. Returns a user-facing status message.
    /// </summary>
    public static string TryEnableAfmfViaAdlx()
    {
        if (!OperatingSystem.IsWindows())
            return "AFMF is only available on Windows with a supported AMD GPU.";

        if (!IsAdlxAvailable())
            return "ADLX not found. " + GetDriverSetupHint();

        // ADLX is a C++ SDK; full integration requires shipping ADLX bindings.
        // Driver-level per-game AFMF remains the supported path for capture portals.
        return "ADLX detected. " + GetDriverSetupHint();
    }
}
