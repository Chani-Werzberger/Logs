using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class HealthControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public HealthControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetHealth_NoCookie_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetHealth_DatabaseUpAndTickRecent_ReturnsHealthyWith200()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        using (var scope = _factory.Services.CreateScope())
        {
            var healthStatus = scope.ServiceProvider.GetRequiredService<AnalysisEngineHealthStatus>();
            healthStatus.RecordTickCompleted(DateTime.UtcNow);
        }

        var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("Healthy", body!.Status);
        Assert.Equal("Healthy", body.Database.Status);
        Assert.Equal("Healthy", body.AnalysisEngine.Status);
    }

    [Fact]
    public async Task GetHealth_TickIsStale_ReturnsUnhealthyWith503()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        using (var scope = _factory.Services.CreateScope())
        {
            var healthStatus = scope.ServiceProvider.GetRequiredService<AnalysisEngineHealthStatus>();
            healthStatus.RecordTickCompleted(DateTime.UtcNow.AddMinutes(-20));
        }

        var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("Unhealthy", body!.Status);
        Assert.Equal("Healthy", body.Database.Status);
        Assert.Equal("Stale", body.AnalysisEngine.Status);
    }
}
