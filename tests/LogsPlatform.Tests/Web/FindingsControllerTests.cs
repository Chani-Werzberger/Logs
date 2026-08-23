using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class FindingsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public FindingsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<(int ApplicationId, int EnvironmentId)> CreateAppWithEnvironmentAsync(HttpClient client, string appName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var envResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));
        var env = await envResponse.Content.ReadFromJsonAsync<EnvironmentResponse>();
        return (app.Id, env!.Id);
    }

    private async Task<Finding> SeedFindingAsync(int applicationId, int environmentId, FindingStatus status, FindingSeverity severity)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var finding = new Finding
        {
            ApplicationId = applicationId, EnvironmentId = environmentId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "Seeded finding",
            DetectedAt = DateTime.UtcNow, Severity = severity, ConfidenceLevel = ConfidenceLevel.High, Status = status
        };
        context.Findings.Add(finding);
        await context.SaveChangesAsync();
        context.FindingStatements.Add(new FindingStatement { FindingId = finding.Id, Kind = FindingStatementKind.Fact, Text = "A fact.", OrderIndex = 0 });
        context.Evidence.Add(new Evidence { FindingId = finding.Id, EvidenceType = EvidenceType.Deployment, ReferenceId = 1, Description = "Deployment #1" });
        await context.SaveChangesAsync();
        return finding;
    }

    [Fact]
    public async Task GetFindings_FiltersByStatus()
    {
        var client = _factory.CreateClient();
        var (appId, envId) = await CreateAppWithEnvironmentAsync(client, "FindingsQueryTestApp");
        await SeedFindingAsync(appId, envId, FindingStatus.New, FindingSeverity.High);
        await SeedFindingAsync(appId, envId, FindingStatus.Resolved, FindingSeverity.Low);

        var response = await client.GetAsync($"/api/v1/findings?applicationId={appId}&environmentId={envId}&status=New");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<FindingSummary>>();
        Assert.Single(results!);
        Assert.Equal("New", results![0].Status);
    }

    [Fact]
    public async Task GetFindingById_ReturnsStatementsAndEvidence()
    {
        var client = _factory.CreateClient();
        var (appId, envId) = await CreateAppWithEnvironmentAsync(client, "FindingsDetailTestApp");
        var finding = await SeedFindingAsync(appId, envId, FindingStatus.New, FindingSeverity.High);

        var response = await client.GetAsync($"/api/v1/findings/{finding.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<FindingDetail>();
        Assert.Single(detail!.Statements);
        Assert.Single(detail.Evidence);
        Assert.Equal("Deployment", detail.Evidence[0].EvidenceType);
    }

    [Fact]
    public async Task GetFindingById_NotFound_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/findings/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
