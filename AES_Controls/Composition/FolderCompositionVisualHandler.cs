using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace AES_Controls.Composition;

internal sealed class FolderCompositionVisualHandler : CompositionCustomVisualHandler
{
    private sealed class ItemState
    {
        public int SnapshotIndex;
        public SKImage? Cover;
        public bool UseFolderCover;
        public double CurX;
        public double CurY;
        public double CurOpacity;
        public double CoverFade;
        public double TgtX;
        public double TgtY;
        public double TgtOpacity;
        public int ZIndex;
        public bool IsTarget;
        public SKImage? LastCover;

        public ItemState(double x, double y)
        {
            CurX = x;
            CurY = y;
            TgtX = x;
            TgtY = y;
            CurOpacity = 0;
            TgtOpacity = 1;
            CoverFade = 0;
        }

        public bool Update(double speed)
        {
            bool any = false;
            double dx = TgtX - CurX;
            double dy = TgtY - CurY;
            double dop = TgtOpacity - CurOpacity;

            if (Math.Abs(dx) > 0.1 || Math.Abs(dy) > 0.1)
            {
                CurX += dx * speed;
                CurY += dy * speed;
                any = true;
            }
            else
            {
                CurX = TgtX;
                CurY = TgtY;
            }

            if (Math.Abs(dop) > 0.005)
            {
                CurOpacity += dop * speed;
                any = true;
            }
            else
            {
                CurOpacity = TgtOpacity;
            }

            double targetFade = Cover != null ? 1.0 : 0.0;
            if (!ReferenceEquals(Cover, LastCover))
            {
                LastCover = Cover;
                if (CoverFade > 0.1)
                    CoverFade = 0;
            }

            double df = targetFade - CoverFade;
            if (Math.Abs(df) > 0.005)
            {
                CoverFade += df * (speed * 0.75);
                any = true;
            }
            else
            {
                CoverFade = targetFade;
            }

            return any;
        }
    }

    private Vector2 _visualSize;
    private readonly List<ItemState> _states = [];
    private readonly List<ItemState> _sortedDrawBuffer = [];
    private readonly SKPaint _coverPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
    private bool _spread;
    private bool _animationPaused;
    private bool _uniformToFill = true;
    private int _maxVisibleCovers = 5;
    private List<FolderItemSnapshot> _itemSnapshots = [];
    private SKImage? _renderDefaultCover;
    private double _curPress = 1.0;
    private double _tgtPress = 1.0;
    private long _lastTicks;

    public override void OnMessage(object message)
    {
        switch (message)
        {
            case null:
                _states.Clear();
                ReleaseOwnedImages();
                return;
            case Vector2 size:
                _visualSize = size;
                Invalidate();
                return;
            case FolderLayoutRebuildMessage rebuild:
                ApplyRebuild(rebuild);
                return;
            case FolderSpreadMessage spread:
                _spread = spread.Spread;
                RecomputeTargets(snap: false);
                return;
            case FolderPressTargetMessage press:
                _tgtPress = press.TargetPress;
                EnsureAnimationLoop();
                return;
            case FolderAnimationPausedMessage paused:
                _animationPaused = paused.IsPaused;
                if (_animationPaused)
                {
                    SnapToTargets();
                    _lastTicks = 0;
                }
                else
                {
                    RecomputeTargets(snap: false);
                }
                return;
            case FolderItemCoverMessage cover:
                if (cover.Index >= 0 && cover.Index < _itemSnapshots.Count)
                {
                    var prev = _itemSnapshots[cover.Index];
                    if (!prev.UseFolderCover)
                        prev.Cover?.Dispose();

                    _itemSnapshots[cover.Index] = new FolderItemSnapshot(cover.Cover, false);
                    foreach (var state in _states)
                    {
                        if (state.SnapshotIndex != cover.Index)
                            continue;
                        state.Cover = cover.Cover;
                        state.LastCover = null;
                        break;
                    }
                    EnsureAnimationLoop();
                }
                return;
        }
    }

