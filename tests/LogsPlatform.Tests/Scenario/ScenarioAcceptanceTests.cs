using LogsPlatform.Domain.Entities;
using LogsPlatform.SyntheticDataGenerator;
using LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

[Collection("Database")]
public class ScenarioAcceptanceTests
{
    private const int Seed = 777;

    [Fact]
    public async Task QuietHistoryPlusSixScenarios_ProducesExactlySixCorrectFindings()
    {
        using var factory = new ScenarioTestWebApplicationFactory();
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(factory);
        var random = new Random(Seed);

        var retailPulse = await DomainFixture.BuildRetailPulseAsync(client);
        var fieldOps = await DomainFixture.BuildFieldOpsAsync(client);
        var customerIds = await DomainFixture.SeedCustomersAsync(client, retailPulse.ApplicationId, ScenarioConstants.CustomerAnomalyPeerCount + 1);

        // Historical (day-offset >= 1) traffic never touches "today," so it's safe to generate and
        // ingest first — the slow, bulk part. Only Phase 2 below is time-sensitive.
        await IngestHistoricalTrafficAsync(client, retailPulse, fieldOps, random);

        // Capture one referenceTime and inject every scenario from it immediately, then run the tick
        // right after — minimizing the window in which a real hour boundary could roll over between
        // "what the scenarios target" and "what the tick checks." See QuietDayGenerator's remarks and
        // FalsePositiveTests' commit message for the real bug this pattern was built to avoid.
        var referenceTime = DateTime.UtcNow;

        await IngestionSender.SendBatchedAsync(client, retailPulse.ApiKey, ErrorSpikeInjector.Inject(referenceTime));
        await IngestionSender.SendBatchedAsync(client, retailPulse.ApiKey, NewExceptionInjector.Inject(referenceTime));
        await IngestionSender.SendBatchedAsync(client, retailPulse.ApiKey, CustomerAnomalyInjector.Inject(customerIds, referenceTime));

        var matchAvailabilityNormalCount = ScenarioConstants.BusinessHours.Contains(referenceTime.Hour) ? 20 : 3;
        await IngestionSender.SendBatchedAsync(client, fieldOps.ApiKey, PerformanceDegradationInjector.Inject(matchAvailabilityNormalCount, referenceTime));

        await DeploymentAnomalyInjector.CreateDeploymentAsync(client, fieldOps, referenceTime);
        await IngestionSender.SendBatchedAsync(client, fieldOps.ApiKey, DeploymentAnomalyInjector.InjectEvents(referenceTime));
        // MissingActivityInjector.Inject() returns no events by design — PullSupplierFeed's current
        // hour is simply never ingested, leaving it silent against its established Baseline.

        using (var scope = factory.Services.CreateScope())
        {
            var tickRunner = scope.ServiceProvider.GetRequiredService<AnalysisEngineTickRunner>();
            await tickRunner.RunOneTickAsync();
        }

        var retailPulseFindings = await GetFindingsAsync(client, retailPulse);
        var fieldOpsFindings = await GetFindingsAsync(client, fieldOps);
        var allFindings = retailPulseFindings.Concat(fieldOpsFindings).ToList();

        if (allFindings.Count != 6)
        {
            var summary = string.Join("\n", allFindings.Select(f => $"  Id={f.Id} Type={f.Type} App={f.ApplicationName} Operation={f.OperationName} Severity={f.Severity} Confidence={f.ConfidenceLevel} Title={f.Title}"));
            throw new Exception($"Expected 6 findings, got {allFindings.Count}:\n{summary}");
        }

        await AssertErrorSpikeAsync(client, retailPulseFindings);
        await AssertNewExceptionAsync(client, retailPulseFindings);
        AssertMissingActivity(retailPulseFindings);
        AssertCustomerAnomaly(retailPulseFindings);
        await AssertPerformanceDegradationAsync(client, fieldOpsFindings);
        await AssertDeploymentAnomalyAsync(client, fieldOpsFindings);
    }

