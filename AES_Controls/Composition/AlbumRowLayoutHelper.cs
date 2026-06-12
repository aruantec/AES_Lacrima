using Avalonia;

namespace AES_Controls.Composition;

internal readonly struct AlbumRowLayoutMetrics
{
    public float TileWidth { get; init; }
    public float TileHeight { get; init; }
    public float Spacing { get; init; }
    public float PaddingLeft { get; init; }
    public float PaddingTop { get; init; }
    public float ContentWidth { get; init; }
    public float MaxScrollX { get; init; }
}

internal static class AlbumRowLayoutHelper
{
    internal const float BaseTileWidth = 240f;
    internal const float BaseTileHeight = 220f;
    internal const float TitleBarHeight = 50f;
    internal const float TitleBarPaddingX = 12f;
    internal const float SelectionLiftScale = 0.072f;
    internal const float RowPaddingX = 20f;
    internal const float RowPaddingY = 12f;
    internal const float ScrollbarReserve = 28f;
    internal const float ScrollbarBottomInset = 6f;
    internal const float ScrollbarHeight = 8f;
    internal const float ScrollbarHitHeight = 22f;

    public static AlbumRowLayoutMetrics Compute(
        float viewportWidth,
        float viewportHeight,
        int itemCount,
        float tileScale,
        float tileSpacing)
    {
        float tileW = BaseTileWidth * tileScale;
        float tileH = BaseTileHeight * tileScale;

        if (itemCount <= 0 || viewportWidth <= 0)
        {
            return new AlbumRowLayoutMetrics
            {
                TileWidth = tileW,
                TileHeight = tileH,
                Spacing = tileSpacing,
                PaddingLeft = RowPaddingX,
                PaddingTop = RowPaddingY,
                ContentWidth = 0,
                MaxScrollX = 0
            };
        }

        float contentWidth = RowPaddingX + itemCount * tileW + Math.Max(0, itemCount - 1) * tileSpacing + RowPaddingX;
        float maxScroll = Math.Max(0, contentWidth - viewportWidth);

        return new AlbumRowLayoutMetrics
        {
            TileWidth = tileW,
            TileHeight = tileH,
            Spacing = tileSpacing,
            PaddingLeft = RowPaddingX,
            PaddingTop = RowPaddingY,
            ContentWidth = contentWidth,
            MaxScrollX = maxScroll
        };
    }

    public static float GetTileTop(float viewportHeight, float tileHeight) =>
        RowPaddingY + Math.Max(0f, (viewportHeight - RowPaddingY * 2f - ScrollbarReserve - tileHeight) * 0.5f);

    public static int HitTestTile(
        Point point,
        double scrollX,
        int itemCount,
        float viewportWidth,
        float viewportHeight,
        float tileScale,
        float tileSpacing)
    {
        if (itemCount <= 0)
            return -1;

        var metrics = Compute(viewportWidth, viewportHeight, itemCount, tileScale, tileSpacing);
        float tileTop = GetTileTop(viewportHeight, metrics.TileHeight);
        float localY = (float)point.Y;
        if (localY < tileTop || localY > tileTop + metrics.TileHeight)
            return -1;

        float localX = (float)point.X + (float)scrollX;
        if (localX < metrics.PaddingLeft)
            return -1;

        float stride = metrics.TileWidth + metrics.Spacing;
        int index = (int)((localX - metrics.PaddingLeft) / stride);
        if (index < 0 || index >= itemCount)
            return -1;

        float tileLeft = metrics.PaddingLeft + index * stride;
        if (localX > tileLeft + metrics.TileWidth)
            return -1;

        return index;
    }

    public static Rect GetTileBounds(
        int index,
        double scrollX,
        float viewportWidth,
        float viewportHeight,
        float tileScale,
        float tileSpacing)
    {
        var metrics = Compute(viewportWidth, viewportHeight, Math.Max(index + 1, 1), tileScale, tileSpacing);
        float stride = metrics.TileWidth + metrics.Spacing;
        float x = metrics.PaddingLeft + index * stride - (float)scrollX;
        float y = GetTileTop(viewportHeight, metrics.TileHeight);
        return new Rect(x, y, metrics.TileWidth, metrics.TileHeight);
    }

