using LogsPlatform.Web.Services;

namespace LogsPlatform.Tests.Web;

public class SparklineRendererTests
{
    [Fact]
    public void Render_EmptyCounts_ReturnsNoDataPlaceholder()
    {
        var result = SparklineRenderer.Render(new Dictionary<DateOnly, int>(), width: 100, height: 24);

        Assert.Contains("no data", result);
    }

    [Fact]
    public void Render_WithCounts_ReturnsSvgWithPolyline()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var counts = new Dictionary<DateOnly, int> { [today] = 5, [today.AddDays(-1)] = 2 };

        var result = SparklineRenderer.Render(counts, width: 100, height: 24);

        Assert.Contains("<svg", result);
        Assert.Contains("<polyline", result);
    }
}