    private static async Task IngestHistoricalTrafficAsync(HttpClient client, AppFixture retailPulse, AppFixture fieldOps, Random random)
    {
        var referenceTime = DateTime.UtcNow; // only anchors which days count as "historical"; hour-of-day irrelevant since includeToday=false

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

    private static async Task<List<FindingSummary>> GetFindingsAsync(HttpClient client, AppFixture app)
    {
        var response = await client.GetAsync($"/api/v1/findings?applicationId={app.ApplicationId}&environmentId={app.EnvironmentId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<FindingSummary>>())!;
    }

    private static async Task<FindingDetail> GetDetailAsync(HttpClient client, long findingId)
    {
        var response = await client.GetAsync($"/api/v1/findings/{findingId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FindingDetail>())!;
    }

    private static async Task AssertErrorSpikeAsync(HttpClient client, List<FindingSummary> findings)
    {
        var finding = Assert.Single(findings, f => f.Type == nameof(FindingType.ErrorSpike));
        Assert.Equal(nameof(FindingSeverity.High), finding.Severity);
        Assert.Equal(nameof(ConfidenceLevel.High), finding.ConfidenceLevel);
        Assert.Equal(ScenarioConstants.ChargePaymentOperation, finding.OperationName);

        var detail = await GetDetailAsync(client, finding.Id);
        var fact = Assert.Single(detail.Statements, s => s.Kind == nameof(FindingStatementKind.Fact));
        Assert.Contains(ScenarioConstants.ErrorSpikeEventCount.ToString(), fact.Text);
    }

    private static async Task AssertNewExceptionAsync(HttpClient client, List<FindingSummary> findings)
    {
        var finding = Assert.Single(findings, f => f.Type == nameof(FindingType.NewException));
        Assert.Equal(nameof(FindingSeverity.High), finding.Severity);
        Assert.Equal(nameof(ConfidenceLevel.High), finding.ConfidenceLevel);

        var detail = await GetDetailAsync(client, finding.Id);
        Assert.Contains(detail.Statements, s => s.Kind == nameof(FindingStatementKind.Hypothesis));
        Assert.Contains(detail.Evidence, e => e.EvidenceType == nameof(EvidenceType.Event));
    }

    private static void AssertMissingActivity(List<FindingSummary> findings)
    {
        var finding = Assert.Single(findings, f => f.Type == nameof(FindingType.MissingActivity));
        Assert.Equal(nameof(ConfidenceLevel.High), finding.ConfidenceLevel);
        Assert.Equal(ScenarioConstants.PullSupplierFeedOperation, finding.OperationName);
    }

    private static void AssertCustomerAnomaly(List<FindingSummary> findings)
    {
        var finding = Assert.Single(findings, f => f.Type == nameof(FindingType.CustomerAnomaly));
        Assert.Equal(nameof(ConfidenceLevel.High), finding.ConfidenceLevel);
    }

    private static async Task AssertPerformanceDegradationAsync(HttpClient client, List<FindingSummary> findings)
    {
        var finding = Assert.Single(findings, f => f.Type == nameof(FindingType.PerformanceDegradation));
        Assert.Equal(nameof(ConfidenceLevel.High), finding.ConfidenceLevel);
        Assert.Equal(ScenarioConstants.MatchAvailabilityOperation, finding.OperationName);

        var detail = await GetDetailAsync(client, finding.Id);
        var fact = Assert.Single(detail.Statements, s => s.Kind == nameof(FindingStatementKind.Fact));
        Assert.Contains(ScenarioConstants.PerformanceDegradationDurationMs.ToString(), fact.Text);
    }

    private static async Task AssertDeploymentAnomalyAsync(HttpClient client, List<FindingSummary> findings)
    {
        var finding = Assert.Single(findings, f => f.Type == nameof(FindingType.ErrorSpike) && f.OperationName == ScenarioConstants.AggregateJobsOperation);
        Assert.Equal(nameof(ConfidenceLevel.High), finding.ConfidenceLevel);

        var detail = await GetDetailAsync(client, finding.Id);
        Assert.Contains(detail.Statements, s => s.Kind == nameof(FindingStatementKind.Hypothesis));
        Assert.Contains(detail.Evidence, e => e.EvidenceType == nameof(EvidenceType.Deployment));
    }
}
