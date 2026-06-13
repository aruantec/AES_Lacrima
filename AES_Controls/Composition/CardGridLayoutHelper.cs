using Avalonia;

namespace AES_Controls.Composition;

internal readonly struct CardGridLayoutMetrics
{
    public int Columns { get; init; }
    public float CardWidth { get; init; }
    public float CardHeight { get; init; }
    public float Spacing { get; init; }
    public float PaddingLeft { get; init; }
    public float PaddingTop { get; init; }
    public float ContentHeight { get; init; }
    public float MaxScrollY { get; init; }
    public int RowCount { get; init; }
}

internal static class CardGridLayoutHelper
{
    private const float BaseCardWidth = 200f;
    private const float BaseCardHeight = 272f;
    private const float GridPaddingX = 28f;
    internal const float ScrollbarReserve = 40f;
    internal const float ScrollbarRightInset = 16f;
    internal const float ScrollbarWidth = 8f;
    internal const float ScrollbarHitWidth = 26f;

    public static CardGridLayoutMetrics Compute(float viewportWidth, float viewportHeight, int itemCount, float cardScale, float cardSpacing, float topPadding)
    {
        if (itemCount <= 0 || viewportWidth <= 0)
        {
            return new CardGridLayoutMetrics
            {
                Columns = 1,
                CardWidth = BaseCardWidth * cardScale,
                CardHeight = BaseCardHeight * cardScale,
                Spacing = cardSpacing,
                PaddingLeft = GridPaddingX,
                PaddingTop = topPadding,
                ContentHeight = 0,
                MaxScrollY = 0,
                RowCount = 0
            };
        }

        float availW = Math.Max(80f, viewportWidth - GridPaddingX * 2 - ScrollbarReserve);
        float minCardW = BaseCardWidth * cardScale * 0.75f;
        int columns = Math.Max(1, (int)((availW + cardSpacing) / (minCardW + cardSpacing)));
        float cardW = (availW - cardSpacing * (columns - 1)) / columns;
        float cardH = cardW * (BaseCardHeight / BaseCardWidth);
        int rowCount = (itemCount + columns - 1) / columns;
        float contentHeight = topPadding + rowCount * cardH + Math.Max(0, rowCount - 1) * cardSpacing + 28f;
        float maxScroll = Math.Max(0, contentHeight - viewportHeight);

        return new CardGridLayoutMetrics
        {
            Columns = columns,
            CardWidth = cardW,
            CardHeight = cardH,
            Spacing = cardSpacing,
            PaddingLeft = GridPaddingX,
            PaddingTop = topPadding,
            ContentHeight = contentHeight,
            MaxScrollY = maxScroll,
            RowCount = rowCount
        };
    }

    public static int HitTestCard(Point point, double scrollY, int itemCount, float viewportWidth, float viewportHeight, float cardScale, float cardSpacing, float topPadding, bool horizontalScrollEnabled = false)
    {
        if (itemCount <= 0)
            return -1;

        if (horizontalScrollEnabled)
        {
            return CardGridHorizontalLayout.HitTestIndex(
                point,
                scrollY,
                itemCount,
                viewportWidth,
                viewportHeight,
                cardScale,
                cardSpacing,
                topPadding);
        }

        var metrics = Compute(viewportWidth, viewportHeight, itemCount, cardScale, cardSpacing, topPadding);
        float localY = (float)point.Y + (float)scrollY;
        if (localY < metrics.PaddingTop - metrics.Spacing)
            return -1;

        int row = (int)((localY - metrics.PaddingTop) / (metrics.CardHeight + metrics.Spacing));
        if (row < 0 || row >= metrics.RowCount)
            return -1;

        float rowTop = metrics.PaddingTop + row * (metrics.CardHeight + metrics.Spacing);
        if (localY > rowTop + metrics.CardHeight)
            return -1;

        float localX = (float)point.X;
        if (localX < metrics.PaddingLeft)
            return -1;

        int col = (int)((localX - metrics.PaddingLeft) / (metrics.CardWidth + metrics.Spacing));
        if (col < 0 || col >= metrics.Columns)
            return -1;

        float colLeft = metrics.PaddingLeft + col * (metrics.CardWidth + metrics.Spacing);
        if (localX > colLeft + metrics.CardWidth)
            return -1;

        int index = row * metrics.Columns + col;
        return index < itemCount ? index : -1;
    }

