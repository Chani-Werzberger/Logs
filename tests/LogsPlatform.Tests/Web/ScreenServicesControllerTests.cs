using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ScreenServicesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ScreenServicesControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<int> CreateModuleAsync(HttpClient client, string appName, string moduleName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var moduleResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{app!.Id}/modules",
            new CreateModuleRequest(moduleName, null));
        var module = await moduleResponse.Content.ReadFromJsonAsync<ModuleResponse>();
        return module!.Id;
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsScreenService()
    {
        var client = _factory.CreateClient();
        var moduleId = await CreateModuleAsync(client, "ScreenServiceControllerTestApp1", "Payments");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/modules/{moduleId}/screen-services",
            new CreateScreenServiceRequest("PaymentGateway", "Service", "Handles payment calls"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();
        Assert.NotNull(created);
        Assert.Equal("PaymentGateway", created!.Name);
        Assert.Equal("Service", created.Type);

        var getResponse = await client.GetAsync($"/api/v1/admin/modules/{moduleId}/screen-services/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var moduleId = await CreateModuleAsync(client, "ScreenServiceControllerTestApp2", "Payments");
        var request = new CreateScreenServiceRequest("DuplicateService", "Screen", null);

        var first = await client.PostAsJsonAsync($"/api/v1/admin/modules/{moduleId}/screen-services", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/v1/admin/modules/{moduleId}/screen-services", request);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_ScreenServiceBelongingToDifferentModule_Returns404()
    {
        var client = _factory.CreateClient();
        var moduleId1 = await CreateModuleAsync(client, "ScreenServiceIdorTestApp1", "ModuleA");
        var moduleId2 = await CreateModuleAsync(client, "ScreenServiceIdorTestApp2", "ModuleB");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/modules/{moduleId1}/screen-services",
            new CreateScreenServiceRequest("BelongsToModule1", "Screen", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();

        var crossModuleGet = await client.GetAsync($"/api/v1/admin/modules/{moduleId2}/screen-services/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossModuleGet.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesNameAndDescription()
    {
        var client = _factory.CreateClient();
        var moduleId = await CreateModuleAsync(client, "ScreenServiceRenameControllerTestApp", "Payments");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/modules/{moduleId}/screen-services",
            new CreateScreenServiceRequest("OriginalName", "Screen", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/modules/{moduleId}/screen-services/{created!.Id}",
            new RenameScreenServiceRequest("RenamedService", "updated"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();
        Assert.Equal("RenamedService", renamed!.Name);

        var getResponse = await client.GetAsync($"/api/v1/admin/modules/{moduleId}/screen-services/{created.Id}");
        var reloaded = await getResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();
        Assert.Equal("RenamedService", reloaded!.Name);
    }

    [Fact]
    public async Task Rename_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var moduleId = await CreateModuleAsync(client, "ScreenServiceRenameConflictControllerTestApp", "Payments");
        await client.PostAsJsonAsync(
            $"/api/v1/admin/modules/{moduleId}/screen-services",
            new CreateScreenServiceRequest("Taken", "Screen", null));
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/modules/{moduleId}/screen-services",
            new CreateScreenServiceRequest("ToRename", "Screen", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/modules/{moduleId}/screen-services/{created!.Id}",
            new RenameScreenServiceRequest("Taken", null));

        Assert.Equal(HttpStatusCode.Conflict, renameResponse.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownModuleId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/modules/999999/screen-services",
            new CreateScreenServiceRequest("PaymentGateway", "Service", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var moduleId = await CreateModuleAsync(client, "ScreenServiceDeactivateControllerTestApp", "Payments");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/modules/{moduleId}/screen-services",
            new CreateScreenServiceRequest("ToDeactivate", "Screen", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/modules/{moduleId}/screen-services/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<ScreenServiceResponse>>($"/api/v1/admin/modules/{moduleId}/screen-services");
        Assert.DoesNotContain(listResponse!, s => s.Id == created.Id);
    }
}
