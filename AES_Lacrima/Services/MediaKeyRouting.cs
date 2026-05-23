using System;
using Avalonia.Input;

namespace AES_Lacrima.Services;

public readonly record struct MediaKeyHandlers(
    Func<bool> PlayNext,
    Func<bool> PlayPrevious,
    Func<bool> TogglePlayPause);

/// <summary>
/// Routes hardware media keys to playback commands.
/// On Linux, Fn/media keys often arrive as XF86 key symbols instead of <see cref="Key.MediaPlayPause"/>.
/// </summary>
public static class MediaKeyRouting
{
    public static bool TryHandle(KeyEventArgs e, MediaKeyHandlers handlers)
    {
        if (e.Handled)
            return false;

        switch (e.Key)
        {
            case Key.MediaNextTrack:
                return TryExecute(handlers.PlayNext, e);
            case Key.MediaPreviousTrack:
                return TryExecute(handlers.PlayPrevious, e);
            case Key.MediaPlayPause:
            case Key.MediaStop:
                return TryExecute(handlers.TogglePlayPause, e);
        }

        var symbol = e.KeySymbol;
        if (string.IsNullOrWhiteSpace(symbol))
            return false;

        switch (symbol)
        {
            case "XF86AudioNext":
                return TryExecute(handlers.PlayNext, e);
            case "XF86AudioPrev":
                return TryExecute(handlers.PlayPrevious, e);
            case "XF86AudioPlay":
            case "XF86AudioPause":
            case "XF86AudioPlayPause":
                return TryExecute(handlers.TogglePlayPause, e);
            case "XF86AudioStop":
                return TryExecute(handlers.TogglePlayPause, e);
            default:
                return false;
        }
    }

    private static bool TryExecute(Func<bool> command, KeyEventArgs e)
    {
        if (!command())
            return false;

        e.Handled = true;
        return true;
    }
}