    public static Rect GetCardBounds(int index, double scrollY, float viewportWidth, float viewportHeight, float cardScale, float cardSpacing, float topPadding, bool horizontalScrollEnabled = false, int itemCount = 0)
    {
        int count = itemCount > 0 ? itemCount : Math.Max(index + 1, 1);
        if (horizontalScrollEnabled)
        {
            return CardGridHorizontalLayout.GetCardBounds(
                index,
                scrollY,
                viewportWidth,
                viewportHeight,
                count,
                cardScale,
                cardSpacing,
                topPadding);
        }

        var metrics = Compute(viewportWidth, viewportHeight, count, cardScale, cardSpacing, topPadding);
        if (!TryGetFlatCardPosition(index, scrollY, metrics, out float x, out float y))
            return default;

        return new Rect(x, y, metrics.CardWidth, metrics.CardHeight);
    }

    private static bool TryGetFlatCardPosition(int index, double scrollY, CardGridLayoutMetrics metrics, out float x, out float y)
    {
        if (index < 0 || metrics.Columns <= 0)
        {
            x = 0;
            y = 0;
            return false;
        }

        int row = index / metrics.Columns;
        int col = index % metrics.Columns;
        x = metrics.PaddingLeft + col * (metrics.CardWidth + metrics.Spacing);
        y = metrics.PaddingTop + row * (metrics.CardHeight + metrics.Spacing) - (float)scrollY;
        return true;
    }

    public static (int StartIndex, int EndIndex) GetVisibleIndexRange(
        double scrollY,
        float viewportHeight,
        int itemCount,
        float viewportWidth,
        float cardScale,
        float cardSpacing,
        float topPadding,
        bool horizontalScrollEnabled = false,
        int rowBuffer = 2)
    {
        if (itemCount <= 0)
            return (0, -1);

        if (horizontalScrollEnabled)
        {
            return CardGridHorizontalLayout.GetVisibleIndexRange(
                scrollY,
                viewportWidth,
                viewportHeight,
                itemCount,
                cardScale,
                cardSpacing,
                topPadding,
                columnBuffer: rowBuffer);
        }

        var metrics = Compute(viewportWidth, viewportHeight, itemCount, cardScale, cardSpacing, topPadding);
        int columns = Math.Max(1, metrics.Columns);
        int firstRow = Math.Max(0, (int)Math.Floor((scrollY - metrics.PaddingTop) / (metrics.CardHeight + metrics.Spacing)) - rowBuffer);
        int lastRow = Math.Min(
            metrics.RowCount - 1,
            (int)Math.Ceiling((scrollY + viewportHeight) / (metrics.CardHeight + metrics.Spacing)) + rowBuffer);

        int start = firstRow * columns;
        int end = Math.Min(itemCount - 1, (lastRow + 1) * columns - 1);
        return (start, end);
    }

    public static double ScrollOffsetForIndex(int index, float viewportWidth, float viewportHeight, int itemCount, float cardScale, float cardSpacing, float topPadding)
    {
        var metrics = Compute(viewportWidth, viewportHeight, itemCount, cardScale, cardSpacing, topPadding);
        if (index < 0 || itemCount <= 0)
            return 0;

        int row = index / Math.Max(1, metrics.Columns);
        float cardTop = metrics.PaddingTop + row * (metrics.CardHeight + metrics.Spacing);
        float centered = cardTop - (viewportHeight - metrics.CardHeight) * 0.5f;
        return Math.Clamp(centered, 0, metrics.MaxScrollY);
    }

    /// <summary>
    /// Returns a scroll offset that keeps the card visible, using the current offset when already in view.
    /// </summary>
    public static double ScrollOffsetToRevealIndex(
        int index,
        double currentScrollY,
        float viewportWidth,
        float viewportHeight,
        int itemCount,
        float cardScale,
        float cardSpacing,
        float topPadding,
        bool horizontalScrollEnabled = false,
        float edgeMargin = 14f)
    {
        if (index < 0 || itemCount <= 0)
            return 0;

        if (horizontalScrollEnabled)
        {
            return CardGridHorizontalLayout.ScrollOffsetToRevealIndex(
                index,
                currentScrollY,
                viewportWidth,
                viewportHeight,
                itemCount,
                cardScale,
                cardSpacing,
                topPadding,
                edgeMargin);
        }

        var metrics = Compute(viewportWidth, viewportHeight, itemCount, cardScale, cardSpacing, topPadding);
        int columns = Math.Max(1, metrics.Columns);
        int row = index / columns;
        float cardTop = metrics.PaddingTop + row * (metrics.CardHeight + metrics.Spacing);
        float cardBottom = cardTop + metrics.CardHeight;

        float viewTop = (float)currentScrollY;
        float viewBottom = viewTop + viewportHeight;

        if (cardTop >= viewTop + edgeMargin && cardBottom <= viewBottom - edgeMargin)
            return currentScrollY;

        if (cardTop < viewTop + edgeMargin)
            return Math.Clamp(cardTop - edgeMargin, 0, metrics.MaxScrollY);

        return Math.Clamp(cardBottom - viewportHeight + edgeMargin, 0, metrics.MaxScrollY);
    }

