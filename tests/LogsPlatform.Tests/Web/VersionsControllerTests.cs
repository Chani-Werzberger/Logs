// tests/LogsPlatform.Tests/Web/VersionsControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class VersionsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public VersionsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<int> CreateApplicationAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(name, null));
        var created = await response.Content.ReadFromJsonAsync<ApplicationResponse>();
        return created!.Id;
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsVersion()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId = await CreateApplicationAsync(client, "VersionControllerTestApp1");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/versions",
            new CreateVersionRequest("1.0.0", "Initial release"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<VersionResponse>();
        Assert.NotNull(created);
        Assert.Equal("1.0.0", created!.VersionNumber);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/versions/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateVersionNumber_Returns409Conflict()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId = await CreateApplicationAsync(client, "VersionControllerTestApp2");
        var request = new CreateVersionRequest("1.0.0-dup", null);

        var first = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/versions", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/versions",
            new CreateVersionRequest("1.0.0-dup", null));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_VersionBelongingToDifferentApplication_Returns404()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId1 = await CreateApplicationAsync(client, "VersionIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "VersionIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/versions",
            new CreateVersionRequest("1.0.0", null));
        var created = await createResponse.Content.ReadFromJsonAsync<VersionResponse>();

        var crossAppGet = await client.GetAsync($"/api/v1/admin/applications/{appId2}/versions/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossAppGet.StatusCode);
    }

    [Fact]
    public async Task Rename_VersionBelongingToDifferentApplication_Returns404()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId1 = await CreateApplicationAsync(client, "VersionRenameIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "VersionRenameIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/versions",
            new CreateVersionRequest("1.0.0", null));
        var created = await createResponse.Content.ReadFromJsonAsync<VersionResponse>();

        var crossAppRename = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId2}/versions/{created!.Id}",
            new RenameVersionRequest("Hijacked"));

        Assert.Equal(HttpStatusCode.NotFound, crossAppRename.StatusCode);
    }

    [Fact]
    public async Task Deactivate_VersionBelongingToDifferentApplication_Returns404()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId1 = await CreateApplicationAsync(client, "VersionDeactivateIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "VersionDeactivateIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/versions",
            new CreateVersionRequest("1.0.0", null));
        var created = await createResponse.Content.ReadFromJsonAsync<VersionResponse>();

        var crossAppDeactivate = await client.DeleteAsync($"/api/v1/admin/applications/{appId2}/versions/{created!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, crossAppDeactivate.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesReleaseNotes_LeavesVersionNumberUnchanged()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId = await CreateApplicationAsync(client, "VersionRenameControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/versions",
            new CreateVersionRequest("1.0.0", "OldNotes"));
        var created = await createResponse.Content.ReadFromJsonAsync<VersionResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/versions/{created!.Id}",
            new RenameVersionRequest("NewNotes"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<VersionResponse>();
        Assert.Equal("NewNotes", renamed!.ReleaseNotes);
        Assert.Equal("1.0.0", renamed.VersionNumber);
    }

    [Fact]
    public async Task Create_UnknownApplicationId_Returns404NotFound()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications/999999/versions",
            new CreateVersionRequest("1.0.0", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId = await CreateApplicationAsync(client, "VersionDeactivateControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/versions",
            new CreateVersionRequest("1.0.0", null));
        var created = await createResponse.Content.ReadFromJsonAsync<VersionResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/versions/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<VersionResponse>>($"/api/v1/admin/applications/{appId}/versions");
        Assert.DoesNotContain(listResponse!, v => v.Id == created.Id);
    }
}
