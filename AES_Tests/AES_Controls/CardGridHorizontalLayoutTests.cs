using AES_Controls.Composition;

namespace AES_Controls.Tests;

public sealed class CardGridHorizontalLayoutTests
{
    [Fact]
    public void ComputeMetrics_TwoItems_UseSingleRow()
    {
        var metrics = CardGridHorizontalLayout.ComputeMetrics(
            itemCount: 2,
            viewportWidth: 1280,
            viewportHeight: 720,
            cardScale: 1f,
            cardSpacing: 12f,
            topPadding: 24f);

        Assert.Equal(1, metrics.Rows);
        Assert.Equal(2, metrics.Columns);
    }

    [Fact]
    public void ComputeMetrics_FourItems_UseSingleRow()
    {
        var metrics = CardGridHorizontalLayout.ComputeMetrics(
            itemCount: 4,
            viewportWidth: 1280,
            viewportHeight: 720,
            cardScale: 1f,
            cardSpacing: 12f,
            topPadding: 24f);

        Assert.Equal(1, metrics.Rows);
        Assert.Equal(4, metrics.Columns);
        Assert.InRange(metrics.CardWidth, 199f, 201f);
        Assert.InRange(metrics.CardHeight, 271f, 273f);
    }

    [Fact]
    public void TryGetPosition_FourItems_AreOnSameRow()
    {
        var metrics = CardGridHorizontalLayout.ComputeMetrics(
            itemCount: 4,
            viewportWidth: 1280,
            viewportHeight: 720,
            cardScale: 1f,
            cardSpacing: 12f,
            topPadding: 24f);

        Assert.True(CardGridHorizontalLayout.TryGetPosition(0, 4, 0, metrics, out _, out float y0));
        for (int i = 1; i < 4; i++)
        {
            Assert.True(CardGridHorizontalLayout.TryGetPosition(i, 4, 0, metrics, out float x, out float y));
            Assert.Equal(y0, y);
            if (i > 0)
            {
                Assert.True(CardGridHorizontalLayout.TryGetPosition(i - 1, 4, 0, metrics, out float prevX, out _));
                Assert.True(x > prevX);
            }
        }
    }

    [Fact]
    public void EnumerateVisibleIndices_MultiRow_IncludesEachVisibleRow()
    {
        const int itemCount = 856;
        var metrics = CardGridHorizontalLayout.ComputeMetrics(
            itemCount,
            viewportWidth: 1280,
            viewportHeight: 720,
            cardScale: 1f,
            cardSpacing: 12f,
            topPadding: 24f);

        Assert.True(metrics.Rows > 1, $"Expected multiple rows, got {metrics.Rows}");

        var visible = CardGridHorizontalLayout.EnumerateVisibleIndices(
            scrollX: 0,
            viewportWidth: 1280,
            viewportHeight: 720,
            itemCount,
            cardScale: 1f,
            cardSpacing: 12f,
            topPadding: 24f,
            columnBuffer: 0).ToArray();

        Assert.Contains(0, visible);
        Assert.Contains(metrics.Columns, visible);
        Assert.DoesNotContain(metrics.Columns / 2, visible);
    }

    [Fact]
    public void GetVisibleIndexRange_MultiRow_BoundsActualVisibleRows()
    {
        const int itemCount = 856;
        var metrics = CardGridHorizontalLayout.ComputeMetrics(
            itemCount,
            viewportWidth: 1280,
            viewportHeight: 720,
            cardScale: 1f,
            cardSpacing: 12f,
            topPadding: 24f);

        var visible = CardGridHorizontalLayout.EnumerateVisibleIndices(
            0,
            1280,
            720,
            itemCount,
            1f,
            12f,
            24f,
            columnBuffer: 0).ToArray();

        var (start, end) = CardGridHorizontalLayout.GetVisibleIndexRange(
            0,
            1280,
            720,
            itemCount,
            1f,
            12f,
            24f,
            columnBuffer: 0);

        Assert.Equal(visible.Min(), start);
        Assert.Equal(visible.Max(), end);
        Assert.True(metrics.Rows > 1);
    }
}
