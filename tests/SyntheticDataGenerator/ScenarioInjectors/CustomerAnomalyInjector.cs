namespace LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;

/// <summary>
/// referenceTime must be the same value the caller passes to every other generator/injector call in
/// the same test — see QuietDayGenerator's remarks.
///
/// Events are spread from midnight of referenceTime's day through referenceTime — NOT a full rolling
/// 24 hours back — even though CustomerOutlierDetector's own production query really does use a
/// rolling 24h window. A full 24h-back window would inevitably spill some events into "yesterday"
/// (day-offset >= 1), which BaselineCalculator picks up as historical data for ConfirmOrder — an
/// Operation deliberately kept baseline-free elsewhere in this plan (see Global Constraints) so
/// RateAnomalyDetector never evaluates it. That spillover was a real bug caught while running
/// ScenarioAcceptanceTests: a single fragmentary historical sample gave ConfirmOrder just enough of a
/// Baseline for its current hour to look like a drop, producing a spurious MissingActivity Finding.
/// CustomerOutlierDetector's own query only counts events within the last 24 real hours — it doesn't
/// care how they're spaced — so confining generation to "today" only doesn't change what's being
/// tested, it just keeps this Operation invisible to the hourly-baseline machinery entirely.
/// </summary>
public static class CustomerAnomalyInjector
{
    public static IReadOnlyList<SimulatedEvent> Inject(IReadOnlyList<string> customerIds, DateTime referenceTime)
    {
        if (customerIds.Count < ScenarioConstants.CustomerAnomalyPeerCount + 1)
        {
            throw new ArgumentException($"Need at least {ScenarioConstants.CustomerAnomalyPeerCount + 1} customer ids.", nameof(customerIds));
        }

        var events = new List<SimulatedEvent>();
        var windowStart = referenceTime.Date;

        for (var i = 0; i < ScenarioConstants.CustomerAnomalyPeerCount; i++)
        {
            events.AddRange(SpreadAcrossWindow(customerIds[i], ScenarioConstants.CustomerAnomalyPeerConfirmOrderCount, windowStart, referenceTime));
        }

        var outlierId = customerIds[ScenarioConstants.CustomerAnomalyPeerCount];
        events.AddRange(SpreadAcrossWindow(outlierId, ScenarioConstants.CustomerAnomalyOutlierConfirmOrderCount, windowStart, referenceTime));

        return events;
    }

    private static IEnumerable<SimulatedEvent> SpreadAcrossWindow(string customerId, int count, DateTime windowStart, DateTime windowEnd)
    {
        var totalMinutes = Math.Max((windowEnd - windowStart).TotalMinutes, 1);
        var spacingMinutes = totalMinutes / count;
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
