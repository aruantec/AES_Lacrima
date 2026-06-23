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
        float availW = Math.Max(80f, viewportWidth - PaddingLeft - PaddingRight);
        float minCardW = BaseCardWidth * cardScale * 0.75f;
        float targetCardH = BaseCardHeight * cardScale;
        float minCardH = targetCardH * MinCardHeightRatio;

        int maxRows = 1;
        for (int candidate = 1; candidate <= 24; candidate++)
        {
            float candidateH = (availH - (candidate - 1) * spacing) / candidate;
            if (candidateH >= minCardH)
                maxRows = candidate;
            else
                break;
        }

        // Fill left-to-right by row. Wrap only when another card cannot fit at min width.
        int maxColsInViewport = Math.Max(1, (int)((availW + spacing) / (minCardW + spacing)));
        int rows = itemCount <= 0
            ? 1
            : itemCount <= maxColsInViewport
                ? 1
                : Math.Clamp((itemCount + maxColsInViewport - 1) / maxColsInViewport, 1, Math.Min(maxRows, itemCount));

        int columns = itemCount <= 0 ? 1 : Math.Max(1, (itemCount + rows - 1) / rows);

        float maxCardH = Math.Min(availH, targetCardH);
        float maxCardW = maxCardH * (BaseCardWidth / BaseCardHeight);

        float cardW;
        float cardH;
        if (rows == 1 && itemCount > 0)
        {
            float distributedW = (availW - (columns - 1) * spacing) / columns;
            cardW = Math.Min(maxCardW, distributedW);
            cardH = cardW * (BaseCardHeight / BaseCardWidth);
            if (cardH > maxCardH)
            {
                cardH = maxCardH;
                cardW = maxCardW;
            }
        }
        else
        {
            cardH = Math.Min(maxCardH, (availH - (rows - 1) * spacing) / rows);
            cardW = cardH * (BaseCardWidth / BaseCardHeight);
        }
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

    public static IEnumerable<int> EnumerateVisibleIndices(
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
            yield break;

        var metrics = ComputeMetrics(itemCount, viewportWidth, viewportHeight, cardScale, cardSpacing, topPadding);
        if (metrics.Rows <= 0 || metrics.Columns <= 0)
            yield break;

        int firstCol = Math.Max(0, (int)Math.Floor((scrollX - metrics.PaddingLeft) / metrics.ColumnPitch) - columnBuffer);
        int lastCol = Math.Min(
            metrics.Columns - 1,
            (int)Math.Ceiling((scrollX + viewportWidth - metrics.PaddingLeft) / metrics.ColumnPitch) + columnBuffer);

        for (int row = 0; row < metrics.Rows; row++)
        {
            for (int col = firstCol; col <= lastCol; col++)
            {
                int index = row * metrics.Columns + col;
                if (index >= itemCount)
                    yield break;

                yield return index;
            }
        }
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
        int start = int.MaxValue;
        int end = int.MinValue;
        foreach (int index in EnumerateVisibleIndices(
                     scrollX,
                     viewportWidth,
                     viewportHeight,
                     itemCount,
                     cardScale,
                     cardSpacing,
                     topPadding,
                     columnBuffer))
        {
            if (index < start)
                start = index;
            if (index > end)
                end = index;
        }

        return start <= end ? (start, end) : (0, -1);
    }

    public static int EstimateViewportCenterIndex(
        double scrollX,
        float viewportWidth,
        float viewportHeight,
        int itemCount,
        float cardScale,
        float cardSpacing,
        float topPadding)
    {
        if (itemCount <= 0)
            return 0;

        var metrics = ComputeMetrics(itemCount, viewportWidth, viewportHeight, cardScale, cardSpacing, topPadding);
        if (metrics.Rows <= 0 || metrics.Columns <= 0)
            return 0;

        float centerX = (float)scrollX + viewportWidth * 0.5f;
        int col = (int)MathF.Round((centerX - metrics.PaddingLeft) / Math.Max(1f, metrics.ColumnPitch));
        col = Math.Clamp(col, 0, Math.Max(0, metrics.Columns - 1));
        int row = Math.Clamp(metrics.Rows / 2, 0, Math.Max(0, metrics.Rows - 1));
        return Math.Clamp(row * metrics.Columns + col, 0, itemCount - 1);
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
