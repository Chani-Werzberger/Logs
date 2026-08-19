using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class DeploymentsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public DeploymentsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<int> CreateApplicationAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(name, null));
        var created = await response.Content.ReadFromJsonAsync<ApplicationResponse>();
        return created!.Id;
    }

    private static async Task<(int EnvironmentId, int VersionId)> CreateFixtureAsync(HttpClient client, int appId)
    {
        var envResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/environments",
            new CreateEnvironmentRequest("Production", true));
        var env = await envResponse.Content.ReadFromJsonAsync<EnvironmentResponse>();

        var versionResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/versions",
            new CreateVersionRequest("1.0.0", null));
        var version = await versionResponse.Content.ReadFromJsonAsync<VersionResponse>();

        return (env!.Id, version!.Id);
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsDeployment()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "DeploymentControllerTestApp1");
        var (envId, versionId) = await CreateFixtureAsync(client, appId);
        var deployedAt = DateTime.UtcNow;

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/deployments",
            new CreateDeploymentRequest(envId, versionId, deployedAt, "First deploy"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<DeploymentResponse>();
        Assert.NotNull(created);
        Assert.Equal(envId, created!.EnvironmentId);
        Assert.Equal(versionId, created.VersionId);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/deployments/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_EnvironmentBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "DeploymentEnvIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "DeploymentEnvIdorTestApp2");
        var (envIdFromApp1, _) = await CreateFixtureAsync(client, appId1);
        var (_, versionIdFromApp2) = await CreateFixtureAsync(client, appId2);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId2}/deployments",
            new CreateDeploymentRequest(envIdFromApp1, versionIdFromApp2, DateTime.UtcNow, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_VersionBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "DeploymentVersionIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "DeploymentVersionIdorTestApp2");
        var (_, versionIdFromApp1) = await CreateFixtureAsync(client, appId1);
        var (envIdFromApp2, _) = await CreateFixtureAsync(client, appId2);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId2}/deployments",
            new CreateDeploymentRequest(envIdFromApp2, versionIdFromApp1, DateTime.UtcNow, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownApplicationId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications/999999/deployments",
            new CreateDeploymentRequest(1, 1, DateTime.UtcNow, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetById_DeploymentBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "DeploymentGetIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "DeploymentGetIdorTestApp2");
        var (envId, versionId) = await CreateFixtureAsync(client, appId1);
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/deployments",
            new CreateDeploymentRequest(envId, versionId, DateTime.UtcNow, null));
        var created = await createResponse.Content.ReadFromJsonAsync<DeploymentResponse>();

        var crossAppGet = await client.GetAsync($"/api/v1/admin/applications/{appId2}/deployments/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossAppGet.StatusCode);
    }

    [Fact]
    public async Task Rename_DeploymentBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "DeploymentRenameIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "DeploymentRenameIdorTestApp2");
        var (envId, versionId) = await CreateFixtureAsync(client, appId1);
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/deployments",
            new CreateDeploymentRequest(envId, versionId, DateTime.UtcNow, null));
        var created = await createResponse.Content.ReadFromJsonAsync<DeploymentResponse>();

        var crossAppRename = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId2}/deployments/{created!.Id}",
            new RenameDeploymentRequest("Hijacked"));

        Assert.Equal(HttpStatusCode.NotFound, crossAppRename.StatusCode);
    }

    [Fact]
    public async Task Deactivate_DeploymentBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "DeploymentDeactivateIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "DeploymentDeactivateIdorTestApp2");
        var (envId, versionId) = await CreateFixtureAsync(client, appId1);
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/deployments",
            new CreateDeploymentRequest(envId, versionId, DateTime.UtcNow, null));
        var created = await createResponse.Content.ReadFromJsonAsync<DeploymentResponse>();

        var crossAppDeactivate = await client.DeleteAsync($"/api/v1/admin/applications/{appId2}/deployments/{created!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, crossAppDeactivate.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesNotes()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "DeploymentRenameControllerTestApp");
        var (envId, versionId) = await CreateFixtureAsync(client, appId);
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/deployments",
            new CreateDeploymentRequest(envId, versionId, DateTime.UtcNow, "OldNotes"));
        var created = await createResponse.Content.ReadFromJsonAsync<DeploymentResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/deployments/{created!.Id}",
            new RenameDeploymentRequest("NewNotes"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<DeploymentResponse>();
        Assert.Equal("NewNotes", renamed!.Notes);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "DeploymentDeactivateControllerTestApp");
        var (envId, versionId) = await CreateFixtureAsync(client, appId);
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/deployments",
            new CreateDeploymentRequest(envId, versionId, DateTime.UtcNow, null));
        var created = await createResponse.Content.ReadFromJsonAsync<DeploymentResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/deployments/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<DeploymentResponse>>($"/api/v1/admin/applications/{appId}/deployments");
        Assert.DoesNotContain(listResponse!, d => d.Id == created.Id);
    }

    [Fact]
    public async Task Create_SameEnvironmentAndVersionTwice_BothSucceed()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "DeploymentRedeployControllerTestApp");
        var (envId, versionId) = await CreateFixtureAsync(client, appId);

        var first = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/deployments",
            new CreateDeploymentRequest(envId, versionId, DateTime.UtcNow, "First"));
        var second = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/deployments",
            new CreateDeploymentRequest(envId, versionId, DateTime.UtcNow, "Redeploy"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }
}