    public override void OnAnimationFrameUpdate()
    {
        if (_visualSize.X <= 0 || _visualSize.Y <= 0)
            return;

        if (_animationPaused && !HasActiveMotion())
            return;

        long currentTicks = Stopwatch.GetTimestamp();
        if (_lastTicks == 0)
            _lastTicks = currentTicks;
        double dt = (double)(currentTicks - _lastTicks) / Stopwatch.Frequency;
        _lastTicks = currentTicks;
        if (dt > 0.1)
            dt = 0.1;

        bool any = false;
        // Frame-rate independent lerp matching ~0.12 per 60 Hz frame.
        double speed = 1.0 - Math.Pow(1.0 - 0.12, dt * 60.0);

        double dp = _tgtPress - _curPress;
        if (Math.Abs(dp) > 0.001)
        {
            _curPress += dp * speed;
            any = true;
        }
        else
        {
            _curPress = _tgtPress;
        }

        for (int i = _states.Count - 1; i >= 0; i--)
        {
            var state = _states[i];
            bool stateMoving = state.Update(speed);
            any |= stateMoving;

            if (!stateMoving && !state.IsTarget && state.CurOpacity <= 0.005)
                _states.RemoveAt(i);
        }

        if (any)
        {
            RegisterForNextAnimationFrameUpdate();
            Invalidate();
        }
        else
        {
            _lastTicks = 0;
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null)
            return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;
        if (_visualSize.X <= 0 || _visualSize.Y <= 0 || _states.Count == 0)
            return;

        float w = _visualSize.X;
        float h = _visualSize.Y;
        float cx = w / 2f;
        float cy = h / 2f;

        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.Scale((float)_curPress, (float)_curPress);
        canvas.Translate(-cx, -cy);

        float itemSize = Math.Max(w, h);
        _sortedDrawBuffer.Clear();
        _sortedDrawBuffer.AddRange(_states);
        _sortedDrawBuffer.Sort((a, b) => a.ZIndex.CompareTo(b.ZIndex));

        foreach (var state in _sortedDrawBuffer)
        {
            if (state.CurOpacity <= 0.001f)
                continue;

            float drawW = itemSize;
            float drawH = itemSize;
            float drawX = (float)state.CurX;
            float drawY = (float)state.CurY + (itemSize - drawH) / 2f;
            var dest = new SKRect(drawX, drawY, drawX + drawW, drawY + drawH);

            if (_renderDefaultCover != null && state.UseFolderCover)
            {
                DrawCover(canvas, _renderDefaultCover, dest, state.CurOpacity, 1f);
                continue;
            }

            if (state.Cover != null && state.CoverFade > 0.001f)
                DrawCover(canvas, state.Cover, dest, state.CurOpacity, (float)state.CoverFade);
        }

        canvas.Restore();
    }

    private void DrawCover(SKCanvas canvas, SKImage cover, SKRect dest, double opacity, float coverFade)
    {
        if (!IsValidDrawRect(dest) || opacity <= 0.001 || coverFade <= 0.001f)
            return;

        if (!TryReadCoverSize(cover, out int w, out int h))
            return;

        try
        {

            byte alpha = (byte)Math.Clamp((int)(255 * opacity * coverFade), 0, 255);
            if (alpha == 0)
                return;

            _coverPaint.Color = SKColors.White.WithAlpha(alpha);

            var srcFull = new SKRect(0, 0, w, h);
            if (_uniformToFill)
            {
                var src = UniformToFillSrc(w, h, dest);
                if (!IsValidDrawRect(src))
                    return;
                canvas.DrawImage(cover, src, dest, _coverPaint);
            }
            else
            {
                var fitDest = UniformFitDest(w, h, dest);
                if (!IsValidDrawRect(fitDest))
                    return;
                canvas.DrawImage(cover, srcFull, fitDest, _coverPaint);
            }
        }
        catch (ObjectDisposedException)
        {
            // Control replaced cover textures on the UI thread.
        }
    }

    private static bool IsValidDrawRect(SKRect r) =>
        r.Width > 0.5f && r.Height > 0.5f &&
        !float.IsNaN(r.Left) && !float.IsNaN(r.Top) &&
        !float.IsInfinity(r.Width) && !float.IsInfinity(r.Height);

