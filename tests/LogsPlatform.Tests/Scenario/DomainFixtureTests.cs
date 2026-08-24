using System.Net.Http.Json;
using LogsPlatform.SyntheticDataGenerator;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Tests.Web;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

[Collection("Database")]
public class DomainFixtureTests
{
    [Fact]
    public async Task BuildRetailPulseAsync_CreatesApplicationWithApiKeyAndEnvironment()
    {
        using var factory = new TestWebApplicationFactory();
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(factory);

        var fixture = await DomainFixture.BuildRetailPulseAsync(client);

        Assert.True(fixture.ApplicationId > 0);
        Assert.True(fixture.EnvironmentId > 0);
        Assert.StartsWith("lgp_", fixture.ApiKey);

        var response = await client.GetAsync($"/api/v1/admin/applications/{fixture.ApplicationId}");
        response.EnsureSuccessStatusCode();
        var app = await response.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.Equal("RetailPulse", app!.Name);
    }

    [Fact]
    public async Task BuildFieldOpsAsync_CreatesApplicationWithApiKeyAndEnvironment()
    {
        using var factory = new TestWebApplicationFactory();
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(factory);

        var fixture = await DomainFixture.BuildFieldOpsAsync(client);

        Assert.True(fixture.ApplicationId > 0);
        Assert.True(fixture.EnvironmentId > 0);
        Assert.StartsWith("lgp_", fixture.ApiKey);
    }

    [Fact]
    public async Task SeedCustomersAsync_CreatesRequestedCount()
    {
        using var factory = new TestWebApplicationFactory();
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(factory);
        var fixture = await DomainFixture.BuildRetailPulseAsync(client);

        var customerIds = await DomainFixture.SeedCustomersAsync(client, fixture.ApplicationId, 15);

        Assert.Equal(15, customerIds.Count);
        Assert.Equal("cust-0", customerIds[0]);
        Assert.Equal("cust-14", customerIds[14]);
    }
}
