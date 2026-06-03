using System;
using System.Collections.Generic;
using System.Linq;
using AES_Core.Logging;
using AES_Emulation.Windows.API;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AES_Lacrima.ViewModels;

public partial class EmulationViewModel
{
    public static IReadOnlyList<CaptureAspectRatioPreset> CaptureAspectRatioPresets { get; } =
    [
        new("handler", "Handler Default", null),
        new("4:3", "4:3", 4.0 / 3.0),
        new("16:9", "16:9", 16.0 / 9.0),
        new("21:9", "21:9", 21.0 / 9.0),
        new("32:9", "32:9", 32.0 / 9.0),
        new("1:1", "1:1", 1.0),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureWindowAspectRatio))]
    [NotifyPropertyChangedFor(nameof(SelectedCaptureAspectRatioLabel))]
    private string _selectedCaptureAspectRatioKey = "handler";

    public string SelectedCaptureAspectRatioLabel =>
        CaptureAspectRatioPresets.FirstOrDefault(p =>
            string.Equals(p.Key, SelectedCaptureAspectRatioKey, StringComparison.OrdinalIgnoreCase)).Label
        ?? "Handler Default";

    public double CaptureWindowAspectRatio
    {
        get
        {
            var preset = CaptureAspectRatioPresets.FirstOrDefault(p =>
                string.Equals(p.Key, SelectedCaptureAspectRatioKey, StringComparison.OrdinalIgnoreCase));

            if (preset.Ratio is > 0)
                return preset.Ratio.Value;

            return CurrentEmulatorHandler?.CaptureWindowAspectRatio ?? 0;
        }
    }

    [RelayCommand]
    private void SetCaptureAspectRatio(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (CaptureAspectRatioPresets.All(p =>
                !string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        SelectedCaptureAspectRatioKey = key;
    }

    partial void OnSelectedCaptureAspectRatioKeyChanged(string value)
    {
        ApplyCaptureAspectRatioToRunningTarget();
        AutoSave();
    }

    private void ApplyCaptureAspectRatioToRunningTarget()
    {
        if (!IsEmulatorRunning || EmulatorTargetHwnd == IntPtr.Zero)
            return;

        var aspect = CaptureWindowAspectRatio;
        if (aspect <= 0)
            return;

        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            Win32API.ResizeWindowToAspectRatioInPlace(EmulatorTargetHwnd, aspect);
        }
        catch (Exception ex)
        {
            SLog.Warn("Failed to apply capture aspect ratio to emulator window.", ex);
        }
    }
}
