using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AppUsersControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AppUsersControllerTests(TestWebApplicationFactory factory)
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
    public async Task PostThenGet_CreatesAndReturnsAppUser()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId = await CreateApplicationAsync(client, "AppUserControllerTestApp1");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/users",
            new CreateAppUserRequest("user-1", "Jane Doe"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<AppUserResponse>();
        Assert.NotNull(created);
        Assert.Equal("user-1", created!.ExternalUserId);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/users/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateExternalUserId_Returns409Conflict()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId = await CreateApplicationAsync(client, "AppUserControllerTestApp2");
        var first = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/users",
            new CreateAppUserRequest("user-dup", "First"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/users",
            new CreateAppUserRequest("user-dup", "Second"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_AppUserBelongingToDifferentApplication_Returns404()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId1 = await CreateApplicationAsync(client, "AppUserIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "AppUserIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/users",
            new CreateAppUserRequest("user-1", "BelongsToApp1"));
        var created = await createResponse.Content.ReadFromJsonAsync<AppUserResponse>();

        var crossAppGet = await client.GetAsync($"/api/v1/admin/applications/{appId2}/users/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossAppGet.StatusCode);
    }

    [Fact]
    public async Task Rename_AppUserBelongingToDifferentApplication_Returns404()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId1 = await CreateApplicationAsync(client, "AppUserRenameIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "AppUserRenameIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/users",
            new CreateAppUserRequest("user-1", "BelongsToApp1"));
        var created = await createResponse.Content.ReadFromJsonAsync<AppUserResponse>();

        var crossAppRename = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId2}/users/{created!.Id}",
            new RenameAppUserRequest("Hijacked"));

        Assert.Equal(HttpStatusCode.NotFound, crossAppRename.StatusCode);
    }

    [Fact]
    public async Task Deactivate_AppUserBelongingToDifferentApplication_Returns404()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId1 = await CreateApplicationAsync(client, "AppUserDeactivateIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "AppUserDeactivateIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/users",
            new CreateAppUserRequest("user-1", "BelongsToApp1"));
        var created = await createResponse.Content.ReadFromJsonAsync<AppUserResponse>();

        var crossAppDeactivate = await client.DeleteAsync($"/api/v1/admin/applications/{appId2}/users/{created!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, crossAppDeactivate.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesDisplayName_LeavesExternalUserIdUnchanged()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId = await CreateApplicationAsync(client, "AppUserRenameControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/users",
            new CreateAppUserRequest("user-1", "OriginalName"));
        var created = await createResponse.Content.ReadFromJsonAsync<AppUserResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/users/{created!.Id}",
            new RenameAppUserRequest("RenamedUser"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<AppUserResponse>();
        Assert.Equal("RenamedUser", renamed!.DisplayName);
        Assert.Equal("user-1", renamed.ExternalUserId);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/users/{created.Id}");
        var reloaded = await getResponse.Content.ReadFromJsonAsync<AppUserResponse>();
        Assert.Equal("RenamedUser", reloaded!.DisplayName);
    }

    [Fact]
    public async Task Create_UnknownApplicationId_Returns404NotFound()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications/999999/users",
            new CreateAppUserRequest("user-1", "Jane Doe"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId = await CreateApplicationAsync(client, "AppUserDeactivateControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/users",
            new CreateAppUserRequest("user-1", "ToDeactivate"));
        var created = await createResponse.Content.ReadFromJsonAsync<AppUserResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/users/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<AppUserResponse>>($"/api/v1/admin/applications/{appId}/users");
        Assert.DoesNotContain(listResponse!, u => u.Id == created.Id);
    }
}
