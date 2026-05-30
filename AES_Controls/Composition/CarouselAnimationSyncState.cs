namespace AES_Controls.Composition;

/// <summary>
/// Animation state written by <see cref="CompositionCarouselVisualHandler"/> each compositor frame
/// and read by <see cref="CompositionCarouselControl"/> for hit testing without duplicating physics on the UI thread.
/// </summary>
internal sealed class CarouselAnimationSyncState
{
    public double CurrentIndex;
    public double TargetIndex;
    public double Velocity;
    public bool IsAnimating;
}
