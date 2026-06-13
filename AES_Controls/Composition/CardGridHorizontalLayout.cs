using Avalonia;

namespace AES_Controls.Composition;

internal readonly struct HorizontalGridMetrics
{
    public float CardWidth { get; init; }
    public float CardHeight { get; init; }
    public float Spacing { get; init; }
    public float PaddingLeft { get; init; }
    public float PaddingTop { get; init; }
    public int Columns { get; init; }
    public int Rows { get; init; }
    public float ColumnPitch { get; init; }
    public float ContentWidth { get; init; }
    public float MaxScrollX { get; init; }
}

internal static class CardGridHorizontalLayout
{
    private const float BaseCardWidth = 200f;
    private const float BaseCardHeight = 272f;
    private const float PaddingLeft = 36f;
    private const float PaddingRight = 36f;
    private const float BottomPadding = 28f;
    private const float MinCardHeightRatio = 0.58f;

    public static HorizontalGridMetrics ComputeMetrics(
        int itemCount,
        float viewportWidth,
        float viewportHeight,
        float cardScale,
        float cardSpacing,
        float topPadding)
    {
        if (itemCount <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
        {
            return new HorizontalGridMetrics
            {
                CardWidth = BaseCardWidth * cardScale,
                CardHeight = BaseCardHeight * cardScale,
                Spacing = cardSpacing,
                PaddingLeft = PaddingLeft,
                PaddingTop = topPadding,
                Columns = 1,
                Rows = 0,
                ColumnPitch = BaseCardWidth * cardScale
            };
        }

        float spacing = Math.Max(4f, cardSpacing);
        float availH = Math.Max(120f, viewportHeight - topPadding - BottomPadding);
        float targetCardH = BaseCardHeight * cardScale;
        float minCardH = targetCardH * MinCardHeightRatio;

        int rows = 1;
        for (int candidate = 1; candidate <= 24; candidate++)
        {
            float candidateH = (availH - (candidate - 1) * spacing) / candidate;
            if (candidateH >= minCardH)
                rows = candidate;
            else
                break;
        }

        float cardH = (availH - (rows - 1) * spacing) / rows;
        float cardW = cardH * (BaseCardWidth / BaseCardHeight);
        int columns = Math.Max(1, (itemCount + rows - 1) / rows);
        float pitch = cardW + spacing;
        float contentWidth = PaddingLeft + PaddingRight + columns * cardW + Math.Max(0, columns - 1) * spacing;
        float maxScroll = Math.Max(0f, contentWidth - viewportWidth);

        return new HorizontalGridMetrics
        {
            CardWidth = cardW,
            CardHeight = cardH,
            Spacing = spacing,
            PaddingLeft = PaddingLeft,
            PaddingTop = topPadding,
            Columns = columns,
            Rows = rows,
            ColumnPitch = pitch,
            ContentWidth = contentWidth,
            MaxScrollX = maxScroll
        };
    }

    public static bool TryGetPosition(int index, int itemCount, double scrollX, HorizontalGridMetrics metrics, out float x, out float y)
    {
        if (index < 0 || index >= itemCount || metrics.Rows <= 0)
        {
            x = 0;
            y = 0;
            return false;
        }

        int columns = Math.Max(1, metrics.Columns);
        int col = index % columns;
        int row = index / columns;
        x = metrics.PaddingLeft + col * metrics.ColumnPitch - (float)scrollX;
        y = metrics.PaddingTop + row * (metrics.CardHeight + metrics.Spacing);
        return true;
    }

    public static int HitTestIndex(
        Point point,
        double scrollX,
        int itemCount,
        float viewportWidth,
        float viewportHeight,
        float cardScale,
        float cardSpacing,
        float topPadding)
    {
        if (itemCount <= 0)
            return -1;

        var metrics = ComputeMetrics(itemCount, viewportWidth, viewportHeight, cardScale, cardSpacing, topPadding);
        var (start, end) = GetVisibleIndexRange(scrollX, viewportWidth, viewportHeight, itemCount, cardScale, cardSpacing, topPadding, columnBuffer: 1);

        for (int i = start; i <= end; i++)
        {
            if (i < 0 || i >= itemCount)
                continue;

            if (!TryGetPosition(i, itemCount, scrollX, metrics, out float x, out float y))
                continue;

            if (point.X >= x && point.X <= x + metrics.CardWidth &&
                point.Y >= y && point.Y <= y + metrics.CardHeight)
                return i;
        }

        return -1;
    }

    public static Rect GetCardBounds(
        int index,
        double scrollX,
        float viewportWidth,
        float viewportHeight,
        int itemCount,
        float cardScale,
        float cardSpacing,
        float topPadding)
    {
        var metrics = ComputeMetrics(Math.Max(itemCount, index + 1), viewportWidth, viewportHeight, cardScale, cardSpacing, topPadding);
        if (!TryGetPosition(index, itemCount, scrollX, metrics, out float x, out float y))
            return default;

        return new Rect(x, y, metrics.CardWidth, metrics.CardHeight);
    }

    public static (int StartIndex, int EndIndex) GetVisibleIndexRange(
        double scrollX,
        float viewportWidth,
        float viewportHeight,
        int itemCount,
        float cardScale,
        float cardSpacing,
        float topPadding,
        int columnBuffer = 2)
    {
        if (itemCount <= 0)
            return (0, -1);

        var metrics = ComputeMetrics(itemCount, viewportWidth, viewportHeight, cardScale, cardSpacing, topPadding);
        int firstCol = Math.Max(0, (int)Math.Floor((scrollX - metrics.PaddingLeft) / metrics.ColumnPitch) - columnBuffer);
        int lastCol = Math.Min(
            metrics.Columns - 1,
            (int)Math.Ceiling((scrollX + viewportWidth - metrics.PaddingLeft) / metrics.ColumnPitch) + columnBuffer);

        int start = firstCol;
        int end = Math.Min(itemCount - 1, (metrics.Rows - 1) * metrics.Columns + lastCol);
        return (start, end);
    }

    public static double ScrollOffsetToRevealIndex(
        int index,
        double currentScrollX,
        float viewportWidth,
        float viewportHeight,
        int itemCount,
        float cardScale,
        float cardSpacing,
        float topPadding,
        float edgeMargin = 14f)
    {
        if (index < 0 || itemCount <= 0)
            return 0;

        var metrics = ComputeMetrics(itemCount, viewportWidth, viewportHeight, cardScale, cardSpacing, topPadding);
        int col = index % Math.Max(1, metrics.Columns);
        float cardLeft = metrics.PaddingLeft + col * metrics.ColumnPitch;
        float cardRight = cardLeft + metrics.CardWidth;

        float viewLeft = (float)currentScrollX;
        float viewRight = viewLeft + viewportWidth;

        if (cardLeft >= viewLeft + edgeMargin && cardRight <= viewRight - edgeMargin)
            return currentScrollX;

        if (cardLeft < viewLeft + edgeMargin)
            return Math.Clamp(cardLeft - edgeMargin, 0, metrics.MaxScrollX);

        return Math.Clamp(cardRight - viewportWidth + edgeMargin, 0, metrics.MaxScrollX);
    }

    public static Point GetCardCenter(
        int index,
        double scrollX,
        float viewportWidth,
        float viewportHeight,
        int itemCount,
        float cardScale,
        float cardSpacing,
        float topPadding)
    {
        var metrics = ComputeMetrics(Math.Max(itemCount, index + 1), viewportWidth, viewportHeight, cardScale, cardSpacing, topPadding);
        if (!TryGetPosition(index, itemCount, scrollX, metrics, out float x, out float y))
            return default;

        return new Point(x + metrics.CardWidth * 0.5f, y + metrics.CardHeight * 0.5f);
    }
}
