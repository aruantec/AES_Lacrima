using System.Diagnostics;
using AES_Controls.Player.Models;
using System.Numerics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace AES_Controls.Composition;

internal record AlbumRowScrollMessage(double TargetScrollX);
internal record AlbumRowScrollVelocityMessage(double VelocityX);
internal record AlbumRowDirectScrollFollowMessage(bool Enabled);
internal record AlbumRowSnapScrollMessage(double ScrollX);
internal record AlbumRowLayoutMessage(float TileScale, float TileSpacing);
internal record AlbumRowTitlesMessage(string[] Titles, int[] TrackCounts, bool[] LoadingFlags);
internal record AlbumRowSelectedIndexMessage(int Index);
internal record AlbumRowHoveredIndexMessage(int Index);
internal record AlbumRowScrollFrozenMessage(bool Frozen);
internal record AlbumRowScrollbarPressedMessage(bool IsPressed);
internal record AlbumRowScrollbarHoverMessage(bool IsHovered);
internal record AlbumRowResetScrollbarMessage();
internal record AlbumRowAttachSyncMessage(AlbumRowAnimationSyncState State);
internal record AlbumRowTileCoversMessage(int Index, IReadOnlyList<FolderItemSnapshot> Snapshots, SKImage? DefaultCover);
internal record AlbumRowSwapTileCoversMessage(int FromIndex, int ToIndex);
internal record AlbumRowDragStateMessage(int Index, bool IsDragging);
internal record AlbumRowDragPositionMessage(Vector2 Position);
internal record AlbumRowDropTargetMessage(int Index);
internal record AlbumRowDragCancelMessage();
internal record AlbumRowDragCommitMessage(int TargetIndex);
internal record AlbumRowDragFinalizeMessage();
internal record AlbumRowBackgroundColorMessage(SKColor Color);
internal record AlbumRowRenamingIndexMessage(int Index);

internal sealed class CompositionAlbumRowVisualHandler : CompositionCustomVisualHandler
{
    private const float TileCornerRadius = 12f;
    private const float CoverCornerRadius = 12f;
    private const int MaxVisibleCovers = FolderMediaItem.AlbumTilePresentationCoverCount;
    private const float SwapAnimationSeconds = 0.2f;
    private const float DragCommitSeconds = 0.3f;
    private const float DragLiftScale = 1.04f;
    private const float HoverLiftScale = 0.034f;
    private static readonly SKColor DefaultBackgroundColor = SKColor.Parse("#101010");
    private static readonly SKColor TileBackground = SKColor.Parse("#111111");
    private static readonly SKColor TileBorder = SKColor.Parse("#1A1A1A");
    private static readonly SKColor TitleBarColor = SKColor.Parse("#CC000000");
    private static readonly SKColor BadgeBackground = SKColor.Parse("#222222");
    private static readonly SKColor LoadingOverlay = SKColor.Parse("#66000000");

    private Vector2 _visualSize;
    private float _tileScale = 1f;
    private float _tileSpacing;
    private double _targetScrollX;
    private double _currentScrollX;
    private double _scrollVelocity;
    private double _scrollSpringVelocity;
    private bool _directScrollFollow;
    private bool _scrollFrozen;
    private long _lastTicks;
    private bool _isScrollbarPressed;
    private bool _isScrollbarHovered;
    private long _scrollbarVisibleUntilTicks;
    private float _scrollbarOpacity;
    private float _scrollbarOpacityVelocity;
    private int _selectedIndex = -1;
    private int _hoveredIndex = -1;
    private int _renamingIndex = -1;
    private float _selectionBorderFade;
    private float _selectionPulsePhase;
    private AlbumRowAnimationSyncState? _animationSync;
    private int _draggingIndex = -1;
    private int _dropTargetIndex = -1;
    private Vector2 _dragPosition;
    private Vector2 _dragCommitStartPosition;
    private bool _isDragCommitting;
    private float _dragCommitProgress;
    private readonly Dictionary<int, Vector2> _swapOffsets = new();
    private readonly Dictionary<int, Vector2> _swapOffsetTargets = new();
    private readonly Dictionary<int, float> _hoverLift = new();
    private float _spinnerRotation;

    private readonly List<AlbumTileVisual> _tiles = [];
    private string[] _titles = [];
    private int[] _trackCounts = [];
    private bool[] _loadingFlags = [];

