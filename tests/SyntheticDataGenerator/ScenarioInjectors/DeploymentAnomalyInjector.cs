using System.Net.Http.Json;

namespace LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;

/// <summary>
/// A new FieldOps version is deployed to Production 20 minutes before referenceTime (inside
/// DeploymentCorrelator's 60-minute DEPLOYMENT_CORRELATION_WINDOW), then AggregateJobs's event count
/// spikes far above its quiet-day mean (AggregateJobsBusinessHourMean=20) in the current hour —
/// DeploymentCorrelator picks up the Deployment automatically once RateAnomalyDetector's ErrorSpike
/// Finding is written, since it runs generically over every new Finding, not scenario-specific logic.
/// referenceTime must be the same value the caller passes to every other generator/injector call in
/// the same test — see QuietDayGenerator's remarks.
/// </summary>
public static class DeploymentAnomalyInjector
{
    public static async Task CreateDeploymentAsync(HttpClient client, AppFixture fieldOps, DateTime referenceTime)
    {
        var versionResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{fieldOps.ApplicationId}/versions",
            new { VersionNumber = "2.0.0", ReleaseNotes = "Synthetic scenario deployment" });
        versionResponse.EnsureSuccessStatusCode();
        var version = await versionResponse.Content.ReadFromJsonAsync<IdResponse>();

        var deployedAt = referenceTime.AddMinutes(-20);
        var deploymentResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{fieldOps.ApplicationId}/deployments",
            new { EnvironmentId = fieldOps.EnvironmentId, VersionId = version!.Id, DeployedAt = deployedAt, Notes = (string?)null });
        deploymentResponse.EnsureSuccessStatusCode();
    }

    public static IReadOnlyList<SimulatedEvent> InjectEvents(DateTime referenceTime)
    {
        var currentHourStart = referenceTime.Date.AddHours(referenceTime.Hour);
        var hourlyCounts = new List<(DateTime HourStart, int Count)> { (currentHourStart, ScenarioConstants.DeploymentAnomalyEventCount) };

        return QuietDayGenerator.ToEvents(hourlyCounts, "Error", "Job aggregation failed",
            module: ScenarioConstants.ReportingModule, screenService: ScenarioConstants.DailyReportScreenService,
            process: ScenarioConstants.GenerateReportProcess, operation: ScenarioConstants.AggregateJobsOperation);
    }

    private record IdResponse(int Id);
}
