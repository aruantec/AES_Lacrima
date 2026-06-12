namespace AES_Controls.Composition;

/// <summary>
/// Animation state written by <see cref="CompositionCardGridVisualHandler"/> each compositor frame
/// and read by <see cref="CompositionCardGridControl"/> for hit testing without duplicating physics on the UI thread.
/// </summary>
internal sealed class CardGridAnimationSyncState
{
    public double CurrentScrollY;
    public double TargetScrollY;
    public double VelocityY;
    public bool IsAnimating;
}
