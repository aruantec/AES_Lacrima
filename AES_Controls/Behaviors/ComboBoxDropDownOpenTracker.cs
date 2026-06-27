using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using AES_Controls.Helpers;

namespace AES_Controls.Behaviors;

/// <summary>
/// Tracks how many combo box drop-downs are currently open so borderless window resize
/// can be suppressed while any list is shown.
/// </summary>
public static class ComboBoxDropDownOpenTracker
{
    private static int _openCount;
    private static readonly ConditionalWeakTable<ComboBox, DropDownSubscription> Subscriptions = new();

    public static bool IsAnyOpen => Volatile.Read(ref _openCount) > 0;

    /// <summary>
    /// Raised on the UI thread when the first tracked combo box drop-down opens.
    /// </summary>
    public static event Action? Opened;

    /// <summary>
    /// Raised on the UI thread when the last tracked combo box drop-down closes.
    /// </summary>
    public static event Action? LastClosed;

    public static void EnsureTracking(ComboBox combo, Action<bool>? onDropDownOpenChanged = null)
    {
        if (combo == null)
            return;

        if (!Subscriptions.TryGetValue(combo, out var subscription))
        {
            subscription = new DropDownSubscription(combo);
            Subscriptions.Add(combo, subscription);
        }

        if (onDropDownOpenChanged != null)
            subscription.AddCallback(onDropDownOpenChanged);
    }

    public static void RemoveCallback(ComboBox combo, Action<bool> onDropDownOpenChanged)
    {
        if (combo == null || onDropDownOpenChanged == null)
            return;

        if (Subscriptions.TryGetValue(combo, out var subscription))
            subscription.RemoveCallback(onDropDownOpenChanged);
    }

    public static void NotifyOpened()
    {
        if (Interlocked.Increment(ref _openCount) == 1)
            Opened?.Invoke();
    }

    public static void NotifyClosed()
    {
        var remaining = Interlocked.Decrement(ref _openCount);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _openCount, 0);
            return;
        }

        if (remaining == 0)
            LastClosed?.Invoke();
    }

    private sealed class DropDownSubscription
    {
        private readonly List<Action<bool>> _callbacks = [];
        private readonly IDisposable _subscription;

        public DropDownSubscription(ComboBox combo)
        {
            _subscription = combo.GetObservable(ComboBox.IsDropDownOpenProperty)
                .Subscribe(new SimpleObserver<bool>(isOpen =>
                {
                    if (isOpen)
                    {
                        NotifyOpened();
                        InvokeCallbacks(true);
                        return;
                    }

                    NotifyClosed();
                    InvokeCallbacks(false);
                }));
        }

        public void AddCallback(Action<bool> callback)
        {
            if (!_callbacks.Contains(callback))
                _callbacks.Add(callback);
        }

        public void RemoveCallback(Action<bool> callback) => _callbacks.Remove(callback);

        private void InvokeCallbacks(bool isOpen)
        {
            foreach (var callback in _callbacks.ToArray())
                callback(isOpen);
        }

        public void Dispose() => _subscription.Dispose();
    }
}
