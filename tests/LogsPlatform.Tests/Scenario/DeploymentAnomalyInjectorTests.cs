using LogsPlatform.SyntheticDataGenerator;
using LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using System.Net.Http.Json;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

[Collection("Database")]
public class DeploymentAnomalyInjectorTests
{
    [Fact]
    public async Task CreateDeploymentAsync_CreatesDeploymentWithinCorrelationWindow()
    {
        using var factory = new ScenarioTestWebApplicationFactory();
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(factory);
        var fieldOps = await DomainFixture.BuildFieldOpsAsync(client);

        await DeploymentAnomalyInjector.CreateDeploymentAsync(client, fieldOps, DateTime.UtcNow);

        var response = await client.GetAsync($"/api/v1/admin/applications/{fieldOps.ApplicationId}/deployments");
        response.EnsureSuccessStatusCode();
        var deployments = await response.Content.ReadFromJsonAsync<List<DeploymentResponse>>();

        Assert.Single(deployments!);
        var deployedAt = deployments![0].DeployedAt;
        Assert.True(deployedAt > DateTime.UtcNow.AddMinutes(-60) && deployedAt < DateTime.UtcNow);
    }
}
