using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ApiKeysControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ApiKeysControllerTests(TestWebApplicationFactory factory)
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
    public async Task PostThenGet_CreatesAndReturnsApiKey()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId = await CreateApplicationAsync(client, "ApiKeyControllerTestApp1");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/api-keys",
            new CreateApiKeyRequest("CI pipeline key"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();
        Assert.NotNull(created);
        Assert.Equal("CI pipeline key", created!.Label);
        Assert.StartsWith("lgp_", created.ApiKey);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/api-keys/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<ApiKeyResponse>();
        Assert.Equal("CI pipeline key", fetched!.Label);
        Assert.Null(fetched.RevokedAt);
    }

    [Fact]
    public async Task GetById_ResponseNeverContainsRawKeyOrHashField()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId = await CreateApplicationAsync(client, "ApiKeyNoLeakTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/api-keys",
            new CreateApiKeyRequest("LeakCheckKey"));
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/api-keys/{created!.Id}");
        var body = await getResponse.Content.ReadAsStringAsync();

        Assert.DoesNotContain(created.ApiKey, body);
        Assert.DoesNotContain("keyHash", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetById_ApiKeyBelongingToDifferentApplication_Returns404()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId1 = await CreateApplicationAsync(client, "ApiKeyIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "ApiKeyIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/api-keys",
            new CreateApiKeyRequest("BelongsToApp1"));
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var crossAppGet = await client.GetAsync($"/api/v1/admin/applications/{appId2}/api-keys/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossAppGet.StatusCode);
    }

    [Fact]
    public async Task Revoke_ApiKeyBelongingToDifferentApplication_Returns404()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId1 = await CreateApplicationAsync(client, "ApiKeyRevokeIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "ApiKeyRevokeIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/api-keys",
            new CreateApiKeyRequest("BelongsToApp1"));
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var crossAppRevoke = await client.DeleteAsync($"/api/v1/admin/applications/{appId2}/api-keys/{created!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, crossAppRevoke.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownApplicationId_Returns404NotFound()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications/999999/api-keys",
            new CreateApiKeyRequest("Orphan"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_SetsRevokedAt_ExcludedFromDefaultList()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId = await CreateApplicationAsync(client, "ApiKeyRevokeControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/api-keys",
            new CreateApiKeyRequest("ToRevoke"));
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var revokeResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/api-keys/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<ApiKeyResponse>>($"/api/v1/admin/applications/{appId}/api-keys");
        Assert.DoesNotContain(listResponse!, k => k.Id == created.Id);
    }

    [Fact]
    public async Task Revoke_CalledTwice_ReturnsNoContentBothTimes()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId = await CreateApplicationAsync(client, "ApiKeyDoubleRevokeControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/api-keys",
            new CreateApiKeyRequest("DoubleRevoke"));
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var firstRevoke = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/api-keys/{created!.Id}");
        var secondRevoke = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/api-keys/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, firstRevoke.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondRevoke.StatusCode);
    }

    [Fact]
    public async Task Revoke_CalledTwice_LeavesOriginalRevokedAtUnchanged()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId = await CreateApplicationAsync(client, "ApiKeyRevokeTimestampControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/api-keys",
            new CreateApiKeyRequest("TimestampCheck"));
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        await client.DeleteAsync($"/api/v1/admin/applications/{appId}/api-keys/{created!.Id}");
        var afterFirst = await client.GetFromJsonAsync<ApiKeyResponse>($"/api/v1/admin/applications/{appId}/api-keys/{created.Id}");

        await Task.Delay(50);
        await client.DeleteAsync($"/api/v1/admin/applications/{appId}/api-keys/{created.Id}");
        var afterSecond = await client.GetFromJsonAsync<ApiKeyResponse>($"/api/v1/admin/applications/{appId}/api-keys/{created.Id}");

        Assert.NotNull(afterFirst!.RevokedAt);
        Assert.Equal(afterFirst.RevokedAt, afterSecond!.RevokedAt);
    }

    [Fact]
    public async Task GetAll_ResponseNeverContainsRawKeyOrHashField()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appId = await CreateApplicationAsync(client, "ApiKeyListNoLeakTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/api-keys",
            new CreateApiKeyRequest("ListLeakCheckKey"));
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var listResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/api-keys");
        var body = await listResponse.Content.ReadAsStringAsync();

        Assert.DoesNotContain(created!.ApiKey, body);
        Assert.DoesNotContain("keyHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ListLeakCheckKey", body);
    }
}
