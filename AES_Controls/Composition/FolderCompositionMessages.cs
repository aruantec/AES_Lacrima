using SkiaSharp;

namespace AES_Controls.Composition;

internal sealed record FolderItemSnapshot(SKImage? Cover, bool UseFolderCover);

internal sealed record FolderLayoutRebuildMessage(
    IReadOnlyList<FolderItemSnapshot> Items,
    SKImage? DefaultCover,
    bool Spread,
    int MaxVisibleCovers,
    bool UniformToFill,
    bool SnapToTargets);

internal sealed record FolderSpreadMessage(bool Spread);

internal sealed record FolderPressTargetMessage(double TargetPress);

internal sealed record FolderAnimationPausedMessage(bool IsPaused);

internal sealed record FolderItemCoverMessage(int Index, SKImage? Cover);

internal sealed record FolderAttachSyncMessage(FolderAnimationSyncState State);
