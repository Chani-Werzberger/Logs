using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ModulesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ModulesControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<int> CreateApplicationAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications",
            new CreateApplicationRequest(name, null));
        var created = await response.Content.ReadFromJsonAsync<ApplicationResponse>();
        return created!.Id;
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsModule()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "ModuleControllerTestApp1");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/modules",
            new CreateModuleRequest("Payments", "Payment handling"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ModuleResponse>();
        Assert.NotNull(created);
        Assert.Equal("Payments", created!.Name);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/modules/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "ModuleControllerTestApp2");
        var request = new CreateModuleRequest("DuplicateModule", null);

        var first = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/modules", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/modules", request);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_ModuleBelongingToDifferentApplication_Returns404()
    {
        // IDOR guard: a valid module ID under the WRONG appId in the route must 404, not leak data.
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "ModuleIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "ModuleIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/modules",
            new CreateModuleRequest("BelongsToApp1", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var crossAppGet = await client.GetAsync($"/api/v1/admin/applications/{appId2}/modules/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossAppGet.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesNameAndDescription()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "ModuleRenameControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/modules",
            new CreateModuleRequest("OriginalName", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/modules/{created!.Id}",
            new RenameModuleRequest("RenamedModule", "updated"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<ModuleResponse>();
        Assert.Equal("RenamedModule", renamed!.Name);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/modules/{created.Id}");
        var reloaded = await getResponse.Content.ReadFromJsonAsync<ModuleResponse>();
        Assert.Equal("RenamedModule", reloaded!.Name);
    }

    [Fact]
    public async Task Rename_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "ModuleRenameConflictControllerTestApp");
        await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/modules", new CreateModuleRequest("Taken", null));
        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/modules", new CreateModuleRequest("ToRename", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/modules/{created!.Id}",
            new RenameModuleRequest("Taken", null));

        Assert.Equal(HttpStatusCode.Conflict, renameResponse.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownApplicationId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications/999999/modules",
            new CreateModuleRequest("Payments", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "ModuleDeactivateControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/modules",
            new CreateModuleRequest("ToDeactivate", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/modules/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<ModuleResponse>>($"/api/v1/admin/applications/{appId}/modules");
        Assert.DoesNotContain(listResponse!, m => m.Id == created.Id);

        var listWithInactive = await client.GetFromJsonAsync<List<ModuleResponse>>(
            $"/api/v1/admin/applications/{appId}/modules?includeInactive=true");
        Assert.Contains(listWithInactive!, m => m.Id == created.Id);
    }
}
