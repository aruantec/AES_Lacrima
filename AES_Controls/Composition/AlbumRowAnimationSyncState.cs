namespace AES_Controls.Composition;

/// <summary>
/// Animation state written by <see cref="CompositionAlbumRowVisualHandler"/> each compositor frame
/// and read by <see cref="CompositionAlbumRowControl"/> for hit testing without duplicating physics on the UI thread.
/// </summary>
internal sealed class AlbumRowAnimationSyncState
{
    public double CurrentScrollX;
    public double TargetScrollX;
    public double VelocityX;
    public bool IsAnimating;
}
