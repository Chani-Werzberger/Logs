namespace LogsPlatform.SyntheticDataGenerator;

public static class ScenarioConstants
{
    // Hierarchy names — must match 06-מודל-אפליקציה.md §4 exactly.
    public const string RetailPulseApp = "RetailPulse";
    public const string OrdersModule = "Orders";
    public const string OrderApiServiceScreenService = "OrderApiService";
    public const string CreateOrderProcess = "CreateOrder";
    public const string ValidateCartOperation = "ValidateCart";
    public const string ReserveStockOperation = "ReserveStock";
    public const string ChargePaymentOperation = "ChargePayment";
    public const string ConfirmOrderOperation = "ConfirmOrder";
    public const string InventoryModule = "Inventory";
    public const string StockServiceScreenService = "StockService";
    public const string StockSyncProcess = "StockSync";
    public const string PullSupplierFeedOperation = "PullSupplierFeed";

    public const string FieldOpsApp = "FieldOps";
    public const string SchedulingModule = "Scheduling";
    public const string SchedulerApiScreenService = "SchedulerApi";
    public const string AssignTechnicianProcess = "AssignTechnician";
    public const string MatchAvailabilityOperation = "MatchAvailability";
    public const string ReportingModule = "Reporting";
    public const string DailyReportScreenService = "DailyReport";
    public const string GenerateReportProcess = "GenerateReport";
    public const string AggregateJobsOperation = "AggregateJobs";

    // Quiet-day traffic curves (events/hour, business hours = 08:00-17:59 inclusive).
    public const int ChargePaymentBusinessHourMean = 50;
    public const int ChargePaymentNightHourMean = 5;
    public const int MatchAvailabilityDurationMeanMs = 200;
    public const int PullSupplierFeedHourlyMean = 15;
    public const int AggregateJobsBusinessHourMean = 20;
    public const int AggregateJobsNightHourMean = 3;

    public const double NoiseRelativeRange = 0.3; // ±30%
    public const int QuietDaysBack = 35;

    // Scenario magnitudes — large margins over SPIKE_THRESHOLD=3 by design (see Global Constraints).
    public const int ErrorSpikeEventCount = 260;          // vs. ChargePayment business-hour mean 50 → z >> 3
    public const int PerformanceDegradationDurationMs = 900; // vs. mean 200ms
    public const int DeploymentAnomalyEventCount = 100;   // vs. AggregateJobs business-hour mean 20

    public const int CustomerAnomalyPeerCount = 14;        // >= MIN_SAMPLES(14) so CustomerAnomaly reaches Confidence=High
    public const int CustomerAnomalyPeerConfirmOrderCount = 10;
    public const int CustomerAnomalyOutlierConfirmOrderCount = 60;

    public static readonly int[] BusinessHours = Enumerable.Range(8, 10).ToArray(); // 08:00-17:59
}
