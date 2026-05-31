using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace AES_Controls.Composition;

/// <summary>
/// Animated retro/cyberpunk title logo for the application top bar.
/// </summary>
public class TitleLogoCompositionControl : Control, IScaleExclusionRenderTarget
{
    private const int IdleTickIntervalMs = 100;
    private const int BurstTickIntervalMs = 16;

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<TitleLogoCompositionControl, string>(nameof(Title), "AES LACRIMA");

    public static readonly StyledProperty<bool> IsAnimationEnabledProperty =
        AvaloniaProperty.Register<TitleLogoCompositionControl, bool>(nameof(IsAnimationEnabled), false);

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool IsAnimationEnabled
    {
        get => GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
    }

    private CompositionCustomVisual? _visual;
    private TitleLogoCompositionVisualHandler? _handler;
    private DispatcherTimer? _idleTickTimer;
    private DispatcherTimer? _burstTickTimer;

    static TitleLogoCompositionControl()
    {
        AffectsRender<TitleLogoCompositionControl>(TitleProperty, IsAnimationEnabledProperty);
    }

    public TitleLogoCompositionControl()
    {
        ScalableDecorator.SetExcludeFromScale(this, true);
        ScalableDecorator.SetExcludeFromScaleCompensation(this, false);

        ClipToBounds = false;
        Height = 70;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
    }

    public void RefreshExclusionRenderSize() => UpdateVisualSize();

    private bool ShouldAnimate => IsVisible && IsAnimationEnabled;

    private void EnsureVisual()
    {
        if (_visual != null)
            return;

        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null)
            return;

        _handler = new TitleLogoCompositionVisualHandler();
        _visual = compositor.CreateCustomVisual(_handler);
        ElementComposition.SetElementChildVisual(this, _visual);
        UpdateVisualSize();
        SendAllPropertiesToVisual();
    }

    private void DestroyVisual()
    {
        StopIdleTickTimer();
        StopBurstTickTimer();

        _visual?.SendHandlerMessage(null!);
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
        _handler = null;
    }

    private void UpdateVisualSize()
    {
        if (_visual == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
        _visual.Size = size;
        _visual.SendHandlerMessage(size);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (ShouldAnimate)
        {
            EnsureVisual();
            UpdateIdleTickTimer();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DestroyVisual();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateVisualSize();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty || change.Property == IsAnimationEnabledProperty)
        {
            if (ShouldAnimate)
            {
                EnsureVisual();
                UpdateIdleTickTimer();
            }
            else
            {
                DestroyVisual();
            }
        }

        if (_visual == null)
            return;

        if (change.Property == TitleProperty)
            _visual.SendHandlerMessage(new TitleLogoTextMessage(change.GetNewValue<string>() ?? "AES LACRIMA"));
        else if (change.Property == IsAnimationEnabledProperty)
            _visual.SendHandlerMessage(new TitleLogoAnimationMessage(change.GetNewValue<bool>()));
    }

    private void UpdateIdleTickTimer()
    {
        if (ShouldAnimate)
        {
            _idleTickTimer ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(IdleTickIntervalMs),
                DispatcherPriority.Background,
                OnIdleTick);

            if (!_idleTickTimer.IsEnabled)
                _idleTickTimer.Start();
        }
        else
        {
            StopIdleTickTimer();
            StopBurstTickTimer();
        }
    }

    private void StopIdleTickTimer() => _idleTickTimer?.Stop();

    private void StopBurstTickTimer() => _burstTickTimer?.Stop();

    private void UpdateBurstTickTimer()
    {
        if (!ShouldAnimate || _handler == null || !_handler.IsGlitchBurstActive)
        {
            StopBurstTickTimer();
            return;
        }

        _burstTickTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(BurstTickIntervalMs),
            DispatcherPriority.Background,
            OnBurstTick);

        if (!_burstTickTimer.IsEnabled)
            _burstTickTimer.Start();
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        if (!ShouldAnimate || _visual == null)
            return;

        _visual.SendHandlerMessage(TitleLogoIdleTickMessage.Instance);
        UpdateBurstTickTimer();
    }

    private void OnBurstTick(object? sender, EventArgs e)
    {
        if (!ShouldAnimate || _visual == null || _handler == null)
            return;

        _visual.SendHandlerMessage(TitleLogoBurstTickMessage.Instance);

        if (!_handler.IsGlitchBurstActive)
            StopBurstTickTimer();
    }

    private void SendAllPropertiesToVisual()
    {
        if (_visual == null)
            return;

        _visual.SendHandlerMessage(new TitleLogoTextMessage(Title));
        _visual.SendHandlerMessage(new TitleLogoAnimationMessage(IsAnimationEnabled));
    }
}
