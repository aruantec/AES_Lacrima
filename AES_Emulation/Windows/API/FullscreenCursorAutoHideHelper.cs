using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AES_Emulation.Linux.API;

namespace AES_Emulation.Windows.API;

/// <summary>
/// Hides the mouse cursor after a period of inactivity while fullscreen, and restores it on movement.
/// Uses Win32 cursor polling on Windows and X11 pointer polling + XFixes on Linux so hiding works
/// over native capture surfaces (airspace).
/// Also polls Escape / left-button double-click on Windows because emulator focus steals Avalonia input.
/// </summary>
public sealed class FullscreenCursorAutoHideHelper : IDisposable
{
    private static readonly TimeSpan IdleDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DoubleClickInterval = TimeSpan.FromMilliseconds(500);

    private readonly InputElement? _cursorScope;
    private readonly TopLevel? _topLevel;

    private DispatcherTimer? _pollTimer;
    private Cursor? _savedScopeCursor;
    private Cursor? _savedTopLevelCursor;
    private NativePoint _lastCursorPos;
    private DateTime _lastMovementUtc;
    private bool _isHidden;
    private bool _didHideSystemCursor;
    private bool _escapeWasDown;
    private bool _leftButtonWasDown;
    private bool _hasPendingClick;
    private DateTime _pendingClickUtc;
    private LinuxFullscreenCursorSupport? _linuxCursorSupport;

    public event EventHandler? EscapePressed;
    public event EventHandler? DoubleClicked;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_ESCAPE = 0x1B;
    private const int VK_LBUTTON = 0x01;

    public FullscreenCursorAutoHideHelper(InputElement? cursorScope = null)
    {
        _cursorScope = cursorScope;
        _topLevel = cursorScope != null ? TopLevel.GetTopLevel(cursorScope) : null;
    }

    public void Start()
    {
        if (_pollTimer != null)
            return;

        _lastMovementUtc = DateTime.UtcNow;
        _isHidden = false;
        _didHideSystemCursor = false;
        _escapeWasDown = false;
        _leftButtonWasDown = false;
        _hasPendingClick = false;
        _savedScopeCursor = null;
        _savedTopLevelCursor = null;

        if (OperatingSystem.IsWindows())
        {
            GetCursorPos(out _lastCursorPos);
        }
        else if (OperatingSystem.IsLinux())
        {
            _linuxCursorSupport = LinuxFullscreenCursorSupport.TryCreate(_topLevel);
            if (_linuxCursorSupport?.TryGetRootPointerPosition(out var x, out var y) == true)
                _lastCursorPos = new NativePoint { X = x, Y = y };
        }

        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    public void NotifyPointerActivity()
    {
        _lastMovementUtc = DateTime.UtcNow;
        ShowCursorNow();
    }

    public void Stop()
    {
        if (_pollTimer != null)
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= OnPollTick;
            _pollTimer = null;
        }

        ShowCursorNow();
        _hasPendingClick = false;

        if (OperatingSystem.IsLinux())
        {
            _linuxCursorSupport?.Dispose();
            _linuxCursorSupport = null;
        }
    }

    public void Dispose() => Stop();

    private void OnPollTick(object? sender, EventArgs e)
    {
        PollEscapeKey();
        PollLeftButtonDoubleClick();

        if (TryPollPointerMovement())
            return;

        if (!_isHidden && DateTime.UtcNow - _lastMovementUtc >= IdleDuration)
            HideCursorNow();
    }

    private void PollEscapeKey()
    {
        // Emulator focus steals Avalonia KeyDown; poll Escape while capture fullscreen is active.
        if (!OperatingSystem.IsWindows())
            return;

        var isDown = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;
        if (isDown && !_escapeWasDown)
            EscapePressed?.Invoke(this, EventArgs.Empty);

        _escapeWasDown = isDown;
    }