    private SKColor _backgroundColor = DefaultBackgroundColor;
    private float _currentGlobalOpacity = 1f;
    private float _targetGlobalOpacity = 1f;
    private float _currentGlobalOpacityVelocity;
    private readonly SKPaint _paint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
    private readonly SKPaint _titlePaint = CreateTitlePaint(SKColors.White);
    private readonly SKPaint _badgePaint = CreateTitlePaint(SKColors.White);
    private readonly SKPaint _overlayPaint = new() { IsAntialias = true };
    private readonly SKPaint _scrollbarPaint = new() { IsAntialias = true };
    private readonly SKPaint _spinnerPaint = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
    private readonly SKMaskFilter _scrollbarBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3);
    private readonly SKMaskFilter _selectionGlowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f);

    private sealed class AlbumTileVisual
    {
        public SKImage? DefaultCover;
        public readonly List<FolderItemSnapshot> Snapshots = [];
        public readonly AlbumTileFolderAnimator Folder = new();
    }

    private sealed class AlbumTileFolderAnimator
    {
        private sealed class LayerState
        {
            public int SnapshotIndex;
            public SKImage? Cover;
            public bool UseFolderCover;
            public double CurX;
            public double CurY;
            public double CurOpacity = 1;
            public double TgtX;
            public double TgtY;
            public double TgtOpacity = 1;
            public int ZIndex;
            public bool IsTarget;
        }

        private readonly List<LayerState> _layers = [];
        private bool _spread;
        private float _coverWidth = 240f;
        private float _coverHeight = 170f;

        public void Rebuild(IReadOnlyList<FolderItemSnapshot> snapshots, SKImage? defaultCover, bool spread, bool snap)
        {
            foreach (var snapshot in SnapshotsOwned())
            {
                if (!snapshot.UseFolderCover)
                    snapshot.Cover?.Dispose();
            }

            _layers.Clear();
            Snapshots = snapshots.ToList();
            DefaultCover = defaultCover;
            _spread = spread;
            RecomputeTargets(snap);
        }

        private List<FolderItemSnapshot> Snapshots { get; set; } = [];
        public SKImage? DefaultCover { get; private set; }

        public void SetSpread(bool spread, bool snap)
        {
            if (_spread == spread)
                return;
            _spread = spread;
            RecomputeTargets(snap);
        }

        public bool HasSameSnapshotStructure(IReadOnlyList<FolderItemSnapshot> snapshots)
        {
            if (Snapshots.Count != snapshots.Count)
                return false;

            for (int i = 0; i < snapshots.Count; i++)
            {
                if (Snapshots[i].UseFolderCover != snapshots[i].UseFolderCover)
                    return false;
            }

            return true;
        }

        public void UpdateCoverImages(IReadOnlyList<FolderItemSnapshot> snapshots, SKImage? defaultCover)
        {
            Snapshots = snapshots.ToList();
            DefaultCover = defaultCover;

            foreach (var layer in _layers)
            {
                if (layer.SnapshotIndex < 0 || layer.SnapshotIndex >= Snapshots.Count)
                    continue;

                var itemSnap = Snapshots[layer.SnapshotIndex];
                layer.Cover = itemSnap.UseFolderCover ? null : itemSnap.Cover;
                layer.UseFolderCover = itemSnap.UseFolderCover;
            }
        }

        public bool Update(double speed)
        {
            bool any = false;
            for (int i = _layers.Count - 1; i >= 0; i--)
            {
                var layer = _layers[i];
                double dx = layer.TgtX - layer.CurX;
                double dy = layer.TgtY - layer.CurY;
                if (Math.Abs(dx) > 0.1 || Math.Abs(dy) > 0.1)
                {
                    layer.CurX += dx * speed;
                    layer.CurY += dy * speed;
                    any = true;
                }
                else
                {
                    layer.CurX = layer.TgtX;
                    layer.CurY = layer.TgtY;
                }

                layer.CurOpacity = layer.TgtOpacity;
                if (!any && (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01))
                    any = true;

                if (!any && layer.CurOpacity <= 0.005 && !layer.IsTarget)
                    _layers.RemoveAt(i);
            }

            return any;
        }

        public void Draw(SKCanvas canvas, SKRect coverArea)
        {
            if (_layers.Count == 0)
                return;

            if (Math.Abs(_coverWidth - coverArea.Width) > 0.5f || Math.Abs(_coverHeight - coverArea.Height) > 0.5f)
            {
                _coverWidth = coverArea.Width;
                _coverHeight = coverArea.Height;
                RecomputeTargets(snap: true);
            }

            float itemSize = Math.Max(coverArea.Width, coverArea.Height);
            var sorted = _layers.OrderBy(l => l.ZIndex).ToArray();
            foreach (var layer in sorted)
            {
                if (layer.CurOpacity <= 0.001)
                    continue;

                float drawX = coverArea.Left + (float)layer.CurX;
                float drawY = coverArea.Top + (float)layer.CurY;
                var dest = new SKRect(drawX, drawY, drawX + itemSize, drawY + itemSize);

                if (layer.UseFolderCover && DefaultCover != null)
                    DrawCover(canvas, DefaultCover, dest, layer.CurOpacity);
                else if (layer.Cover != null)
                    DrawCover(canvas, layer.Cover, dest, layer.CurOpacity);
            }

            void DrawCover(SKCanvas c, SKImage cover, SKRect dest, double opacity)
            {
                if (dest.Width <= 0.5f || dest.Height <= 0.5f || opacity <= 0.001)
                    return;

                byte alpha = (byte)Math.Clamp((int)(255 * opacity), 0, 255);
                using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium, Color = SKColors.White.WithAlpha(alpha) };
                var src = UniformToFillSrc(cover.Width, cover.Height, dest);
                c.DrawImage(cover, src, dest, paint);
            }
        }

        private void RecomputeTargets(bool snap)
        {
            float w = _coverWidth;
            float itemSize = Math.Max(w, _coverHeight);
            float baseXStacked = w - itemSize;
            float marginHover = itemSize * 0.18f;

            int count = Snapshots.Count;
            int visible = Math.Min(count, MaxVisibleCovers);
            int startIndex = Math.Max(0, count - MaxVisibleCovers);

            foreach (var layer in _layers)
                layer.IsTarget = false;

            for (int i = 0; i < visible; i++)
            {
                int snapshotIndex = startIndex + i;
                var itemSnap = Snapshots[snapshotIndex];
                float tx = _spread ? baseXStacked - (i * marginHover) : baseXStacked;
                float ty = 0;

                var layer = _layers.FirstOrDefault(l => l.SnapshotIndex == snapshotIndex);
                if (layer == null)
                {
                    layer = new LayerState
                    {
                        SnapshotIndex = snapshotIndex,
                        CurX = baseXStacked,
                        CurY = ty,
                        Cover = itemSnap.UseFolderCover ? null : itemSnap.Cover,
                        UseFolderCover = itemSnap.UseFolderCover,
                        ZIndex = i
                    };
                    _layers.Add(layer);
                }

                layer.IsTarget = true;
                layer.ZIndex = i;
                layer.TgtX = tx;
                layer.TgtY = ty;
                layer.TgtOpacity = 1;
                layer.Cover = itemSnap.UseFolderCover ? null : itemSnap.Cover;
                layer.UseFolderCover = itemSnap.UseFolderCover;
            }

            foreach (var layer in _layers)
            {
                if (!layer.IsTarget)
                    layer.TgtOpacity = 0;
            }

            if (snap)
            {
                foreach (var layer in _layers)
                {
                    layer.CurX = layer.TgtX;
                    layer.CurY = layer.TgtY;
                    layer.CurOpacity = layer.TgtOpacity;
                }
            }
        }

        private IEnumerable<FolderItemSnapshot> SnapshotsOwned() => Snapshots;

        private static SKRect UniformToFillSrc(float srcW, float srcH, SKRect dest)
        {
            float srcAspect = srcW / srcH;
            float destAspect = dest.Width / dest.Height;
            float cropW = srcW;
            float cropH = srcH;
            float cropX = 0;
            float cropY = 0;

            if (srcAspect > destAspect)
            {
                cropW = srcH * destAspect;
                cropX = (srcW - cropW) / 2f;
            }
            else
            {
                cropH = srcW / destAspect;
                cropY = (srcH - cropH) / 2f;
            }

            return new SKRect(cropX, cropY, cropX + cropW, cropY + cropH);
        }
    }

    public override void OnMessage(object message)
    {
        switch (message)
        {
            case null:
                ReleaseAllTileImages();
                _tiles.Clear();
                return;
            case Vector2 size:
                _visualSize = size;
                Invalidate();
                return;
            case AlbumRowLayoutMessage layout:
                _tileScale = layout.TileScale;
                _tileSpacing = layout.TileSpacing;
                Invalidate();
                return;
            case AlbumRowTitlesMessage titles:
                _titles = titles.Titles;
                _trackCounts = titles.TrackCounts;
                _loadingFlags = titles.LoadingFlags;
                EnsureTileCount(_titles.Length);
                Invalidate();
                return;
            case AlbumRowSwapTileCoversMessage swap:
                if (swap.FromIndex >= 0 &&
                    swap.ToIndex >= 0 &&
                    swap.FromIndex < _tiles.Count &&
                    swap.ToIndex < _tiles.Count &&
                    swap.FromIndex != swap.ToIndex)
                {
                    (_tiles[swap.FromIndex], _tiles[swap.ToIndex]) = (_tiles[swap.ToIndex], _tiles[swap.FromIndex]);
                    Invalidate();
                }

                return;
            case AlbumRowTileCoversMessage covers:
                EnsureTileCount(Math.Max(_tiles.Count, covers.Index + 1));
                if (covers.Index >= 0 && covers.Index < _tiles.Count)
                {
                    var tile = _tiles[covers.Index];
                    bool keepFolderClosed = _scrollFrozen || _isDragCommitting || _draggingIndex >= 0;
                    bool spread = !keepFolderClosed && covers.Index == _hoveredIndex;

                    if (tile.Folder.HasSameSnapshotStructure(covers.Snapshots))
                    {
                        for (int i = 0; i < tile.Snapshots.Count; i++)
                        {
                            var previous = tile.Snapshots[i];
                            var next = covers.Snapshots[i];
                            if (!previous.UseFolderCover && previous.Cover != next.Cover)
                                previous.Cover?.Dispose();
                        }

                        if (tile.DefaultCover != covers.DefaultCover)
                            tile.DefaultCover?.Dispose();

                        tile.DefaultCover = covers.DefaultCover;
                        tile.Snapshots.Clear();
                        tile.Snapshots.AddRange(covers.Snapshots);
                        tile.Folder.UpdateCoverImages(covers.Snapshots, covers.DefaultCover);
                    }
                    else
                    {
                        ReleaseTileImages(tile);
                        tile.DefaultCover = covers.DefaultCover;
                        tile.Snapshots.Clear();
                        tile.Snapshots.AddRange(covers.Snapshots);
                        tile.Folder.Rebuild(tile.Snapshots, tile.DefaultCover, spread, snap: keepFolderClosed || !spread);
                    }
                }
                Invalidate();
                return;
            case AlbumRowScrollMessage scroll:
                _targetScrollX = scroll.TargetScrollX;
                _scrollbarVisibleUntilTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.2);
                EnsureAnimationLoop();
                return;
            case AlbumRowScrollVelocityMessage velocity:
                _scrollVelocity = velocity.VelocityX;
                _scrollbarVisibleUntilTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.2);
                EnsureAnimationLoop();
                return;
            case AlbumRowDirectScrollFollowMessage follow:
                _directScrollFollow = follow.Enabled;
                EnsureAnimationLoop();
                return;
            case AlbumRowSnapScrollMessage snap:
                _targetScrollX = snap.ScrollX;
                _currentScrollX = snap.ScrollX;
                _scrollVelocity = 0;
                _scrollSpringVelocity = 0;
                Invalidate();
                return;
            case AlbumRowSelectedIndexMessage selected:
                _selectedIndex = selected.Index;
                EnsureAnimationLoop();
                return;
            case AlbumRowHoveredIndexMessage hovered:
                if (_hoveredIndex != hovered.Index)
                {
                    if (_hoveredIndex >= 0 && _hoveredIndex < _tiles.Count && !_scrollFrozen)
                        _tiles[_hoveredIndex].Folder.SetSpread(false, snap: false);
                    _hoveredIndex = hovered.Index;
                    if (_hoveredIndex >= 0)
                    {
                        if (!_hoverLift.ContainsKey(_hoveredIndex))
                            _hoverLift[_hoveredIndex] = 0f;
                        bool canSpread = !_scrollFrozen && _draggingIndex < 0 && !_isDragCommitting;
                        if (canSpread && _hoveredIndex < _tiles.Count)
                            _tiles[_hoveredIndex].Folder.SetSpread(true, snap: false);
                    }
                }
                EnsureAnimationLoop();
                return;
            case AlbumRowBackgroundColorMessage background:
                _backgroundColor = background.Color;
                Invalidate();
                return;
            case AlbumRowRenamingIndexMessage renaming:
                _renamingIndex = renaming.Index;
                Invalidate();
                return;
            case GlobalOpacityMessage opacity:
                _targetGlobalOpacity = (float)Math.Clamp(opacity.Value, 0.0, 1.0);
                if (_lastTicks == 0)
                    _lastTicks = Stopwatch.GetTimestamp();
                EnsureAnimationLoop();
                return;
            case AlbumRowScrollFrozenMessage frozen:
                _scrollFrozen = frozen.Frozen;
                if (_scrollFrozen)
                {
                    for (int i = 0; i < _tiles.Count; i++)
                        _tiles[i].Folder.SetSpread(false, snap: true);
                }
                return;
            case AlbumRowScrollbarPressedMessage pressed:
                _isScrollbarPressed = pressed.IsPressed;
                EnsureAnimationLoop();
                return;
            case AlbumRowScrollbarHoverMessage hover:
                _isScrollbarHovered = hover.IsHovered;
                EnsureAnimationLoop();
                return;
            case AlbumRowResetScrollbarMessage:
                _scrollbarOpacity = 0;
                _scrollbarOpacityVelocity = 0;
                return;
            case AlbumRowAttachSyncMessage attach:
                _animationSync = attach.State;
                return;
            case AlbumRowDragStateMessage drag:
                if (drag.IsDragging)
                {
                    _draggingIndex = drag.Index;
                    _dropTargetIndex = drag.Index;
                    _isDragCommitting = false;
                    _dragCommitProgress = 0f;
                    _swapOffsets.Clear();
                    _swapOffsetTargets.Clear();
                    _hoveredIndex = -1;
                    for (int i = 0; i < _tiles.Count; i++)
                        _tiles[i].Folder.SetSpread(false, snap: true);
                }
                else if (!_isDragCommitting)
                {
                    ClearDragVisualState();
                }

                EnsureAnimationLoop();
                return;
            case AlbumRowDragPositionMessage pos:
                _dragPosition = pos.Position;
                if (_draggingIndex != -1 && !_isDragCommitting)
                    EnsureAnimationLoop();
                else
                    Invalidate();
                return;
            case AlbumRowDropTargetMessage drop:
                if (drop.Index != _dropTargetIndex)
                {
                    _dropTargetIndex = drop.Index;
                    UpdateSwapOffsetTargets();
                }
                EnsureAnimationLoop();
                return;
            case AlbumRowDragCancelMessage:
                ClearDragVisualState();
                EnsureAnimationLoop();
                return;
            case AlbumRowDragCommitMessage commit:
                _dropTargetIndex = commit.TargetIndex;
                _dragCommitStartPosition = _dragPosition;
                _isDragCommitting = true;
                _dragCommitProgress = 0f;
                EnsureAnimationLoop();
                return;
            case AlbumRowDragFinalizeMessage:
                ClearDragVisualState();
                for (int i = 0; i < _tiles.Count; i++)
                    _tiles[i].Folder.SetSpread(false, snap: true);
                Invalidate();
                return;
        }
    }

    public override void OnAnimationFrameUpdate()
    {
        long currentTicks = Stopwatch.GetTimestamp();
        if (_lastTicks == 0) _lastTicks = currentTicks;
        double dt = (double)(currentTicks - _lastTicks) / Stopwatch.Frequency;
        _lastTicks = currentTicks;
        if (dt > 0.1) dt = 0.1;

        var metrics = ComputeMetrics(_titles.Length);
        double maxScroll = metrics.MaxScrollX;
        double speed = 1.0 - Math.Pow(1.0 - 0.12, dt * 60.0);

        if (_directScrollFollow)
        {
            _currentScrollX = _targetScrollX;
            _scrollVelocity = 0;
            _scrollSpringVelocity = 0;
        }
        else
        {
            if (Math.Abs(_scrollVelocity) > 0.5)
            {
                _targetScrollX += _scrollVelocity * dt;
                _scrollVelocity *= Math.Exp(-2.15 * dt);
            }
            else
            {
                _scrollVelocity = 0;
            }

            if (_targetScrollX < 0)
            {
                _targetScrollX += (-_targetScrollX) * Math.Min(1.0, 12.0 * dt);
                if (Math.Abs(_targetScrollX) < 0.5 && Math.Abs(_scrollVelocity) < 2)
                    _targetScrollX = 0;
            }
            else if (_targetScrollX > maxScroll)
            {
                double overshoot = _targetScrollX - maxScroll;
                _targetScrollX -= overshoot * Math.Min(1.0, 12.0 * dt);
                if (overshoot < 0.5 && Math.Abs(_scrollVelocity) < 2)
                    _targetScrollX = maxScroll;
            }

            double distance = _targetScrollX - _currentScrollX;
            double stiffness = 420.0;
            double damping = 2.0 * Math.Sqrt(stiffness) * 0.92;
            _scrollSpringVelocity += (distance * stiffness - _scrollSpringVelocity * damping) * dt;
            _currentScrollX += _scrollSpringVelocity * dt;

            if (Math.Abs(distance) < 0.01 && Math.Abs(_scrollSpringVelocity) < 0.01)
            {
                _currentScrollX = _targetScrollX;
                _scrollSpringVelocity = 0;
            }
        }

        bool folderAnimating = false;
        if (!_scrollFrozen)
        {
            for (int i = 0; i < _tiles.Count; i++)
                folderAnimating |= _tiles[i].Folder.Update(speed);
        }

        bool needsScrollbar = maxScroll > 1;
        bool scrollActive = _directScrollFollow ||
                            Math.Abs(_scrollVelocity) > 2 ||
                            Stopwatch.GetTimestamp() < _scrollbarVisibleUntilTicks;
        float desiredScrollbarOpacity = needsScrollbar && (_isScrollbarPressed || _isScrollbarHovered || scrollActive) ? 1f : 0f;

        if (Math.Abs(_scrollbarOpacity - desiredScrollbarOpacity) > 0.001f || Math.Abs(_scrollbarOpacityVelocity) > 0.001f)
        {
            double opStiffness = 45.0;
            double opDamping = 2.0 * Math.Sqrt(opStiffness);
            _scrollbarOpacityVelocity += (float)((desiredScrollbarOpacity - _scrollbarOpacity) * opStiffness - _scrollbarOpacityVelocity * opDamping) * (float)dt;
            _scrollbarOpacity += _scrollbarOpacityVelocity * (float)dt;
            _scrollbarOpacity = Math.Clamp(_scrollbarOpacity, 0f, 1f);
        }
        else
        {
            _scrollbarOpacity = desiredScrollbarOpacity;
            _scrollbarOpacityVelocity = 0;
        }

        float fadeTarget = _selectedIndex >= 0 ? 1f : 0f;
        bool selectionFading = Math.Abs(_selectionBorderFade - fadeTarget) > 0.001f;
        if (selectionFading)
        {
            float rate = fadeTarget > _selectionBorderFade ? 18f : 22f;
            _selectionBorderFade += (fadeTarget - _selectionBorderFade) * Math.Min(1f, (float)dt * rate);
            _selectionBorderFade = Math.Clamp(_selectionBorderFade, 0f, 1f);
        }
        else
        {
            _selectionBorderFade = fadeTarget;
        }

        bool selectionPulsing = _selectedIndex >= 0 && _selectionBorderFade > 0.2f;
        if (selectionPulsing)
            _selectionPulsePhase += (float)dt * 3.1f;

        if (Math.Abs(_currentGlobalOpacity - _targetGlobalOpacity) > 0.0005f || Math.Abs(_currentGlobalOpacityVelocity) > 0.0005f)
        {
            double opStiffness = 30.0;
            double opDamping = 2.0 * Math.Sqrt(opStiffness);
            _currentGlobalOpacityVelocity += (float)((_targetGlobalOpacity - _currentGlobalOpacity) * opStiffness - _currentGlobalOpacityVelocity * opDamping) * (float)dt;
            _currentGlobalOpacity += _currentGlobalOpacityVelocity * (float)dt;
            _currentGlobalOpacity = Math.Clamp(_currentGlobalOpacity, 0f, 1f);
        }

        bool dragAnimating = AnimateSwapOffsets((float)dt);
        if (_isDragCommitting && _dragCommitProgress < 1f)
        {
            _dragCommitProgress += (float)(dt / DragCommitSeconds);
            if (_dragCommitProgress > 1f)
                _dragCommitProgress = 1f;
            dragAnimating = true;
        }

        bool hoverAnimating = false;
        if (_hoverLift.Count > 0)
        {
            var finished = new List<int>();
            foreach (var index in _hoverLift.Keys.ToArray())
            {
                float target = index == _hoveredIndex ? 1f : 0f;
                float strength = _hoverLift[index];
                if (Math.Abs(strength - target) > 0.001f)
                {
                    strength += (target - strength) * Math.Min(1f, (float)dt * 14f);
                    _hoverLift[index] = strength;
                    hoverAnimating = true;
                }
                else
                {
                    _hoverLift[index] = target;
                    if (target <= 0f)
                        finished.Add(index);
                }
            }

            foreach (var index in finished)
                _hoverLift.Remove(index);
        }

        bool isAnimating = _directScrollFollow ||
                           Math.Abs(_targetScrollX - _currentScrollX) > 0.01 ||
                           Math.Abs(_scrollVelocity) > 0.5 ||
                           Math.Abs(_scrollSpringVelocity) > 0.01 ||
                           Math.Abs(_scrollbarOpacity - desiredScrollbarOpacity) > 0.01 ||
                           Math.Abs(_currentGlobalOpacity - _targetGlobalOpacity) > 0.001f ||
                           selectionFading ||
                           selectionPulsing ||
                           folderAnimating ||
                           hoverAnimating ||
                           dragAnimating;
        bool animateSpinners = _loadingFlags.Any(static f => f);

        if (isAnimating || animateSpinners)
        {
            _spinnerRotation = (_spinnerRotation + 8f) % 360f;
            RegisterForNextAnimationFrameUpdate();
            Invalidate();
        }
        else
        {
            _lastTicks = 0;
        }

        if (_animationSync != null)
        {
            _animationSync.CurrentScrollX = _currentScrollX;
            _animationSync.TargetScrollX = _targetScrollX;
            _animationSync.VelocityX = _scrollVelocity;
            _animationSync.IsAnimating = isAnimating || animateSpinners;
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null)
            return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;
        canvas.Clear(_backgroundColor);

        if (_visualSize.X <= 0 || _visualSize.Y <= 0)
            return;

        if (_titles.Length == 0)
            return;

        float g = Math.Clamp(_currentGlobalOpacity, 0f, 1f);
        if (g <= 0f)
            return;

        var metrics = ComputeMetrics(_titles.Length);
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, _visualSize.X, _visualSize.Y));

        canvas.SaveLayer(new SKPaint { Color = SKColors.White.WithAlpha((byte)(g * 255)) });

        bool isDragging = _draggingIndex != -1;
        if (isDragging)
        {
            if (_draggingIndex >= 0 &&
                TryGetTilePosition(_draggingIndex, metrics, out float holeX, out float holeY))
            {
                DrawPlaceholder(canvas, holeX, holeY, metrics.TileWidth, metrics.TileHeight);
            }

            for (int index = 0; index < _titles.Length; index++)
            {
                if (index == _draggingIndex || index == _selectedIndex)
                    continue;

                if (!TryGetTileDrawPosition(index, metrics, out float x, out float y))
                    continue;
                DrawAlbumTile(canvas, index, x, y, metrics, 1f);
            }
        }
        else
        {
            var (start, end) = AlbumRowLayoutHelper.GetVisibleIndexRange(
                _currentScrollX, _visualSize.X, _visualSize.Y, _titles.Length, _tileScale, _tileSpacing);

            for (int index = start; index <= end; index++)
            {
                if (index == _selectedIndex)
                    continue;
                if (!TryGetTileDrawPosition(index, metrics, out float x, out float y))
                    continue;
                DrawAlbumTile(canvas, index, x, y, metrics, 1f);
            }
        }

        if (_selectedIndex >= 0 &&
            _selectedIndex < _titles.Length &&
            _selectedIndex != _draggingIndex &&
            TryGetTileDrawPosition(_selectedIndex, metrics, out float selX, out float selY))
        {
            DrawAlbumTile(canvas, _selectedIndex, selX, selY, metrics, 1f);
        }

        if (isDragging && _draggingIndex >= 0 && _draggingIndex < _titles.Length)
            DrawDraggedTile(canvas, _draggingIndex, metrics);

        DrawScrollbar(canvas, metrics);
        canvas.Restore();
        canvas.Restore();
    }

    private void DrawAlbumTile(SKCanvas canvas, int index, float x, float y, RowMetrics metrics, float alpha)
    {
        float w = metrics.TileWidth;
        float h = metrics.TileHeight;
        var tileRect = new SKRect(x, y, x + w, y + h);
        bool isSelected = index == _selectedIndex;
        float borderFade = isSelected ? _selectionBorderFade : 0f;
        float hoverLift = _hoverLift.TryGetValue(index, out var hoverStrength) ? hoverStrength : 0f;
        float easedHover = EaseOutCubic(hoverLift);
        float scale = isSelected
            ? 1f + AlbumRowLayoutHelper.SelectionLiftScale * borderFade
            : 1f + HoverLiftScale * easedHover;

        canvas.Save();
        canvas.Translate(x, y);
        if (Math.Abs(scale - 1f) > 0.001f)
        {
            canvas.Translate(w / 2f, h / 2f);
            canvas.Scale(scale, scale);
            canvas.Translate(-w / 2f, -h / 2f);
        }

        var localRect = new SKRect(0, 0, w, h);
        using (var clipPath = new SKPath())
        {
            clipPath.AddRoundRect(localRect, TileCornerRadius, TileCornerRadius);
            canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);
        }
        using (var bgPaint = new SKPaint { IsAntialias = true, Color = TileBackground })
            canvas.DrawRoundRect(localRect, TileCornerRadius, TileCornerRadius, bgPaint);
        using (var borderPaint = new SKPaint { IsAntialias = true, Color = TileBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
            canvas.DrawRoundRect(localRect, TileCornerRadius, TileCornerRadius, borderPaint);

        float coverHeight = h - AlbumRowLayoutHelper.TitleBarHeight;
        var coverRect = new SKRect(0, 0, w, coverHeight);
        canvas.Save();
        using (var clipRound = new SKRoundRect(coverRect, CoverCornerRadius, CoverCornerRadius))
            canvas.ClipRoundRect(clipRound);
        if (index < _tiles.Count)
            _tiles[index].Folder.Draw(canvas, coverRect);
        canvas.Restore();

        var titleRect = new SKRect(0, coverHeight, w, h);
        bool isRenaming = index == _renamingIndex;
        if (!isRenaming)
        {
            using (var titleBg = new SKPaint { IsAntialias = true, Color = TitleBarColor })
                canvas.DrawRect(titleRect, titleBg);

            string title = index < _titles.Length ? _titles[index] : string.Empty;
            _titlePaint.TextSize = 18f;
            CompositionSkiaTextHelper.ConfigurePaint(_titlePaint);
            var lines = CompositionSkiaTextHelper.WrapTextLines(title, w - 16f, _titlePaint, 2);
            float lineHeight = 22f;
            float textBlockHeight = lines.Count * lineHeight;
            float textY = coverHeight + (AlbumRowLayoutHelper.TitleBarHeight - textBlockHeight) / 2f + 16f;
            foreach (var line in lines)
            {
                float textWidth = CompositionSkiaTextHelper.MeasureText(line, _titlePaint);
                float textX = (w - textWidth) / 2f;
                CompositionSkiaTextHelper.DrawText(canvas, line, textX, textY, _titlePaint);
                textY += lineHeight;
            }
        }
        else
        {
            using (var titleBg = new SKPaint { IsAntialias = true, Color = TitleBarColor })
                canvas.DrawRect(titleRect, titleBg);
        }

        int count = index < _trackCounts.Length ? _trackCounts[index] : 0;
        if (count > 0)
        {
            string countText = count.ToString();
            _badgePaint.TextSize = 20f;
            float badgeTextWidth = CompositionSkiaTextHelper.MeasureText(countText, _badgePaint);
            float badgeW = Math.Max(24f, badgeTextWidth + 12f);
            var badgeRect = new SKRect(w - badgeW - 12f, 12f, w - 12f, 12f + 24f);
            using var badgeBg = new SKPaint { IsAntialias = true, Color = BadgeBackground.WithAlpha(179) };
            canvas.DrawRoundRect(badgeRect, 12f, 12f, badgeBg);
            CompositionSkiaTextHelper.DrawText(canvas, countText, badgeRect.MidX - badgeTextWidth / 2f, badgeRect.MidY + 7f, _badgePaint);
        }

        if (index < _loadingFlags.Length && _loadingFlags[index])
        {
            using var loadPaint = new SKPaint { IsAntialias = true, Color = LoadingOverlay };
            canvas.DrawRoundRect(localRect, TileCornerRadius, TileCornerRadius, loadPaint);
            DrawSpinner(canvas, w / 2f, h / 2f);
        }

        canvas.Restore();

        if (isSelected && borderFade > 0.001f)
        {
            float pulse = 0.72f + 0.28f * (0.5f + 0.5f * MathF.Sin(_selectionPulsePhase));
            canvas.Save();
            canvas.Translate(x, y);
            if (Math.Abs(scale - 1f) > 0.001f)
            {
                canvas.Translate(w / 2f, h / 2f);
                canvas.Scale(scale, scale);
                canvas.Translate(-w / 2f, -h / 2f);
            }

            DrawSelectionGlowBorder(canvas, localRect, borderFade, pulse);
            canvas.Restore();
        }
    }

    private void DrawDraggedTile(SKCanvas canvas, int index, RowMetrics metrics)
    {
        float w = metrics.TileWidth;
        float h = metrics.TileHeight;
        float cx = _dragPosition.X;
        float cy = _dragPosition.Y;
        float scale = DragLiftScale;

        if (_isDragCommitting && TryGetTilePosition(_dropTargetIndex, metrics, out float targetX, out float targetY))
        {
            float eased = EaseOutCubic(_dragCommitProgress);
            float targetCx = targetX + w * 0.5f;
            float targetCy = targetY + h * 0.5f;
            cx = _dragCommitStartPosition.X + (targetCx - _dragCommitStartPosition.X) * eased;
            cy = _dragCommitStartPosition.Y + (targetCy - _dragCommitStartPosition.Y) * eased;
            scale = DragLiftScale + (1f - DragLiftScale) * eased;
        }

        float x = cx - w * 0.5f;
        float y = cy - h * 0.5f;
        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.Scale(scale, scale);
        canvas.Translate(-cx, -cy);
        DrawAlbumTile(canvas, index, x, y, metrics, 1f);
        canvas.Restore();
    }

    private static float EaseOutCubic(float t) => 1f - MathF.Pow(1f - Math.Clamp(t, 0f, 1f), 3f);

    private void DrawSelectionGlowBorder(SKCanvas canvas, SKRect rect, float strength, float pulse)
    {
        if (strength <= 0.001f)
            return;

        float glowA = strength * (0.65f + 0.35f * pulse);
        var glowRect = rect;
        glowRect.Inflate(2.4f, 2.4f);

        _overlayPaint.Style = SKPaintStyle.Stroke;
        _overlayPaint.Shader = null;
        _overlayPaint.MaskFilter = _selectionGlowBlur;
        _overlayPaint.StrokeWidth = 6f;
        _overlayPaint.Color = SKColor.Parse("#7EC8F2").WithAlpha((byte)(110 * glowA));
        canvas.DrawRoundRect(glowRect, TileCornerRadius + 2.4f, TileCornerRadius + 2.4f, _overlayPaint);

        _overlayPaint.MaskFilter = null;
        _overlayPaint.StrokeWidth = 2.1f;
        _overlayPaint.Color = SKColors.White.WithAlpha((byte)(230 * strength));
        canvas.DrawRoundRect(rect, TileCornerRadius, TileCornerRadius, _overlayPaint);
    }

    private void DrawPlaceholder(SKCanvas canvas, float x, float y, float w, float h)
    {
        var rect = new SKRect(x, y, x + w, y + h);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColor.Parse("#1A1A1A").WithAlpha(180),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            PathEffect = SKPathEffect.CreateDash(new[] { 8f, 8f }, 0)
        };
        canvas.DrawRoundRect(rect, TileCornerRadius, TileCornerRadius, paint);
    }

    private void DrawSpinner(SKCanvas canvas, float cx, float cy)
    {
        _spinnerPaint.Color = SKColor.Parse("#00BFFF");
        var rect = new SKRect(cx - 18f, cy - 18f, cx + 18f, cy + 18f);
        canvas.DrawArc(rect, _spinnerRotation, 270f, false, _spinnerPaint);
    }

    private void DrawScrollbar(SKCanvas canvas, RowMetrics metrics)
    {
        if (_scrollbarOpacity <= 0.01f || metrics.MaxScrollX <= 1)
            return;

        float trackY = _visualSize.Y - AlbumRowLayoutHelper.ScrollbarBottomInset - AlbumRowLayoutHelper.ScrollbarHeight;
        float trackLeft = AlbumRowLayoutHelper.RowPaddingX;
        float trackRight = _visualSize.X - AlbumRowLayoutHelper.RowPaddingX;
        float trackWidth = trackRight - trackLeft;

        float thumbRatio = _visualSize.X / Math.Max(metrics.ContentWidth, _visualSize.X);
        float thumbWidth = Math.Max(32f, trackWidth * thumbRatio);
        float scrollRatio = metrics.MaxScrollX <= 0 ? 0 : (float)(_currentScrollX / metrics.MaxScrollX);
        float thumbX = trackLeft + (trackWidth - thumbWidth) * scrollRatio;

        byte alpha = (byte)Math.Clamp(_scrollbarOpacity * 220, 0, 255);
        _scrollbarPaint.Color = SKColors.White.WithAlpha(alpha);
        _scrollbarPaint.MaskFilter = _scrollbarBlur;
        canvas.DrawRoundRect(new SKRect(thumbX, trackY, thumbX + thumbWidth, trackY + AlbumRowLayoutHelper.ScrollbarHeight), 4f, 4f, _scrollbarPaint);
        _scrollbarPaint.MaskFilter = null;
    }

    private bool AnimateSwapOffsets(float dt)
    {
        if (_draggingIndex < 0 || _isDragCommitting)
            return false;

        bool animating = false;
        float step = 1f - MathF.Exp(-dt / Math.Max(0.001f, SwapAnimationSeconds * 0.45f));

        foreach (var (index, target) in _swapOffsetTargets)
        {
            _swapOffsets.TryGetValue(index, out var current);
            var next = current + (target - current) * step;
            _swapOffsets[index] = next;
            if (Vector2.DistanceSquared(next, target) > 0.25f)
                animating = true;
        }

        foreach (var index in _swapOffsets.Keys.ToArray())
        {
            if (_swapOffsetTargets.ContainsKey(index))
                continue;

            var current = _swapOffsets[index];
            var next = current + (Vector2.Zero - current) * step;
            _swapOffsets[index] = next;
            if (Vector2.DistanceSquared(next, Vector2.Zero) > 0.25f)
                animating = true;
            else
                _swapOffsets.Remove(index);
        }

        return animating;
    }

    private void ClearDragVisualState()
    {
        _draggingIndex = -1;
        _dropTargetIndex = -1;
        _isDragCommitting = false;
        _dragCommitProgress = 0f;
        _dragCommitStartPosition = default;
        _swapOffsets.Clear();
        _swapOffsetTargets.Clear();
    }

    private void UpdateSwapOffsetTargets()
    {
        if (_draggingIndex < 0 || _titles.Length == 0)
            return;

        _swapOffsetTargets.Clear();
        for (int i = 0; i < _titles.Length; i++)
        {
            if (i == _draggingIndex)
                continue;

            var offset = AlbumRowLayoutHelper.GetSwapOffset(
                i,
                _draggingIndex,
                _dropTargetIndex,
                _titles.Length,
                _currentScrollX,
                _visualSize.X,
                _visualSize.Y,
                _tileScale,
                _tileSpacing);
            _swapOffsetTargets[i] = new Vector2((float)offset.X, (float)offset.Y);
            if (!_swapOffsets.ContainsKey(i))
                _swapOffsets[i] = Vector2.Zero;
        }
    }

    private bool TryGetTilePosition(int index, RowMetrics metrics, out float x, out float y)
    {
        if (index < 0 || index >= _titles.Length)
        {
            x = 0;
            y = 0;
            return false;
        }

        float stride = metrics.TileWidth + metrics.Spacing;
        x = metrics.PaddingLeft + index * stride - (float)_currentScrollX;
        y = AlbumRowLayoutHelper.GetTileTop(_visualSize.Y, metrics.TileHeight);
        return true;
    }

    private bool TryGetTileDrawPosition(int index, RowMetrics metrics, out float x, out float y)
    {
        if (!TryGetTilePosition(index, metrics, out x, out y))
            return false;

        if (_draggingIndex >= 0 &&
            index != _draggingIndex &&
            _swapOffsets.TryGetValue(index, out var offset))
        {
            x += offset.X;
            y += offset.Y;
        }

        return x + metrics.TileWidth >= 0 && x <= _visualSize.X;
    }

    private readonly struct RowMetrics
    {
        public float TileWidth { get; init; }
        public float TileHeight { get; init; }
        public float Spacing { get; init; }
        public float PaddingLeft { get; init; }
        public float PaddingTop { get; init; }
        public float ContentWidth { get; init; }
        public float MaxScrollX { get; init; }
    }

    private RowMetrics ComputeMetrics(int itemCount)
    {
        var m = AlbumRowLayoutHelper.Compute(_visualSize.X, _visualSize.Y, itemCount, _tileScale, _tileSpacing);
        return new RowMetrics
        {
            TileWidth = m.TileWidth,
            TileHeight = m.TileHeight,
            Spacing = m.Spacing,
            PaddingLeft = m.PaddingLeft,
            PaddingTop = m.PaddingTop,
            ContentWidth = m.ContentWidth,
            MaxScrollX = m.MaxScrollX
        };
    }

    private void EnsureTileCount(int count)
    {
        while (_tiles.Count < count)
            _tiles.Add(new AlbumTileVisual());
        while (_tiles.Count > count)
        {
            ReleaseTileImages(_tiles[^1]);
            _tiles.RemoveAt(_tiles.Count - 1);
        }
    }

    private void ReleaseAllTileImages()
    {
        foreach (var tile in _tiles)
            ReleaseTileImages(tile);
    }

    private static void ReleaseTileImages(AlbumTileVisual tile)
    {
        tile.DefaultCover?.Dispose();
        tile.DefaultCover = null;
        foreach (var snap in tile.Snapshots)
        {
            if (!snap.UseFolderCover)
                snap.Cover?.Dispose();
        }
        tile.Snapshots.Clear();
    }

    private void EnsureAnimationLoop()
    {
        if (_lastTicks == 0)
            _lastTicks = Stopwatch.GetTimestamp();
        RegisterForNextAnimationFrameUpdate();
        Invalidate();
    }

    private static SKPaint CreateTitlePaint(SKColor color)
    {
        var paint = new SKPaint { IsAntialias = true, Color = color };
        CompositionSkiaTextHelper.ConfigurePaint(paint);
        return paint;
    }
}
