namespace LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;

/// <summary>
/// RetailPulse/PullSupplierFeed's normally-hourly activity (PullSupplierFeedHourlyMean=15, well above
/// MIN_MEANINGFUL_ACTIVITY=5 so the "drop" isn't dismissed as noise) goes silent for the current hour.
/// The scenario IS the absence of data — this deliberately returns an empty list rather than "no
/// implementation needed," so the caller (Task 12) doesn't need a special case: it simply doesn't add
/// anything to the ingestion batch for this Operation's current hour, while 35 days of quiet-day
/// history already established a real Baseline with mean well above 5.
/// </summary>
public static class MissingActivityInjector
{
    public static IReadOnlyList<SimulatedEvent> Inject() => Array.Empty<SimulatedEvent>();
}
