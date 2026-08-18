using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ProcessesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ProcessesControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<int> CreateScreenServiceAsync(HttpClient client, string appName, string moduleName, string screenServiceName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var moduleResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{app!.Id}/modules",
            new CreateModuleRequest(moduleName, null));
        var module = await moduleResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var screenServiceResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/modules/{module!.Id}/screen-services",
            new CreateScreenServiceRequest(screenServiceName, "Service", null));
        var screenService = await screenServiceResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();
        return screenService!.Id;
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsProcess()
    {
        var client = _factory.CreateClient();
        var screenServiceId = await CreateScreenServiceAsync(client, "ProcessControllerTestApp1", "Payments", "PaymentGateway");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId}/processes",
            new CreateProcessRequest("ChargeCard", "Charges a customer's card"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ProcessResponse>();
        Assert.NotNull(created);
        Assert.Equal("ChargeCard", created!.Name);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var screenServiceId = await CreateScreenServiceAsync(client, "ProcessControllerTestApp2", "Payments", "PaymentGateway");
        var request = new CreateProcessRequest("DuplicateProcess", null);

        var first = await client.PostAsJsonAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes", request);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_ProcessBelongingToDifferentScreenService_Returns404()
    {
        var client = _factory.CreateClient();
        var screenServiceId1 = await CreateScreenServiceAsync(client, "ProcessIdorTestApp1", "ModuleA", "ScreenServiceA");
        var screenServiceId2 = await CreateScreenServiceAsync(client, "ProcessIdorTestApp2", "ModuleB", "ScreenServiceB");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId1}/processes",
            new CreateProcessRequest("BelongsToScreenService1", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ProcessResponse>();

        var crossParentGet = await client.GetAsync($"/api/v1/admin/screen-services/{screenServiceId2}/processes/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossParentGet.StatusCode);
    }

    [Fact]
    public async Task Rename_ProcessBelongingToDifferentScreenService_Returns404()
    {
        var client = _factory.CreateClient();
        var screenServiceId1 = await CreateScreenServiceAsync(client, "ProcessRenameIdorTestApp1", "ModuleA", "ScreenServiceA");
        var screenServiceId2 = await CreateScreenServiceAsync(client, "ProcessRenameIdorTestApp2", "ModuleB", "ScreenServiceB");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId1}/processes",
            new CreateProcessRequest("BelongsToScreenService1", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ProcessResponse>();

        var crossParentRename = await client.PutAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId2}/processes/{created!.Id}",
            new RenameProcessRequest("Hijacked", null));

        Assert.Equal(HttpStatusCode.NotFound, crossParentRename.StatusCode);
    }

    [Fact]
    public async Task Deactivate_ProcessBelongingToDifferentScreenService_Returns404()
    {
        var client = _factory.CreateClient();
        var screenServiceId1 = await CreateScreenServiceAsync(client, "ProcessDeactivateIdorTestApp1", "ModuleA", "ScreenServiceA");
        var screenServiceId2 = await CreateScreenServiceAsync(client, "ProcessDeactivateIdorTestApp2", "ModuleB", "ScreenServiceB");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId1}/processes",
            new CreateProcessRequest("BelongsToScreenService1", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ProcessResponse>();

        var crossParentDeactivate = await client.DeleteAsync($"/api/v1/admin/screen-services/{screenServiceId2}/processes/{created!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, crossParentDeactivate.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesNameAndDescription()
    {
        var client = _factory.CreateClient();
        var screenServiceId = await CreateScreenServiceAsync(client, "ProcessRenameControllerTestApp", "Payments", "PaymentGateway");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId}/processes",
            new CreateProcessRequest("OriginalName", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ProcessResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId}/processes/{created!.Id}",
            new RenameProcessRequest("RenamedProcess", "updated"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<ProcessResponse>();
        Assert.Equal("RenamedProcess", renamed!.Name);

        var getResponse = await client.GetAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes/{created.Id}");
        var reloaded = await getResponse.Content.ReadFromJsonAsync<ProcessResponse>();
        Assert.Equal("RenamedProcess", reloaded!.Name);
    }

    [Fact]
    public async Task Rename_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var screenServiceId = await CreateScreenServiceAsync(client, "ProcessRenameConflictControllerTestApp", "Payments", "PaymentGateway");
        await client.PostAsJsonAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes", new CreateProcessRequest("Taken", null));
        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes", new CreateProcessRequest("ToRename", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ProcessResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId}/processes/{created!.Id}",
            new RenameProcessRequest("Taken", null));

        Assert.Equal(HttpStatusCode.Conflict, renameResponse.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownScreenServiceId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/screen-services/999999/processes",
            new CreateProcessRequest("ChargeCard", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var screenServiceId = await CreateScreenServiceAsync(client, "ProcessDeactivateControllerTestApp", "Payments", "PaymentGateway");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId}/processes",
            new CreateProcessRequest("ToDeactivate", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ProcessResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<ProcessResponse>>($"/api/v1/admin/screen-services/{screenServiceId}/processes");
        Assert.DoesNotContain(listResponse!, p => p.Id == created.Id);
    }
}
