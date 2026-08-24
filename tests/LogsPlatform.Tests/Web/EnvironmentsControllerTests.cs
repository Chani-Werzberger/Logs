using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class EnvironmentsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public EnvironmentsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetAll_ReturnsOnlyEnvironmentsScopedToRequestedApplication()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var firstAppResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/applications",
            new CreateApplicationRequest("RetailPulse", "E-commerce simulation app"));
        Assert.Equal(HttpStatusCode.Created, firstAppResponse.StatusCode);
        var firstApp = await firstAppResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.NotNull(firstApp);

        var secondAppResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/applications",
            new CreateApplicationRequest("FieldOps", "Field-service scheduling simulation app"));
        Assert.Equal(HttpStatusCode.Created, secondAppResponse.StatusCode);
        var secondApp = await secondAppResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.NotNull(secondApp);

        var firstEnvResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{firstApp!.Id}/environments",
            new CreateEnvironmentRequest("Production", true));
        Assert.Equal(HttpStatusCode.Created, firstEnvResponse.StatusCode);
        var firstEnv = await firstEnvResponse.Content.ReadFromJsonAsync<EnvironmentResponse>();
        Assert.NotNull(firstEnv);

        var secondEnvResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{secondApp!.Id}/environments",
            new CreateEnvironmentRequest("Staging", false));
        Assert.Equal(HttpStatusCode.Created, secondEnvResponse.StatusCode);
        var secondEnv = await secondEnvResponse.Content.ReadFromJsonAsync<EnvironmentResponse>();
        Assert.NotNull(secondEnv);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{firstApp.Id}/environments");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var environments = await getResponse.Content.ReadFromJsonAsync<List<EnvironmentResponse>>();

        Assert.NotNull(environments);
        var environment = Assert.Single(environments);
        Assert.Equal(firstEnv!.Id, environment.Id);
        Assert.Equal(firstApp.Id, environment.ApplicationId);
        Assert.Equal("Production", environment.Name);
        Assert.DoesNotContain(environments, e => e.Id == secondEnv!.Id);
        Assert.DoesNotContain(environments, e => e.ApplicationId == secondApp.Id);
    }

    [Fact]
    public async Task Create_UnknownApplicationId_Returns404NotFound()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications/999999/environments",
            new CreateEnvironmentRequest("Production", true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