    private static bool TryReadCoverSize(SKImage cover, out int width, out int height)
    {
        width = height = 0;
        try
        {
            width = cover.Width;
            height = cover.Height;
            return width > 0 && height > 0;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void ReleaseOwnedImages()
    {
        _renderDefaultCover?.Dispose();
        _renderDefaultCover = null;

        foreach (var snap in _itemSnapshots)
        {
            if (!snap.UseFolderCover)
                snap.Cover?.Dispose();
        }

        _itemSnapshots = [];
    }

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

    private static SKRect UniformFitDest(float srcW, float srcH, SKRect dest)
    {
        float scale = Math.Min(dest.Width / srcW, dest.Height / srcH);
        float drawW = srcW * scale;
        float drawH = srcH * scale;
        float drawX = dest.MidX - drawW / 2f;
        float drawY = dest.MidY - drawH / 2f;
        return new SKRect(drawX, drawY, drawX + drawW, drawY + drawH);
    }

    private void ApplyRebuild(FolderLayoutRebuildMessage rebuild)
    {
        ReleaseOwnedImages();
        _states.Clear();

        _spread = rebuild.Spread;
        _maxVisibleCovers = Math.Max(1, rebuild.MaxVisibleCovers);
        _uniformToFill = rebuild.UniformToFill;
        _renderDefaultCover = rebuild.DefaultCover;
        _itemSnapshots = rebuild.Items.ToList();

        RecomputeTargetsFromSnapshots(rebuild.SnapToTargets);
    }

    private void RecomputeTargets(bool snap) =>
        RecomputeTargetsFromSnapshots(snap);

    private void RecomputeTargetsFromSnapshots(bool snap)
    {
        var items = _itemSnapshots;
        if (_visualSize.X <= 0 || _visualSize.Y <= 0)
            return;

        float w = _visualSize.X;
        float h = _visualSize.Y;
        float itemSize = Math.Max(w, h);
        float baseXStacked = w - itemSize;
        float marginHover = itemSize * 0.18f;

        int count = items.Count;
        int maxVisible = Math.Max(1, _maxVisibleCovers);
        int visible = Math.Min(count, maxVisible);
        int startIndex = Math.Max(0, count - maxVisible);

        foreach (var s in _states)
            s.IsTarget = false;

        for (int i = 0; i < visible; i++)
        {
            int snapshotIndex = startIndex + i;
            var itemSnap = items[snapshotIndex];
            float tx = _spread ? baseXStacked - (i * marginHover) : baseXStacked;
            float ty = 0;

            var state = FindState(snapshotIndex);
            if (state == null)
            {
                state = new ItemState(baseXStacked, ty)
                {
                    SnapshotIndex = snapshotIndex,
                    IsTarget = true,
                    ZIndex = i,
                    Cover = itemSnap.UseFolderCover ? null : itemSnap.Cover,
                    UseFolderCover = itemSnap.UseFolderCover,
                    LastCover = itemSnap.Cover
                };
                state.TgtX = tx;
                state.TgtY = ty;
                state.TgtOpacity = 1;
                _states.Add(state);
            }
            else
            {
                state.IsTarget = true;
                state.ZIndex = i;
                state.TgtX = tx;
                state.TgtY = ty;
                state.TgtOpacity = 1;
                state.Cover = itemSnap.UseFolderCover ? null : itemSnap.Cover;
                state.UseFolderCover = itemSnap.UseFolderCover;
                if (!ReferenceEquals(state.Cover, itemSnap.Cover))
                    state.LastCover = null;
            }
        }

        for (int i = _states.Count - 1; i >= 0; i--)
        {
            var state = _states[i];
            if (!state.IsTarget)
                state.TgtOpacity = 0;
        }

        if (snap)
            SnapToTargets();
        else
            EnsureAnimationLoop();
    }

    private ItemState? FindState(int snapshotIndex)
    {
        foreach (var state in _states)
        {
            if (state.SnapshotIndex == snapshotIndex)
                return state;
        }

        return null;
    }

    private void SnapToTargets()
    {
        foreach (var state in _states)
        {
            state.CurX = state.TgtX;
            state.CurY = state.TgtY;
            state.CurOpacity = state.TgtOpacity;
            state.CoverFade = state.Cover != null ? 1.0 : 0.0;
        }

        _curPress = _tgtPress;
        Invalidate();
    }

    private void EnsureAnimationLoop()
    {
        if (_animationPaused && !HasActiveMotion())
            return;

        if (_lastTicks == 0)
            _lastTicks = Stopwatch.GetTimestamp();
        RegisterForNextAnimationFrameUpdate();
        Invalidate();
    }

    private bool HasActiveMotion()
    {
        if (_spread || Math.Abs(_tgtPress - _curPress) > 0.001)
            return true;

        foreach (var state in _states)
        {
            if (Math.Abs(state.TgtX - state.CurX) > 0.1 ||
                Math.Abs(state.TgtY - state.CurY) > 0.1 ||
                Math.Abs(state.TgtOpacity - state.CurOpacity) > 0.005 ||
                Math.Abs((state.Cover != null ? 1.0 : 0.0) - state.CoverFade) > 0.005)
            {
                return true;
            }
        }

        return false;
    }

}
