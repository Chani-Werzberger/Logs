namespace LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;

/// <summary>
/// RetailPulse/ChargePayment starts failing at a rate far above baseline for the current hour.
/// ChargePaymentBusinessHourMean=50, so 260 events (ErrorSpikeEventCount) is ~5x even the
/// business-hour peak — reliably crosses SPIKE_THRESHOLD=3 regardless of the realized noise/stddev.
/// referenceTime must be the same value the caller passes to every other generator/injector call in
/// the same test and runs the tick with minimal delay after — see QuietDayGenerator's remarks.
/// </summary>
public static class ErrorSpikeInjector
{
    public static IReadOnlyList<SimulatedEvent> Inject(DateTime referenceTime)
    {
        var currentHourStart = referenceTime.Date.AddHours(referenceTime.Hour);
        var hourlyCounts = new List<(DateTime HourStart, int Count)> { (currentHourStart, ScenarioConstants.ErrorSpikeEventCount) };

        return QuietDayGenerator.ToEvents(hourlyCounts, "Error", "Card authorization failed",
            module: ScenarioConstants.OrdersModule, screenService: ScenarioConstants.OrderApiServiceScreenService,
            process: ScenarioConstants.CreateOrderProcess, operation: ScenarioConstants.ChargePaymentOperation);
    }
}
