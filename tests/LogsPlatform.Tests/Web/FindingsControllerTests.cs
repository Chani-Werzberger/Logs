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
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
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
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
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
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var response = await client.GetAsync("/api/v1/findings/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateStatus_ValidStatus_Returns204AndPersists()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var (appId, envId) = await CreateAppWithEnvironmentAsync(client, "FindingsStatusTestApp");
        var finding = await SeedFindingAsync(appId, envId, FindingStatus.New, FindingSeverity.High);

        var response = await client.PatchAsJsonAsync($"/api/v1/findings/{finding.Id}/status", new UpdateFindingStatusRequest("Acknowledged"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var detailResponse = await client.GetAsync($"/api/v1/findings/{finding.Id}");
        var detail = await detailResponse.Content.ReadFromJsonAsync<FindingDetail>();
        Assert.Equal("Acknowledged", detail!.Status);
    }

    [Fact]
    public async Task UpdateStatus_InvalidStatusValue_Returns400()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var (appId, envId) = await CreateAppWithEnvironmentAsync(client, "FindingsStatusInvalidTestApp");
        var finding = await SeedFindingAsync(appId, envId, FindingStatus.New, FindingSeverity.High);

        var response = await client.PatchAsJsonAsync($"/api/v1/findings/{finding.Id}/status", new UpdateFindingStatusRequest("NotAStatus"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PromoteStatement_HypothesisStatement_Returns204AndPersists()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var (appId, envId) = await CreateAppWithEnvironmentAsync(client, "FindingsPromoteTestApp");
        var finding = await SeedFindingAsync(appId, envId, FindingStatus.New, FindingSeverity.High);

        long statementId;
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
            var statement = new FindingStatement { FindingId = finding.Id, Kind = FindingStatementKind.Hypothesis, Text = "Maybe a deployment.", OrderIndex = 1 };
            context.FindingStatements.Add(statement);
            await context.SaveChangesAsync();
            statementId = statement.Id;
        }

        var response = await client.PostAsJsonAsync($"/api/v1/findings/{finding.Id}/statements/{statementId}/promote", new PromoteStatementRequest("Dana"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var detailResponse = await client.GetAsync($"/api/v1/findings/{finding.Id}");
        var detail = await detailResponse.Content.ReadFromJsonAsync<FindingDetail>();
        var promoted = detail!.Statements.Single(s => s.Id == statementId);
        Assert.Equal("Conclusion", promoted.Kind);
        Assert.Equal("Dana", promoted.ApprovedBy);
    }

    [Fact]
    public async Task PromoteStatement_BlankApprovedBy_Returns400()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var (appId, envId) = await CreateAppWithEnvironmentAsync(client, "FindingsPromoteBlankTestApp");
        var finding = await SeedFindingAsync(appId, envId, FindingStatus.New, FindingSeverity.High);

        long statementId;
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
            var statement = new FindingStatement { FindingId = finding.Id, Kind = FindingStatementKind.Hypothesis, Text = "Maybe.", OrderIndex = 1 };
            context.FindingStatements.Add(statement);
            await context.SaveChangesAsync();
            statementId = statement.Id;
        }

        var response = await client.PostAsJsonAsync($"/api/v1/findings/{finding.Id}/statements/{statementId}/promote", new PromoteStatementRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
