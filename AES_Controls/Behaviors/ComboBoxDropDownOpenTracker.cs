using System.Threading;

namespace AES_Controls.Behaviors;

/// <summary>
/// Tracks how many combo box drop-downs are currently open so borderless window resize
/// can be suppressed while any list is shown.
/// </summary>
public static class ComboBoxDropDownOpenTracker
{
    private static int _openCount;

    public static bool IsAnyOpen => Volatile.Read(ref _openCount) > 0;

    public static void NotifyOpened() => Interlocked.Increment(ref _openCount);

    public static void NotifyClosed()
    {
        var remaining = Interlocked.Decrement(ref _openCount);
        if (remaining < 0)
            Interlocked.Exchange(ref _openCount, 0);
    }
}
