using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;
using System;
using System.Collections.Generic;

namespace AES_Lacrima.Behaviors
{
    /// <summary>
    /// A behavior that toggles the <c>MaxHeight</c> of the associated control between
    /// a stored expanded height and zero. Attach this behavior to any <see cref="Control"/>
    /// and bind the <see cref="IsOpen"/> property to a boolean in your ViewModel.
    /// When <see cref="IsOpen"/> is <c>true</c> the behavior sets <c>MaxHeight</c> to
    /// <c>0</c> (collapsed); when <c>false</c> it restores the height captured when the
    /// behavior was attached (or a default value if none was available).
    ///
    /// The behavior intentionally does not create or manage transitions — define any
    /// desired <c>DoubleTransition</c> for <c>MaxHeight</c> in the control's XAML so
    /// the change is animated by the platform's Transitions system.
    /// </summary>
    public class MaxHeightToggleBehavior : Behavior<Control>
    {
        /// <summary>
        /// Styled property backing <see cref="IsOpen"/>.
        /// </summary>
        public static readonly StyledProperty<bool> IsOpenProperty =
            AvaloniaProperty.Register<MaxHeightToggleBehavior, bool>(nameof(IsOpen));

        /// <summary>
        /// Gets or sets whether the associated control should be collapsed.
        /// True => collapsed (MaxHeight = 0). False => restored to the stored expanded height.
        /// Bind this to a boolean on your ViewModel (for example, <c>IsAlbumlistOpen</c>).
        /// </summary>
        public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

        private readonly List<IDisposable> _subs = [];

        /// <summary>
        /// Called when the behavior is detached from its associated object. Disposes
        /// any subscriptions created while attached.
        /// </summary>
        protected override void OnDetaching()
        {
            base.OnDetaching();
            foreach (var d in _subs) try { d.Dispose(); } catch { }
            _subs.Clear();
        }

        private const double DefaultHeight = 265.0;
        private double _savedHeight = 0.0;

        /// <summary>
        /// Called when the behavior is attached. Captures the associated control's
        /// measured height (if available) and subscribes to the <see cref="IsOpenProperty"/>
        /// to update <c>MaxHeight</c> when the value changes.
        /// </summary>
        protected override void OnAttached()
        {
            base.OnAttached();

            if (AssociatedObject != null)
            {
                try
                {
                    var h = AssociatedObject.Bounds.Height;
                    _savedHeight = h > 0 ? h : DefaultHeight;
                }
                catch { _savedHeight = DefaultHeight; }

                // Subscribe to IsOpen changes and apply collapsed/expanded values.
                _subs.Add(this.GetObservable(IsOpenProperty).Subscribe(new SimpleObserver<bool>(_ => UpdateMaxHeight())));

                // Apply initial state
                UpdateMaxHeight();
            }
        }

        /// <summary>
        /// Apply the MaxHeight value to the associated control according to the
        /// current state of <see cref="IsOpen"/>. Collapses to 0 when true; restores
        /// the saved height when false.
        /// </summary>
        private void UpdateMaxHeight()
        {
            if (AssociatedObject == null) return;

            if (IsOpen)
                AssociatedObject.MaxHeight = 0.0;
            else
                AssociatedObject.MaxHeight = _savedHeight > 0 ? _savedHeight : DefaultHeight;
        }
    }
}
