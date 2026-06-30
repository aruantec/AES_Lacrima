using SkiaSharp;

namespace AES_Controls.Composition;

internal record GameplayPreviewVisualMessage(int Index, bool Visible);

internal record GameplayPreviewPlacementMessage(bool OnCarouselBackground, float BackgroundOpacity);

internal record GameplayPreviewFrameMessage(SKImage? Frame);
