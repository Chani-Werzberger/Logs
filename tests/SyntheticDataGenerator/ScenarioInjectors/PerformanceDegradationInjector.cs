namespace LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;

/// <summary>
/// FieldOps/MatchAvailability's per-event duration climbs to 900ms (PerformanceDegradationDurationMs)
/// vs. a 200ms quiet-day mean — event COUNT for the hour stays at a normal level (eventCount is passed
/// in matching the caller's own quiet-hour curve) so only the DurationMs baseline is disturbed, never
/// the EventCount one (which would otherwise also fire an unrelated ErrorSpike/MissingActivity Finding
/// on the same Operation). referenceTime must be the same value the caller passes to every other
/// generator/injector call in the same test — see QuietDayGenerator's remarks.
/// </summary>
public static class PerformanceDegradationInjector
{
    public static IReadOnlyList<SimulatedEvent> Inject(int eventCount, DateTime referenceTime)
    {
        var currentHourStart = referenceTime.Date.AddHours(referenceTime.Hour);
        var hourlyCounts = new List<(DateTime HourStart, int Count)> { (currentHourStart, eventCount) };

        return QuietDayGenerator.ToEvents(hourlyCounts, "Info", "Technician availability matched",
            durationMs: _ => ScenarioConstants.PerformanceDegradationDurationMs,
            module: ScenarioConstants.SchedulingModule, screenService: ScenarioConstants.SchedulerApiScreenService,
            process: ScenarioConstants.AssignTechnicianProcess, operation: ScenarioConstants.MatchAvailabilityOperation);
    }
}
