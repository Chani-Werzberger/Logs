using LogsPlatform.SyntheticDataGenerator;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

public class QuietDayGeneratorTests
{
    [Fact]
    public void GenerateHourlyEventCounts_ExcludesToday_WhenIncludeTodayFalse()
    {
        var random = new Random(42);
        var counts = QuietDayGenerator.GenerateHourlyEventCounts(hour => 50, daysBack: 5, includeToday: false, random);

        var today = DateTime.UtcNow.Date;
        Assert.DoesNotContain(counts, c => c.HourStart.Date == today);
        Assert.Equal(5 * 24, counts.Count);
    }

    [Fact]
    public void GenerateHourlyEventCounts_IncludesToday_WhenIncludeTodayTrue()
    {
        var random = new Random(42);
        var counts = QuietDayGenerator.GenerateHourlyEventCounts(hour => 50, daysBack: 5, includeToday: true, random);

        var currentHour = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        Assert.Contains(counts, c => c.HourStart == currentHour);
    }

    [Fact]
    public void GenerateHourlyEventCounts_CountsVaryAroundMean_NotConstant()
    {
        var random = new Random(42);
        var counts = QuietDayGenerator.GenerateHourlyEventCounts(hour => 50, daysBack: 10, includeToday: false, random);

        var distinctValues = counts.Select(c => c.Count).Distinct().Count();
        Assert.True(distinctValues > 1, "Noise model produced a constant value across all hours.");

        var average = counts.Average(c => c.Count);
        Assert.InRange(average, 35, 65); // mean 50 ± the ±30% noise range, generously bounded
    }

    [Fact]
    public void ToEvents_SpreadsCountAcrossTheHour()
    {
        var hourStart = DateTime.UtcNow.Date;
        var counts = new List<(DateTime HourStart, int Count)> { (hourStart, 10) };

        var events = QuietDayGenerator.ToEvents(counts, "Info", "quiet traffic");

        Assert.Equal(10, events.Count);
        Assert.All(events, e => Assert.InRange(e.Timestamp, hourStart, hourStart.AddHours(1)));
    }
}
