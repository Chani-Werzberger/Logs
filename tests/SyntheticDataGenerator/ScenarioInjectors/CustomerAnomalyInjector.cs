namespace LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;

public static class CustomerAnomalyInjector
{
    public static IReadOnlyList<SimulatedEvent> Inject(IReadOnlyList<string> customerIds)
    {
        if (customerIds.Count < ScenarioConstants.CustomerAnomalyPeerCount + 1)
        {
            throw new ArgumentException($"Need at least {ScenarioConstants.CustomerAnomalyPeerCount + 1} customer ids.", nameof(customerIds));
        }

        var events = new List<SimulatedEvent>();
        var windowStart = DateTime.UtcNow.AddHours(-24);

        for (var i = 0; i < ScenarioConstants.CustomerAnomalyPeerCount; i++)
        {
            events.AddRange(SpreadAcrossWindow(customerIds[i], ScenarioConstants.CustomerAnomalyPeerConfirmOrderCount, windowStart));
        }

        var outlierId = customerIds[ScenarioConstants.CustomerAnomalyPeerCount];
        events.AddRange(SpreadAcrossWindow(outlierId, ScenarioConstants.CustomerAnomalyOutlierConfirmOrderCount, windowStart));

        return events;
    }

    private static IEnumerable<SimulatedEvent> SpreadAcrossWindow(string customerId, int count, DateTime windowStart)
    {
        var spacingMinutes = (24.0 * 60) / count;
        for (var i = 0; i < count; i++)
        {
            yield return new SimulatedEvent(
                windowStart.AddMinutes(i * spacingMinutes), "Info",
                ScenarioConstants.OrdersModule, ScenarioConstants.OrderApiServiceScreenService,
                ScenarioConstants.CreateOrderProcess, ScenarioConstants.ConfirmOrderOperation,
                CorrelationId: null, DurationMs: null, CustomerId: customerId,
                Message: "Order confirmed", ExceptionType: null, StackTrace: null);
        }
    }
}
