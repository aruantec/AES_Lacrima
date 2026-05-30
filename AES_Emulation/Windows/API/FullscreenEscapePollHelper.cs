using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace AES_Emulation.Windows.API;

/// <summary>
/// Polls for Escape while fullscreen capture holds keyboard focus (airspace / tunnel).
/// </summary>
public sealed class FullscreenEscapePollHelper : IDisposable
{
    private const int VkEscape = 0x1B;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly Action _onEscapePressed;
    private DispatcherTimer? _pollTimer;
    private bool _escapeWasDown;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public FullscreenEscapePollHelper(Action onEscapePressed)
    {
        _onEscapePressed = onEscapePressed ?? throw new ArgumentNullException(nameof(onEscapePressed));
    }

    public void Start()
    {
        if (_pollTimer != null)
            return;

        _escapeWasDown = false;
        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    public void Stop()
    {
        if (_pollTimer == null)
            return;

        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTick;
        _pollTimer = null;
        _escapeWasDown = false;
    }

    public void Dispose() => Stop();

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var down = (GetAsyncKeyState(VkEscape) & 0x8000) != 0;
        if (down && !_escapeWasDown)
            _onEscapePressed();

        _escapeWasDown = down;
    }
}
