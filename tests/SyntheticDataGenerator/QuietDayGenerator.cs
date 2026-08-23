namespace LogsPlatform.SyntheticDataGenerator;

public static class QuietDayGenerator
{
    /// <summary>
    /// Produces one (hour bucket start, event count) pair per hour, for `daysBack` historical days
    /// (day-offset 1..daysBack relative to "today") and optionally today's hours up to the current
    /// one (inclusive) when includeToday is true. Each count is drawn from hourlyMean(hourOfDay) with
    /// ±ScenarioConstants.NoiseRelativeRange relative jitter — never a fixed value, so BaselineCalculator
    /// sees real variance, but bounded predictably enough to reason about scenario magnitudes.
    /// </summary>
    public static IReadOnlyList<(DateTime HourStart, int Count)> GenerateHourlyEventCounts(
        Func<int, double> hourlyMean, int daysBack, bool includeToday, Random random)
    {
        var results = new List<(DateTime HourStart, int Count)>();
        var today = DateTime.UtcNow.Date;
        var currentHour = DateTime.UtcNow.Hour;

        for (var dayOffset = daysBack; dayOffset >= 1; dayOffset--)
        {
            var day = today.AddDays(-dayOffset);
            for (var hour = 0; hour < 24; hour++)
            {
                results.Add((day.AddHours(hour), SampleCount(hourlyMean(hour), random)));
            }
        }

        if (includeToday)
        {
            for (var hour = 0; hour <= currentHour; hour++)
            {
                results.Add((today.AddHours(hour), SampleCount(hourlyMean(hour), random)));
            }
        }

        return results;
    }

    public static IReadOnlyList<SimulatedEvent> ToEvents(
        IReadOnlyList<(DateTime HourStart, int Count)> hourlyCounts, string severity, string message,
        Func<DateTime, double?>? durationMs = null,
        string? module = null, string? screenService = null, string? process = null, string? operation = null,
        string? customerId = null)
    {
        var events = new List<SimulatedEvent>();
        foreach (var (hourStart, count) in hourlyCounts)
        {
            if (count == 0) continue;
            var spacingMinutes = 60.0 / count;
            for (var i = 0; i < count; i++)
            {
                var timestamp = hourStart.AddMinutes(i * spacingMinutes);
                events.Add(new SimulatedEvent(
                    timestamp, severity, module, screenService, process, operation,
                    CorrelationId: null, DurationMs: durationMs?.Invoke(timestamp), CustomerId: customerId,
                    Message: message, ExceptionType: null, StackTrace: null));
            }
        }
        return events;
    }

    private static int SampleCount(double mean, Random random)
    {
        var jitter = 1.0 + (random.NextDouble() * 2 - 1) * ScenarioConstants.NoiseRelativeRange; // [1-range, 1+range]
        var value = (int)Math.Round(mean * jitter);
        return Math.Max(0, value);
    }
}
