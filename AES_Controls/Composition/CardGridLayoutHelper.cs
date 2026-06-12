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

    public static int HitTestCard(Point point, double scrollY, int itemCount, float viewportWidth, float viewportHeight, float cardScale, float cardSpacing, float topPadding)
    {
        if (itemCount <= 0)
            return -1;

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

    public static Rect GetCardBounds(int index, double scrollY, float viewportWidth, float viewportHeight, float cardScale, float cardSpacing, float topPadding)
    {
        var metrics = Compute(viewportWidth, viewportHeight, Math.Max(index + 1, 1), cardScale, cardSpacing, topPadding);
        int row = index / Math.Max(1, metrics.Columns);
        int col = index % Math.Max(1, metrics.Columns);
        float x = metrics.PaddingLeft + col * (metrics.CardWidth + metrics.Spacing);
        float y = metrics.PaddingTop + row * (metrics.CardHeight + metrics.Spacing) - (float)scrollY;
        return new Rect(x, y, metrics.CardWidth, metrics.CardHeight);
    }

    public static (int StartIndex, int EndIndex) GetVisibleIndexRange(
        double scrollY,
        float viewportHeight,
        int itemCount,
        float viewportWidth,
        float cardScale,
        float cardSpacing,
        float topPadding,
        int rowBuffer = 2)
    {
        if (itemCount <= 0)
            return (0, -1);

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
}
