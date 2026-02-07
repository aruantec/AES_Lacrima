using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;

namespace AES_Controls.Composition;

/// <summary>
/// A control that renders a particle system using the Avalonia compositor.
/// This control will attempt to use OpenGL for rendering but will fall back to Skia if GL is not available.
/// </summary>
public class ParticleCompositionControl : Control
{
    /// <summary>
    /// The custom visual for rendering particles.
    /// </summary>
    private CompositionCustomVisual? _visual;

    /// <summary>
    /// Defines the <see cref="ParticleCount"/> property.
    /// </summary>
    public static readonly StyledProperty<int> ParticleCountProperty =
        AvaloniaProperty.Register<ParticleCompositionControl, int>(nameof(ParticleCount), defaultValue: 150);

    /// <summary>
    /// Gets or sets the number of particles to render.
    /// </summary>
    public int ParticleCount { get => GetValue(ParticleCountProperty); set => SetValue(ParticleCountProperty, value); }

    /// <summary>
    /// Defines the <see cref="BackgroundBitmap"/> property.
    /// </summary>
    public static readonly StyledProperty<Bitmap?> BackgroundBitmapProperty =
        AvaloniaProperty.Register<ParticleCompositionControl, Bitmap?>(nameof(BackgroundBitmap));

    /// <summary>
    /// Gets or sets the background image for the particle effect.
    /// </summary>
    public Bitmap? BackgroundBitmap { get => GetValue(BackgroundBitmapProperty); set => SetValue(BackgroundBitmapProperty, value); }

    /// <summary>
    /// Defines the <see cref="StretchBitmap"/> property.
    /// </summary>
    public static readonly StyledProperty<Stretch> StretchBitmapProperty =
        AvaloniaProperty.Register<ParticleCompositionControl, Stretch>(nameof(StretchBitmap), defaultValue: Stretch.UniformToFill);

    /// <summary>
    /// Gets or sets the stretch mode for the background bitmap.
    /// </summary>
    public Stretch StretchBitmap { get => GetValue(StretchBitmapProperty); set => SetValue(StretchBitmapProperty, value); }

    /// <summary>
    /// Defines the <see cref="IsPaused"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsPausedProperty =
        AvaloniaProperty.Register<ParticleCompositionControl, bool>(nameof(IsPaused), defaultValue: false);

    /// <summary>
    /// Gets or sets a value indicating whether the particle animation is paused.
    /// </summary>
    public bool IsPaused { get => GetValue(IsPausedProperty); set => SetValue(IsPausedProperty, value); }

    /// <summary>
    /// Initializes a new instance of the <see cref="ParticleCompositionControl"/> class.
    /// </summary>
    public ParticleCompositionControl() { ClipToBounds = false; }

    /// <summary>
    /// Called when the control is attached to a visual tree.
    /// </summary>
    /// <param name="e">The event arguments.</param>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null) return;
        
        _visual = compositor.CreateCustomVisual(new ParticleVisualHandler());
        ElementComposition.SetElementChildVisual(this, _visual);

        UpdateHandlerSize();
        UpdateHandlerSettings();
        _visual?.SendHandlerMessage("invalidate");
    }

    /// <summary>
    /// Called when the control is detached from a visual tree.
    /// </summary>
    /// <param name="e">The event arguments.</param>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_visual != null)
        {
            _visual.SendHandlerMessage(null!);
            ElementComposition.SetElementChildVisual(this, null);
            _visual = null;
        }
    }

    /// <summary>
    /// Called when a property of the control changes.
    /// </summary>
    /// <param name="change">The property change event arguments.</param>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ParticleCountProperty || change.Property == BackgroundBitmapProperty || change.Property == StretchBitmapProperty || change.Property == IsPausedProperty)
        {
            UpdateHandlerSettings();
        }
        else if (change.Property == BoundsProperty)
        {
            UpdateHandlerSize();
        }
    }

    /// <summary>
    /// Updates the size of the particle visual handler.
    /// </summary>
    private void UpdateHandlerSize()
    {
        if (_visual != null)
        {
            var size = new System.Numerics.Vector2((float)Bounds.Width, (float)Bounds.Height);
            _visual.Size = size;
            _visual.SendHandlerMessage(size);
        }
    }

    /// <summary>
    /// Updates the settings of the particle visual handler.
    /// </summary>
    private void UpdateHandlerSettings()
    {
        if (_visual == null) return;
        _visual.SendHandlerMessage(new ParticleSettingsMessage
        {
            ParticleCount = ParticleCount,
            Background = BackgroundBitmap,
            Stretch = StretchBitmap,
            IsPaused = IsPaused
        });
    }
}