namespace AES_Controls;

/// <summary>
/// Implemented by visuals whose render resolution should be refreshed when
/// <see cref="ScalableDecorator"/> scale exclusion changes.
/// </summary>
public interface IScaleExclusionRenderTarget
{
    void RefreshExclusionRenderSize();
}
