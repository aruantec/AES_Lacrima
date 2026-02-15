using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AES_Controls;

public class LoadingControl : Control
{
    private double _loadingAngle;
    private DispatcherTimer? _timer;
    private bool _isTimerRunning;

    public static readonly StyledProperty<IBrush> ForegroundProperty =
        AvaloniaProperty.Register<LoadingControl, IBrush>(nameof(Foreground), Brushes.White);

    public IBrush Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    static LoadingControl()
    {
        AffectsRender<LoadingControl>(ForegroundProperty);
    }

    public LoadingControl()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Render, (_, _) =>
        {
            _loadingAngle = (_loadingAngle + 1.5) % 360;
            InvalidateVisual();
        });
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            UpdateTimerState();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimerState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopTimer();
    }

    private void UpdateTimerState()
    {
        if (IsVisible && VisualRoot != null) StartTimer();
        else StopTimer();
    }

    private void StartTimer()
    {
        if (!_isTimerRunning && _timer != null)
        {
            _timer.Start();
            _isTimerRunning = true;
        }
    }

    private void StopTimer()
    {
        if (_isTimerRunning && _timer != null)
        {
            _timer.Stop();
            _isTimerRunning = false;
        }
    }

    public override void Render(DrawingContext context)
    {
        if (!IsVisible) return;

        // 1. Determine the maximum radius that fits in the current Bounds
        // We use Math.Min to ensure it stays a circle regardless of container shape
        double diameter = Math.Min(Bounds.Width, Bounds.Height);
        double radius = diameter / 2.0;

        if (radius <= 0) return;

        // 2. Calculate dynamic stroke thickness based on size
        // This prevents the lines from looking like tiny hairs when the control is large
        double thickness = diameter / 10.0;

        // 3. Apply a small padding so the strokes don't clip the edges of the bounds
        double drawRadius = radius - (thickness / 2.0);

        double centerX = Bounds.Width / 2.0;
        double centerY = Bounds.Height / 2.0;

        IBrush brush = Foreground;
        Color color = brush is SolidColorBrush scb ? scb.Color : Colors.White;
        int segments = 12;



        for (int i = 0; i < segments; i++)
        {
            double angle = (_loadingAngle + i * (360.0 / segments)) % 360;
            double rad = angle * Math.PI / 180.0;

            // p1 is the inner point (approx 50% of the radius)
            // p2 is the outer point (the full radius)
            var p1 = new Point(centerX + Math.Cos(rad) * (drawRadius * 0.5),
                               centerY + Math.Sin(rad) * (drawRadius * 0.5));

            var p2 = new Point(centerX + Math.Cos(rad) * drawRadius,
                               centerY + Math.Sin(rad) * drawRadius);

            // Create a pen that scales with the control size
            var pen = new Pen(new SolidColorBrush(color) { Opacity = (i + 1) / (double)segments }, thickness, lineCap: PenLineCap.Round);
            context.DrawLine(pen, p1, p2);
        }
    }
}