    private void PollLeftButtonDoubleClick()
    {
        // Emulator foreground can steal Avalonia pointer events; poll left-button edges instead.
        if (!OperatingSystem.IsWindows() || DoubleClicked == null)
            return;

        var isDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (isDown && !_leftButtonWasDown && IsCursorOverTopLevel())
        {
            var now = DateTime.UtcNow;
            if (_hasPendingClick && now - _pendingClickUtc <= DoubleClickInterval)
            {
                _hasPendingClick = false;
                DoubleClicked.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _hasPendingClick = true;
                _pendingClickUtc = now;
            }
        }

        _leftButtonWasDown = isDown;

        if (_hasPendingClick && DateTime.UtcNow - _pendingClickUtc > DoubleClickInterval)
            _hasPendingClick = false;
    }

    private bool IsCursorOverTopLevel()
    {
        if (_topLevel is not Window window)
            return true;

        if (!GetCursorPos(out var pos))
            return false;

        var scaling = Math.Max(0.0001, window.RenderScaling);
        var width = (int)Math.Round(window.ClientSize.Width * scaling);
        var height = (int)Math.Round(window.ClientSize.Height * scaling);
        if (width <= 0 || height <= 0)
            return false;

        var origin = window.Position;
        return pos.X >= origin.X &&
               pos.Y >= origin.Y &&
               pos.X < origin.X + width &&
               pos.Y < origin.Y + height;
    }

    private bool TryPollPointerMovement()
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetCursorPos(out var pos))
                return false;

            if (pos.X == _lastCursorPos.X && pos.Y == _lastCursorPos.Y)
                return false;

            _lastCursorPos = pos;
            _lastMovementUtc = DateTime.UtcNow;
            ShowCursorNow();
            return true;
        }

        if (OperatingSystem.IsLinux() &&
            _linuxCursorSupport?.TryGetRootPointerPosition(out var x, out var y) == true)
        {
            if (x == _lastCursorPos.X && y == _lastCursorPos.Y)
                return false;

            _lastCursorPos = new NativePoint { X = x, Y = y };
            _lastMovementUtc = DateTime.UtcNow;
            ShowCursorNow();
            return true;
        }

        return false;
    }

    private void HideCursorNow()
    {
        if (_isHidden)
            return;

        _isHidden = true;
        HideSystemCursor();
        SetAvaloniaCursorHidden(true);
    }

    private void ShowCursorNow()
    {
        if (!_isHidden && !_didHideSystemCursor &&
            _savedScopeCursor == null && _savedTopLevelCursor == null)
        {
            return;
        }

        _isHidden = false;
        RestoreSystemCursor();
        SetAvaloniaCursorHidden(false);
    }

    private void HideSystemCursor()
    {
        if (OperatingSystem.IsWindows())
        {
            if (_didHideSystemCursor)
                return;

            int displayCount;
            var guard = 0;
            do
            {
                displayCount = ShowCursor(false);
                guard++;
            } while (displayCount >= 0 && guard < 128);

            _didHideSystemCursor = true;
            return;
        }

        if (OperatingSystem.IsLinux())
            _linuxCursorSupport?.HideCursor();
    }

    private void RestoreSystemCursor()
    {
        if (OperatingSystem.IsWindows())
        {
            if (!_didHideSystemCursor)
                return;

            int displayCount;
            var guard = 0;
            do
            {
                displayCount = ShowCursor(true);
                guard++;
            } while (displayCount < 0 && guard < 128);

            _didHideSystemCursor = false;
            return;
        }

        if (OperatingSystem.IsLinux())
            _linuxCursorSupport?.ShowCursor();
    }

    private void SetAvaloniaCursorHidden(bool hidden)
    {
        var none = new Cursor(StandardCursorType.None);

        if (hidden)
        {
            if (_cursorScope != null)
            {
                _savedScopeCursor ??= _cursorScope.Cursor;
                _cursorScope.Cursor = none;
            }

            if (_topLevel != null)
            {
                _savedTopLevelCursor ??= _topLevel.Cursor;
                _topLevel.Cursor = none;
            }

            return;
        }

        if (_cursorScope != null)
        {
            _cursorScope.Cursor = _savedScopeCursor ?? Cursor.Default;
            _savedScopeCursor = null;
        }

        if (_topLevel != null)
        {
            _topLevel.Cursor = _savedTopLevelCursor ?? Cursor.Default;
            _savedTopLevelCursor = null;
        }
    }
}
