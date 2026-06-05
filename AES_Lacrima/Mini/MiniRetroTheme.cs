using System;
using Avalonia.Media;

namespace AES_Lacrima.Mini;

/// <summary>
/// Palette and typography for the mini player's retro RPG presentation.
/// </summary>
public static class MiniRetroTheme
{
    public static FontFamily PixelFontFamily { get; } =
        new("avares://AES_Lacrima/Assets/Fonts/PressStart2P-Regular.ttf#Press Start 2P");

    public static Color WindowBackground { get; } = Color.Parse("#14101C");
    public static Color WindowBorder { get; } = Color.Parse("#7B6FA0");
    public static Color PanelBackground { get; } = Color.Parse("#1E1830");
    public static Color PanelBorder { get; } = Color.Parse("#6E5F94");
    public static Color Divider { get; } = Color.Parse("#4A3F66");
    public static Color PrimaryText { get; } = Color.Parse("#E8E0F0");
    public static Color MutedText { get; } = Color.Parse("#9A8FB8");
    public static Color Accent { get; } = Color.Parse("#5A4678");
    public static Color SelectionFill { get; } = Color.Parse("#3D2B56");
    public static Color SelectionBorder { get; } = Color.Parse("#D8C4FF");
    public static Color SelectionText { get; } = Color.Parse("#F4EEFF");
    public static Color LoadedRowFill { get; } = Color.Parse("#2A2040");
    public static Color LoadedRowText { get; } = Color.Parse("#B8E6FF");
    public static Color ProgressBorder { get; } = Color.Parse("#8A7AAE");
    public static Color ProgressFill { get; } = Color.Parse("#99E8E0F0");

    public static SolidColorBrush WindowBackgroundBrush { get; } = new(WindowBackground);
    public static SolidColorBrush WindowBorderBrush { get; } = new(WindowBorder);
    public static SolidColorBrush PanelBackgroundBrush { get; } = new(PanelBackground);
    public static SolidColorBrush PanelBorderBrush { get; } = new(PanelBorder);
    public static SolidColorBrush DividerBrush { get; } = new(Divider);
    public static SolidColorBrush PrimaryTextBrush { get; } = new(PrimaryText);
    public static SolidColorBrush MutedTextBrush { get; } = new(MutedText);
    public static SolidColorBrush AccentBrush { get; } = new(Accent);
    public static SolidColorBrush SelectionFillBrush { get; } = new(SelectionFill);
    public static SolidColorBrush SelectionBorderBrush { get; } = new(SelectionBorder);
    public static SolidColorBrush SelectionTextBrush { get; } = new(SelectionText);
    public static SolidColorBrush LoadedRowFillBrush { get; } = new(LoadedRowFill);
    public static SolidColorBrush LoadedRowTextBrush { get; } = new(LoadedRowText);
    public static SolidColorBrush ProgressBorderBrush { get; } = new(ProgressBorder);
    public static SolidColorBrush ProgressFillBrush { get; } = new(ProgressFill);

    public static Color Tint(Color baseColor, Color accent, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromArgb(
            baseColor.A,
            (byte)(baseColor.R + (accent.R - baseColor.R) * amount),
            (byte)(baseColor.G + (accent.G - baseColor.G) * amount),
            (byte)(baseColor.B + (accent.B - baseColor.B) * amount));
    }

    public static Color TintWithAlpha(Color baseColor, Color accent, double amount, byte alpha)
    {
        var tinted = Tint(baseColor, accent, amount);
        return Color.FromArgb(alpha, tinted.R, tinted.G, tinted.B);
    }
}
