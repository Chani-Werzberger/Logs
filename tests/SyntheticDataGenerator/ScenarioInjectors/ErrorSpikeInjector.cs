namespace LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;

/// <summary>
/// RetailPulse/ChargePayment starts failing at a rate far above baseline for the current hour.
/// ChargePaymentBusinessHourMean=50, so 260 events (ErrorSpikeEventCount) is ~5x even the
/// business-hour peak — reliably crosses SPIKE_THRESHOLD=3 regardless of the realized noise/stddev.
/// </summary>
public static class ErrorSpikeInjector
{
    public static IReadOnlyList<SimulatedEvent> Inject()
    {
        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        var hourlyCounts = new List<(DateTime HourStart, int Count)> { (currentHourStart, ScenarioConstants.ErrorSpikeEventCount) };

        return QuietDayGenerator.ToEvents(hourlyCounts, "Error", "Card authorization failed",
            module: ScenarioConstants.OrdersModule, screenService: ScenarioConstants.OrderApiServiceScreenService,
            process: ScenarioConstants.CreateOrderProcess, operation: ScenarioConstants.ChargePaymentOperation);
    }
}
