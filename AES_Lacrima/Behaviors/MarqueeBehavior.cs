using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Xaml.Interactivity;
using Avalonia.Visuals;
using Avalonia.VisualTree;
using System;

namespace AES_Lacrima.Behaviors
{
    /// <summary>
    /// When attached to a <see cref="TextBlock"/>, scrolls long text horizontally
    /// in a marquee fashion.  The animation only runs when the rendered text
    /// width exceeds the available width; otherwise the control is left alone.
    /// After the text has scrolled completely out of view it pauses briefly
    /// before jumping back from the right.
    /// </summary>
    public class MarqueeBehavior : Behavior<TextBlock>
    {
        private DispatcherTimer? _timer;
        private double _offset;
        private double _textWidth;
        private double _containerWidth;
        private bool _usingManualWidth;
        private DateTime _pauseEnd;
        private string? _lastText;
        // we avoid reactive constructs; the PropertyChanged event below is used instead

        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject == null)
                return;

            // watch for property changes that might affect the layout
            AssociatedObject.PropertyChanged += OnAnyPropertyChanged;
            AssociatedObject.LayoutUpdated += OnLayoutUpdated;

            // ensure we have a translate transform available
            AssociatedObject.RenderTransform = new TranslateTransform();

            UpdateMetrics();
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            if (AssociatedObject != null)
            {
                AssociatedObject.LayoutUpdated -= OnLayoutUpdated;
                AssociatedObject.PropertyChanged -= OnAnyPropertyChanged;
            }
            StopAnimation();
        }

        private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateMetrics();

        private void OnAnyPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextBlock.TextProperty ||
                e.Property == TextBlock.FontSizeProperty ||
                e.Property == TextBlock.FontFamilyProperty ||
                e.Property == TextBlock.BoundsProperty)
            {
                // When the text itself changes we stop whatever animation was
                // playing and let UpdateMetrics take care of restarting if
                // required. This avoids using stale offset values.
                if (e.Property == TextBlock.TextProperty && _timer != null)
                {
                    StopAnimation();
                }
                UpdateMetrics();
            }
        }

        /// <summary>
        /// Walks up the visual tree from the <see cref="AssociatedObject"/>
        /// looking for the first ancestor that is clipping its contents or
        /// otherwise provides a finite width.  If nothing suitable is found
        /// we fall back to the element's own bounds.  This is what the
        /// scrolling code uses as the "visible area" width.
        /// </summary>
        private double GetClippingWidth()
        {
            if (AssociatedObject == null)
                return 0;

            // start looking from the parent; we want the size of the
            // *container* that clips the marquee, not the textblock itself.
            Visual? current = AssociatedObject as Visual;
            if (current != null)
                current = current.GetVisualParent();

            while (current != null)
            {
                if (current is Control ctrl && ctrl.ClipToBounds)
                {
                    return ctrl.Bounds.Width;
                }
                current = current.GetVisualParent();
            }

            // no ancestor clipped, just return the element's own width
            return AssociatedObject.Bounds.Width;
        }

        private void UpdateMetrics()
        {
            if (AssociatedObject == null)
                return;

            // measure text without wrapping so we know its true width
            var measureBlock = new TextBlock
            {
                Text = AssociatedObject.Text,
                FontSize = AssociatedObject.FontSize,
                FontFamily = AssociatedObject.FontFamily,
                FontStyle = AssociatedObject.FontStyle,
                FontWeight = AssociatedObject.FontWeight,
                FontStretch = AssociatedObject.FontStretch,
                TextWrapping = TextWrapping.NoWrap
            };

            measureBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double newTextWidth = measureBlock.DesiredSize.Width;
            double newContainerWidth;
            if (_timer != null)
            {
                // while animating we freeze the container width so that
                // temporarily changing the TextBlock.Width does not cause
                // premature restarts.  The stored value was captured when the
                // animation started.
                newContainerWidth = _containerWidth;
            }
            else
            {
                newContainerWidth = GetClippingWidth();

                // handle cases where width is not yet available by reusing last
                // non-zero value.  This mirrors the fallback logic used in
                // WidgetControl for similar layout tracking.
                if (newContainerWidth <= 0 && _containerWidth > 0)
                    newContainerWidth = _containerWidth;
            }

            // if nothing has really changed we can bail early; this avoids
            // stomping on the transform every time the layout system ticks
            // (which can happen on every render frame when the element is
            // translating).
            if (newTextWidth == _textWidth && newContainerWidth == _containerWidth &&
                _lastText == AssociatedObject.Text)
            {
                return;
            }

            _textWidth = newTextWidth;
            _containerWidth = newContainerWidth;

            bool textOverflows = _textWidth > _containerWidth;
            const double paddingFactor = 0.4; // fraction of textblock width to offset by

            // restart scroll if the actual text string changed
            if (_lastText != AssociatedObject.Text)
            {
                _lastText = AssociatedObject.Text;
                if (_timer != null)
                {
                    StopAnimation();
                }

                if (textOverflows)
                {
                    // start just off the right edge, with extra padding equal to
                    // paddingFactor of the TextBlock’s current width so the text
                    // never peeks in prematurely.
                    double actualWidth = AssociatedObject.Bounds.Width;
                    _offset = _containerWidth + (actualWidth > 0 ? actualWidth * paddingFactor : 0);
                    if (AssociatedObject?.RenderTransform is TranslateTransform tt) tt.X = _offset;
                    _pauseEnd = DateTime.MinValue;
                }
                else
                {
                    // no scrolling required – make sure we’re sitting at 0 and
                    // clear any manual sizing that might have been applied.
                    _offset = 0;
                    if (AssociatedObject?.RenderTransform is TranslateTransform tt) tt.X = 0;
                    if (_usingManualWidth && AssociatedObject != null)
                    {
                        AssociatedObject.Width = double.NaN;
                        _usingManualWidth = false;
                    }
                }
            }

            if (_containerWidth <= 0)
                return;

            if (textOverflows)
            {
                if (_timer == null)
                    StartAnimation();
            }
            else
            {
                if (_timer != null)
                    StopAnimation();
            }
        }

        private void StartAnimation()
        {
            if (_timer != null)
                return;

            // capture the current container width before we start adjusting the
            // control’s own size; this value is kept constant for the duration
            // of the scroll.  fallback to the element bounds if we failed to
            // acquire a positive width so that we always start outside the
            // visible area.
            _containerWidth = GetClippingWidth();
            if (_containerWidth <= 0 && AssociatedObject != null)
                _containerWidth = AssociatedObject.Bounds.Width; // best effort

            // widen the TextBlock so that its measured width equals the text
            // width.  The parent is responsible for clipping, so expanding our
            // own width should not affect the visible region.
            if (AssociatedObject != null)
            {
                AssociatedObject.Width = _textWidth;
                _usingManualWidth = true;
            }

            _offset = _containerWidth;
            if (AssociatedObject?.RenderTransform is TranslateTransform tt) tt.X = _offset;
            _pauseEnd = DateTime.MinValue;

            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Normal, TimerTick);
            _timer.Start();
        }

        private void StopAnimation()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }

            if (AssociatedObject?.RenderTransform is TranslateTransform tt)
                tt.X = 0;

            if (_usingManualWidth && AssociatedObject != null)
            {
                AssociatedObject.Width = double.NaN;
                _usingManualWidth = false;
            }
        }

        private void TimerTick(object? sender, EventArgs e)
        {
            if (AssociatedObject == null || _timer == null)
                return;

            // pause behaviour
            if (_pauseEnd > DateTime.Now)
                return;

            // reduce speed for a more leisurely scroll
            const double speed = 40; // pixels per second
            double delta = speed * 0.016; // ~16ms interval
            _offset -= delta;

            if (_offset <= -_textWidth)
            {
                // the container may have resized while we paused; recompute
                _containerWidth = GetClippingWidth();
                if (_containerWidth <= 0 && AssociatedObject != null)
                {
                    // if width still isn't valid fall back to last known good
                    _containerWidth = Math.Max(_containerWidth, _containerWidth);
                }

                // apply the same paddingFactor used on initial start
                double actualWidth = AssociatedObject?.Bounds.Width ?? 0;
                const double paddingFactor = 0.4;
                const double wrapPauseSeconds = 0.3; // shorter pause for quicker return
                _offset = _containerWidth + (actualWidth > 0 ? actualWidth * paddingFactor : 0);
                _pauseEnd = DateTime.Now.AddSeconds(wrapPauseSeconds);
            }

            if (AssociatedObject?.RenderTransform is TranslateTransform tt)
                tt.X = _offset;
        }
    }
}