    public static (int StartIndex, int EndIndex) GetVisibleIndexRange(
        double scrollX,
        float viewportWidth,
        float viewportHeight,
        int itemCount,
        float tileScale,
        float tileSpacing,
        int buffer = 1)
    {
        if (itemCount <= 0)
            return (-1, -1);

        var metrics = Compute(viewportWidth, viewportHeight, itemCount, tileScale, tileSpacing);
        float stride = metrics.TileWidth + metrics.Spacing;
        int first = (int)Math.Floor((scrollX - metrics.PaddingLeft) / stride) - buffer;
        int last = (int)Math.Ceiling((scrollX + viewportWidth - metrics.PaddingLeft) / stride) + buffer;
        return (Math.Clamp(first, 0, itemCount - 1), Math.Clamp(last, 0, itemCount - 1));
    }

    public static double ScrollOffsetToRevealIndex(
        int index,
        double currentScrollX,
        float viewportWidth,
        float viewportHeight,
        int itemCount,
        float tileScale,
        float tileSpacing,
        float edgeMargin = 14f)
    {
        if (itemCount <= 0 || index < 0)
            return 0;

        var metrics = Compute(viewportWidth, viewportHeight, itemCount, tileScale, tileSpacing);
        float stride = metrics.TileWidth + metrics.Spacing;
        float tileLeft = metrics.PaddingLeft + index * stride;
        float tileRight = tileLeft + metrics.TileWidth;

        float viewLeft = (float)currentScrollX;
        float viewRight = viewLeft + viewportWidth;

        if (tileLeft >= viewLeft + edgeMargin && tileRight <= viewRight - edgeMargin)
            return currentScrollX;

        if (tileLeft < viewLeft + edgeMargin)
            return Math.Clamp(tileLeft - edgeMargin, 0, metrics.MaxScrollX);

        return Math.Clamp(tileRight - viewportWidth + edgeMargin, 0, metrics.MaxScrollX);
    }

    public static Point GetSlotCenterViewport(
        int index,
        double scrollX,
        float viewportWidth,
        float viewportHeight,
        int itemCount,
        float tileScale,
        float tileSpacing)
    {
        var bounds = GetTileBounds(index, scrollX, viewportWidth, viewportHeight, tileScale, tileSpacing);
        return new Point(bounds.X + bounds.Width * 0.5, bounds.Y + bounds.Height * 0.5);
    }

    public static int FindNearestDropTargetIndex(
        Point dragCenterViewport,
        double scrollX,
        int itemCount,
        float viewportWidth,
        float viewportHeight,
        float tileScale,
        float tileSpacing)
    {
        if (itemCount <= 0)
            return -1;

        int best = 0;
        double minDistanceSq = double.MaxValue;
        for (int i = 0; i < itemCount; i++)
        {
            var center = GetSlotCenterViewport(i, scrollX, viewportWidth, viewportHeight, itemCount, tileScale, tileSpacing);
            double dx = dragCenterViewport.X - center.X;
            double dy = dragCenterViewport.Y - center.Y;
            double distSq = dx * dx + dy * dy;
            if (distSq < minDistanceSq)
            {
                minDistanceSq = distSq;
                best = i;
            }
        }

        return best;
    }

    public static int ComputeDisplaySlot(int index, int dragIndex, int dropTarget, int itemCount)
    {
        if (index == dragIndex)
            return dropTarget;

        const int draggedCount = 1;
        int targetStart = Math.Clamp(dropTarget, 0, Math.Max(0, itemCount - draggedCount));
        int movedBefore = index < dragIndex ? 0 : 1;
        int rankAmongNonDragged = index - movedBefore;
        return rankAmongNonDragged < targetStart ? rankAmongNonDragged : rankAmongNonDragged + draggedCount;
    }

    public static Point GetSwapOffset(
        int index,
        int dragIndex,
        int dropTarget,
        int itemCount,
        double scrollX,
        float viewportWidth,
        float viewportHeight,
        float tileScale,
        float tileSpacing)
    {
        if (index == dragIndex || itemCount <= 0)
            return default;

        int displaySlot = ComputeDisplaySlot(index, dragIndex, dropTarget, itemCount);
        if (displaySlot == index)
            return default;

        var from = GetTileBounds(index, scrollX, viewportWidth, viewportHeight, tileScale, tileSpacing);
        var to = GetTileBounds(displaySlot, scrollX, viewportWidth, viewportHeight, tileScale, tileSpacing);
        return new Point(to.X - from.X, to.Y - from.Y);
    }
}
