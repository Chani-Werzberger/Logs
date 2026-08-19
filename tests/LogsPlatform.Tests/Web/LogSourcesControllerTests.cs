using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class LogSourcesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public LogSourcesControllerTests(TestWebApplicationFactory factory)
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
    public async Task PostThenGet_CreatesAndReturnsLogSource()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "LogSourceControllerTestApp1");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/log-sources",
            new CreateLogSourceRequest("PaymentServiceLogs", "Structured logs"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<LogSourceResponse>();
        Assert.NotNull(created);
        Assert.Equal("PaymentServiceLogs", created!.Name);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/log-sources/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "LogSourceControllerTestApp2");
        var request = new CreateLogSourceRequest("DuplicateSource", null);

        var first = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/log-sources", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/log-sources", request);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_LogSourceBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "LogSourceIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "LogSourceIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/log-sources",
            new CreateLogSourceRequest("BelongsToApp1", null));
        var created = await createResponse.Content.ReadFromJsonAsync<LogSourceResponse>();

        var crossAppGet = await client.GetAsync($"/api/v1/admin/applications/{appId2}/log-sources/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossAppGet.StatusCode);
    }

    [Fact]
    public async Task Rename_LogSourceBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "LogSourceRenameIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "LogSourceRenameIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/log-sources",
            new CreateLogSourceRequest("BelongsToApp1", null));
        var created = await createResponse.Content.ReadFromJsonAsync<LogSourceResponse>();

        var crossAppRename = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId2}/log-sources/{created!.Id}",
            new RenameLogSourceRequest("Hijacked", null));

        Assert.Equal(HttpStatusCode.NotFound, crossAppRename.StatusCode);
    }

    [Fact]
    public async Task Deactivate_LogSourceBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "LogSourceDeactivateIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "LogSourceDeactivateIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/log-sources",
            new CreateLogSourceRequest("BelongsToApp1", null));
        var created = await createResponse.Content.ReadFromJsonAsync<LogSourceResponse>();

        var crossAppDeactivate = await client.DeleteAsync($"/api/v1/admin/applications/{appId2}/log-sources/{created!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, crossAppDeactivate.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesNameAndDescription()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "LogSourceRenameControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/log-sources",
            new CreateLogSourceRequest("OriginalName", null));
        var created = await createResponse.Content.ReadFromJsonAsync<LogSourceResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/log-sources/{created!.Id}",
            new RenameLogSourceRequest("RenamedSource", "updated"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<LogSourceResponse>();
        Assert.Equal("RenamedSource", renamed!.Name);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/log-sources/{created.Id}");
        var reloaded = await getResponse.Content.ReadFromJsonAsync<LogSourceResponse>();
        Assert.Equal("RenamedSource", reloaded!.Name);
    }

    [Fact]
    public async Task Rename_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "LogSourceRenameConflictControllerTestApp");
        await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/log-sources", new CreateLogSourceRequest("Taken", null));
        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/log-sources", new CreateLogSourceRequest("ToRename", null));
        var created = await createResponse.Content.ReadFromJsonAsync<LogSourceResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/log-sources/{created!.Id}",
            new RenameLogSourceRequest("Taken", null));

        Assert.Equal(HttpStatusCode.Conflict, renameResponse.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownApplicationId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications/999999/log-sources",
            new CreateLogSourceRequest("PaymentServiceLogs", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "LogSourceDeactivateControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/log-sources",
            new CreateLogSourceRequest("ToDeactivate", null));
        var created = await createResponse.Content.ReadFromJsonAsync<LogSourceResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/log-sources/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<LogSourceResponse>>($"/api/v1/admin/applications/{appId}/log-sources");
        Assert.DoesNotContain(listResponse!, l => l.Id == created.Id);
    }
}
