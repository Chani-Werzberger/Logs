using LogsPlatform.SyntheticDataGenerator;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

[Collection("Database")]
public class FalsePositiveTests
{
    [Theory]
    [InlineData(1001)]
    [InlineData(2002)]
    [InlineData(3003)]
    public async Task QuietHistoryOnly_ProducesZeroFindings(int seed)
    {
        using var factory = new ScenarioTestWebApplicationFactory();
        var client = factory.CreateClient();
        var random = new Random(seed);

        var retailPulse = await DomainFixture.BuildRetailPulseAsync(client);
        var fieldOps = await DomainFixture.BuildFieldOpsAsync(client);

        // Historical (day-offset >= 1) traffic never touches "today," so it's safe to generate and
        // ingest first — this is the slow, bulk part, and BaselineCalculator never reads today's data
        // anyway. Only the "today" phase below is time-sensitive.
        await IngestHistoricalTrafficAsync(client, retailPulse, fieldOps, random);

        // Capture one referenceTime and generate+ingest every Operation's "today, current hour" data
        // from it immediately, then run the tick right after — minimizing the window in which a real
        // hour boundary could roll over between "what today's data covers" and "what the tick checks."
        // See QuietDayGenerator's remarks: an earlier version of this test read DateTime.UtcNow
        // separately per Operation across a multi-minute ingestion pass, and a genuine hour rollover
        // mid-run left one Operation's current hour empty, producing a real spurious MissingActivity
        // Finding — not a threshold/statistics issue, a test-harness time-skew bug.
        var referenceTime = DateTime.UtcNow;
        await IngestTodayTrafficAsync(client, retailPulse, fieldOps, random, referenceTime);

        using (var scope = factory.Services.CreateScope())
        {
            var tickRunner = scope.ServiceProvider.GetRequiredService<AnalysisEngineTickRunner>();
            await tickRunner.RunOneTickAsync();
        }

        var retailPulseFindings = await GetFindingsAsync(client, retailPulse);
        var fieldOpsFindings = await GetFindingsAsync(client, fieldOps);

        Assert.Empty(retailPulseFindings);
        Assert.Empty(fieldOpsFindings);
    }

    private static async Task IngestHistoricalTrafficAsync(HttpClient client, AppFixture retailPulse, AppFixture fieldOps, Random random)
    {
        var referenceTime = DateTime.UtcNow; // only used to anchor which days count as "historical"; hour-of-day irrelevant since includeToday=false

        var chargePaymentCounts = QuietDayGenerator.GenerateHourlyEventCounts(
            hour => ScenarioConstants.BusinessHours.Contains(hour) ? ScenarioConstants.ChargePaymentBusinessHourMean : ScenarioConstants.ChargePaymentNightHourMean,
            ScenarioConstants.QuietDaysBack, includeToday: false, random, referenceTime);
        await IngestionSender.SendBatchedAsync(client, retailPulse.ApiKey,
            QuietDayGenerator.ToEvents(chargePaymentCounts, "Info", "Card authorized",
                module: ScenarioConstants.OrdersModule, screenService: ScenarioConstants.OrderApiServiceScreenService,
                process: ScenarioConstants.CreateOrderProcess, operation: ScenarioConstants.ChargePaymentOperation));

        var pullSupplierFeedCounts = QuietDayGenerator.GenerateHourlyEventCounts(
            _ => ScenarioConstants.PullSupplierFeedHourlyMean, ScenarioConstants.QuietDaysBack, includeToday: false, random, referenceTime);
        await IngestionSender.SendBatchedAsync(client, retailPulse.ApiKey,
            QuietDayGenerator.ToEvents(pullSupplierFeedCounts, "Info", "Supplier feed pulled",
                module: ScenarioConstants.InventoryModule, screenService: ScenarioConstants.StockServiceScreenService,
                process: ScenarioConstants.StockSyncProcess, operation: ScenarioConstants.PullSupplierFeedOperation));

        var matchAvailabilityCounts = QuietDayGenerator.GenerateHourlyEventCounts(
            hour => ScenarioConstants.BusinessHours.Contains(hour) ? 20 : 3,
            ScenarioConstants.QuietDaysBack, includeToday: false, random, referenceTime);
        await IngestionSender.SendBatchedAsync(client, fieldOps.ApiKey,
            QuietDayGenerator.ToEvents(matchAvailabilityCounts, "Info", "Technician availability matched",
                durationMs: _ => ScenarioConstants.MatchAvailabilityDurationMeanMs + (random.NextDouble() * 2 - 1) * ScenarioConstants.MatchAvailabilityDurationMeanMs * ScenarioConstants.NoiseRelativeRange,
                module: ScenarioConstants.SchedulingModule, screenService: ScenarioConstants.SchedulerApiScreenService,
                process: ScenarioConstants.AssignTechnicianProcess, operation: ScenarioConstants.MatchAvailabilityOperation));

        var aggregateJobsCounts = QuietDayGenerator.GenerateHourlyEventCounts(
            hour => ScenarioConstants.BusinessHours.Contains(hour) ? ScenarioConstants.AggregateJobsBusinessHourMean : ScenarioConstants.AggregateJobsNightHourMean,
            ScenarioConstants.QuietDaysBack, includeToday: false, random, referenceTime);
        await IngestionSender.SendBatchedAsync(client, fieldOps.ApiKey,
            QuietDayGenerator.ToEvents(aggregateJobsCounts, "Info", "Jobs aggregated",
                module: ScenarioConstants.ReportingModule, screenService: ScenarioConstants.DailyReportScreenService,
                process: ScenarioConstants.GenerateReportProcess, operation: ScenarioConstants.AggregateJobsOperation));
    }