    public static Point GetSlotCenterViewport(
        int index,
        double scrollY,
        float viewportWidth,
        float viewportHeight,
        int itemCount,
        float cardScale,
        float cardSpacing,
        float topPadding,
        bool horizontalScrollEnabled = false)
    {
        if (horizontalScrollEnabled)
        {
            return CardGridHorizontalLayout.GetCardCenter(
                index,
                scrollY,
                viewportWidth,
                viewportHeight,
                itemCount,
                cardScale,
                cardSpacing,
                topPadding);
        }

        var metrics = Compute(viewportWidth, viewportHeight, Math.Max(itemCount, index + 1), cardScale, cardSpacing, topPadding);
        if (!TryGetFlatCardPosition(index, scrollY, metrics, out float x, out float y))
            return default;

        return new Point(x + metrics.CardWidth * 0.5f, y + metrics.CardHeight * 0.5f);
    }

    /// <summary>
    /// Nearest-slot targeting used by the albums list drag behavior.
    /// </summary>
    public static int FindNearestDropTargetIndex(
        Point dragCenterViewport,
        double scrollY,
        int itemCount,
        float viewportWidth,
        float viewportHeight,
        float cardScale,
        float cardSpacing,
        float topPadding,
        bool horizontalScrollEnabled = false)
    {
        if (itemCount <= 0)
            return -1;

        int best = 0;
        double minDistanceSq = double.MaxValue;
        for (int i = 0; i < itemCount; i++)
        {
            var center = GetSlotCenterViewport(i, scrollY, viewportWidth, viewportHeight, itemCount, cardScale, cardSpacing, topPadding, horizontalScrollEnabled);
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

    /// <summary>
    /// Visual slot for an item while another item is being dragged to <paramref name="dropTarget"/>.
    /// </summary>
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
        double scrollY,
        float viewportWidth,
        float viewportHeight,
        float cardScale,
        float cardSpacing,
        float topPadding,
        bool horizontalScrollEnabled = false)
    {
        if (index == dragIndex || itemCount <= 0)
            return default;

        int displaySlot = ComputeDisplaySlot(index, dragIndex, dropTarget, itemCount);
        if (displaySlot == index)
            return default;

        if (horizontalScrollEnabled)
        {
            var horizontalMetrics = CardGridHorizontalLayout.ComputeMetrics(
                itemCount,
                viewportWidth,
                viewportHeight,
                cardScale,
                cardSpacing,
                topPadding);

            if (horizontalMetrics.Rows <= 0)
                return default;

            static Point HorizontalSlotTopLeft(int slot, HorizontalGridMetrics m, double scroll)
            {
                int columns = Math.Max(1, m.Columns);
                int col = slot % columns;
                int row = slot / columns;
                return new Point(
                    m.PaddingLeft + col * m.ColumnPitch - scroll,
                    m.PaddingTop + row * (m.CardHeight + m.Spacing));
            }

            var horizontalFrom = HorizontalSlotTopLeft(index, horizontalMetrics, scrollY);
            var horizontalTo = HorizontalSlotTopLeft(displaySlot, horizontalMetrics, scrollY);
            return new Point(horizontalTo.X - horizontalFrom.X, horizontalTo.Y - horizontalFrom.Y);
        }

        var metrics = Compute(viewportWidth, viewportHeight, itemCount, cardScale, cardSpacing, topPadding);
        int columns = Math.Max(1, metrics.Columns);

        static Point SlotTopLeft(int slot, CardGridLayoutMetrics m, int cols, double scroll)
        {
            int row = slot / cols;
            int col = slot % cols;
            return new Point(
                m.PaddingLeft + col * (m.CardWidth + m.Spacing),
                m.PaddingTop + row * (m.CardHeight + m.Spacing) - scroll);
        }

        var from = SlotTopLeft(index, metrics, columns, scrollY);
        var to = SlotTopLeft(displaySlot, metrics, columns, scrollY);
        return new Point(to.X - from.X, to.Y - from.Y);
    }

    public static double GetMaxScroll(
        float viewportWidth,
        float viewportHeight,
        int itemCount,
        float cardScale,
        float cardSpacing,
        float topPadding,
        bool horizontalScrollEnabled = false)
    {
        if (itemCount <= 0)
            return 0;

        if (horizontalScrollEnabled)
        {
            return CardGridHorizontalLayout.ComputeMetrics(
                itemCount,
                viewportWidth,
                viewportHeight,
                cardScale,
                cardSpacing,
                topPadding).MaxScrollX;
        }

        return Compute(viewportWidth, viewportHeight, itemCount, cardScale, cardSpacing, topPadding).MaxScrollY;
    }
}
