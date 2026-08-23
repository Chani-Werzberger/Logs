namespace LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;

/// <summary>
/// RetailPulse/ReserveStock throws an exception type never seen in this test's history (nothing else
/// in this plan uses "StockUnavailableException"), triggering NewExceptionDetector. A second event,
/// 5 seconds later on a DIFFERENT Operation (ValidateCart) sharing the same CorrelationId with
/// Severity=Error, satisfies DownstreamFailureCorrelator's exact matching rule (Timestamp after
/// trigger, different OperationId, Severity >= ERROR_SEVERITY_FLOOR=17), so the resulting Finding
/// also carries a Downstream-Failure Hypothesis + Evidence, per 11-Test-Strategy.md §3's criterion.
/// Neither Operation ever appears in quiet-day generation, so no historical Baseline exists for
/// either — RateAnomalyDetector silently skips both (see Global Constraints), avoiding any
/// confounding rate-based Finding.
/// </summary>
public static class NewExceptionInjector
{
    public static IReadOnlyList<SimulatedEvent> Inject()
    {
        var correlationId = $"order-{Guid.NewGuid():N}";
        var triggerTime = DateTime.UtcNow.AddSeconds(-30);

        var trigger = new SimulatedEvent(
            triggerTime, "Error", ScenarioConstants.OrdersModule, ScenarioConstants.OrderApiServiceScreenService,
            ScenarioConstants.CreateOrderProcess, ScenarioConstants.ReserveStockOperation,
            correlationId, DurationMs: null, CustomerId: null,
            Message: "Stock reservation failed unexpectedly",
            ExceptionType: "StockUnavailableException", StackTrace: "at StockService.Reserve() line 42");

        var downstream = new SimulatedEvent(
            triggerTime.AddSeconds(5), "Error", ScenarioConstants.OrdersModule, ScenarioConstants.OrderApiServiceScreenService,
            ScenarioConstants.CreateOrderProcess, ScenarioConstants.ValidateCartOperation,
            correlationId, DurationMs: null, CustomerId: null,
            Message: "Cart validation failed after stock reservation error",
            ExceptionType: null, StackTrace: null);

        return new[] { trigger, downstream };
    }
}
