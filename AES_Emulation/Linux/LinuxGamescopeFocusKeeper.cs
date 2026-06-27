using System;
using System.Runtime.Versioning;
using Avalonia.Threading;

namespace AES_Emulation.Linux;

/// <summary>
/// Periodically returns X11 input focus to the gamescope game window while AES UI overlays
/// are open. Steam titles often pause (time + audio freeze) when the host app keeps focus.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxGamescopeFocusKeeper : IDisposable
{
    private static readonly TimeSpan FocusKeeperInterval = TimeSpan.FromMilliseconds(16);

    private readonly Func<bool> _shouldKeepFocus;
    private readonly Action _forwardFocus;
    private DispatcherTimer? _timer;
    private bool _disposed;

    public LinuxGamescopeFocusKeeper(Func<bool> shouldKeepFocus, Action forwardFocus)
    {
        _shouldKeepFocus = shouldKeepFocus;
        _forwardFocus = forwardFocus;
    }

    public void Update()
    {
        if (_disposed)
            return;

        if (_shouldKeepFocus())
            StartTimer();
        else
            StopTimer();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopTimer();
    }

    private void StartTimer()
    {
        _timer ??= new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = FocusKeeperInterval
        };

        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;

        if (!_timer.IsEnabled)
        {
            _forwardFocus();
            _timer.Start();
        }
    }

    private void StopTimer()
    {
        _timer?.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_shouldKeepFocus())
        {
            StopTimer();
            return;
        }

        _forwardFocus();
    }
}
