namespace LogsPlatform.Web.Services;

public static class SparklineRenderer
{
    public static string Render(IReadOnlyDictionary<DateOnly, int> dailyCounts, int width, int height)
    {
        if (dailyCounts.Count == 0)
        {
            return "<span class=\"text-muted\">no data</span>";
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var days = Enumerable.Range(0, 14).Select(offset => today.AddDays(-13 + offset)).ToList();
        var values = days.Select(day => dailyCounts.TryGetValue(day, out var count) ? count : 0).ToList();
        var max = Math.Max(values.Max(), 1);

        var points = values.Select((value, index) =>
        {
            var x = (double)index / (values.Count - 1) * width;
            var y = height - (double)value / max * height;
            return $"{x:F1},{y:F1}";
        });

        return $"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\"><polyline fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.5\" points=\"{string.Join(" ", points)}\" /></svg>";
    }
}