    private static async Task IngestTodayTrafficAsync(HttpClient client, AppFixture retailPulse, AppFixture fieldOps, Random random, DateTime referenceTime)
    {
        var chargePaymentToday = QuietDayGenerator.GenerateHourlyEventCounts(
            hour => ScenarioConstants.BusinessHours.Contains(hour) ? ScenarioConstants.ChargePaymentBusinessHourMean : ScenarioConstants.ChargePaymentNightHourMean,
            daysBack: 0, includeToday: true, random, referenceTime);
        await IngestionSender.SendBatchedAsync(client, retailPulse.ApiKey,
            QuietDayGenerator.ToEvents(chargePaymentToday, "Info", "Card authorized",
                module: ScenarioConstants.OrdersModule, screenService: ScenarioConstants.OrderApiServiceScreenService,
                process: ScenarioConstants.CreateOrderProcess, operation: ScenarioConstants.ChargePaymentOperation));

        var pullSupplierFeedToday = QuietDayGenerator.GenerateHourlyEventCounts(
            _ => ScenarioConstants.PullSupplierFeedHourlyMean, daysBack: 0, includeToday: true, random, referenceTime);
        await IngestionSender.SendBatchedAsync(client, retailPulse.ApiKey,
            QuietDayGenerator.ToEvents(pullSupplierFeedToday, "Info", "Supplier feed pulled",
                module: ScenarioConstants.InventoryModule, screenService: ScenarioConstants.StockServiceScreenService,
                process: ScenarioConstants.StockSyncProcess, operation: ScenarioConstants.PullSupplierFeedOperation));

        var matchAvailabilityToday = QuietDayGenerator.GenerateHourlyEventCounts(
            hour => ScenarioConstants.BusinessHours.Contains(hour) ? 20 : 3,
            daysBack: 0, includeToday: true, random, referenceTime);
        await IngestionSender.SendBatchedAsync(client, fieldOps.ApiKey,
            QuietDayGenerator.ToEvents(matchAvailabilityToday, "Info", "Technician availability matched",
                durationMs: _ => ScenarioConstants.MatchAvailabilityDurationMeanMs + (random.NextDouble() * 2 - 1) * ScenarioConstants.MatchAvailabilityDurationMeanMs * ScenarioConstants.NoiseRelativeRange,
                module: ScenarioConstants.SchedulingModule, screenService: ScenarioConstants.SchedulerApiScreenService,
                process: ScenarioConstants.AssignTechnicianProcess, operation: ScenarioConstants.MatchAvailabilityOperation));

        var aggregateJobsToday = QuietDayGenerator.GenerateHourlyEventCounts(
            hour => ScenarioConstants.BusinessHours.Contains(hour) ? ScenarioConstants.AggregateJobsBusinessHourMean : ScenarioConstants.AggregateJobsNightHourMean,
            daysBack: 0, includeToday: true, random, referenceTime);
        await IngestionSender.SendBatchedAsync(client, fieldOps.ApiKey,
            QuietDayGenerator.ToEvents(aggregateJobsToday, "Info", "Jobs aggregated",
                module: ScenarioConstants.ReportingModule, screenService: ScenarioConstants.DailyReportScreenService,
                process: ScenarioConstants.GenerateReportProcess, operation: ScenarioConstants.AggregateJobsOperation));
    }

    private static async Task<List<FindingSummary>> GetFindingsAsync(HttpClient client, AppFixture app)
    {
        var response = await client.GetAsync($"/api/v1/findings?applicationId={app.ApplicationId}&environmentId={app.EnvironmentId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<FindingSummary>>())!;
    }
}
