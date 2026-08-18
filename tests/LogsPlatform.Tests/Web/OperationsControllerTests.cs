using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class OperationsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public OperationsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<int> CreateProcessAsync(HttpClient client, string appName, string moduleName, string screenServiceName, string processName)
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

        var processResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenService!.Id}/processes",
            new CreateProcessRequest(processName, null));
        var process = await processResponse.Content.ReadFromJsonAsync<ProcessResponse>();
        return process!.Id;
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsOperation()
    {
        var client = _factory.CreateClient();
        var processId = await CreateProcessAsync(client, "OperationControllerTestApp1", "Payments", "PaymentGateway", "ChargeCard");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/processes/{processId}/operations",
            new CreateOperationRequest("AuthorizePayment", "Authorizes the payment with the card network"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<OperationResponse>();
        Assert.NotNull(created);
        Assert.Equal("AuthorizePayment", created!.Name);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/processes/{processId}/operations/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var processId = await CreateProcessAsync(client, "OperationControllerTestApp2", "Payments", "PaymentGateway", "ChargeCard");
        var request = new CreateOperationRequest("DuplicateOperation", null);

        var first = await client.PostAsJsonAsync($"/api/v1/admin/processes/{processId}/operations", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/v1/admin/processes/{processId}/operations", request);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_OperationBelongingToDifferentProcess_Returns404()
    {
        var client = _factory.CreateClient();
        var processId1 = await CreateProcessAsync(client, "OperationIdorTestApp1", "ModuleA", "ScreenServiceA", "ProcessA");
        var processId2 = await CreateProcessAsync(client, "OperationIdorTestApp2", "ModuleB", "ScreenServiceB", "ProcessB");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/processes/{processId1}/operations",
            new CreateOperationRequest("BelongsToProcess1", null));
        var created = await createResponse.Content.ReadFromJsonAsync<OperationResponse>();

        var crossParentGet = await client.GetAsync($"/api/v1/admin/processes/{processId2}/operations/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossParentGet.StatusCode);
    }

    [Fact]
    public async Task Rename_OperationBelongingToDifferentProcess_Returns404()
    {
        var client = _factory.CreateClient();
        var processId1 = await CreateProcessAsync(client, "OperationRenameIdorTestApp1", "ModuleA", "ScreenServiceA", "ProcessA");
        var processId2 = await CreateProcessAsync(client, "OperationRenameIdorTestApp2", "ModuleB", "ScreenServiceB", "ProcessB");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/processes/{processId1}/operations",
            new CreateOperationRequest("BelongsToProcess1", null));
        var created = await createResponse.Content.ReadFromJsonAsync<OperationResponse>();

        var crossParentRename = await client.PutAsJsonAsync(
            $"/api/v1/admin/processes/{processId2}/operations/{created!.Id}",
            new RenameOperationRequest("Hijacked", null));

        Assert.Equal(HttpStatusCode.NotFound, crossParentRename.StatusCode);
    }

    [Fact]
    public async Task Deactivate_OperationBelongingToDifferentProcess_Returns404()
    {
        var client = _factory.CreateClient();
        var processId1 = await CreateProcessAsync(client, "OperationDeactivateIdorTestApp1", "ModuleA", "ScreenServiceA", "ProcessA");
        var processId2 = await CreateProcessAsync(client, "OperationDeactivateIdorTestApp2", "ModuleB", "ScreenServiceB", "ProcessB");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/processes/{processId1}/operations",
            new CreateOperationRequest("BelongsToProcess1", null));
        var created = await createResponse.Content.ReadFromJsonAsync<OperationResponse>();

        var crossParentDeactivate = await client.DeleteAsync($"/api/v1/admin/processes/{processId2}/operations/{created!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, crossParentDeactivate.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesNameAndDescription()
    {
        var client = _factory.CreateClient();
        var processId = await CreateProcessAsync(client, "OperationRenameControllerTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/processes/{processId}/operations",
            new CreateOperationRequest("OriginalName", null));
        var created = await createResponse.Content.ReadFromJsonAsync<OperationResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/processes/{processId}/operations/{created!.Id}",
            new RenameOperationRequest("RenamedOperation", "updated"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<OperationResponse>();
        Assert.Equal("RenamedOperation", renamed!.Name);

        var getResponse = await client.GetAsync($"/api/v1/admin/processes/{processId}/operations/{created.Id}");
        var reloaded = await getResponse.Content.ReadFromJsonAsync<OperationResponse>();
        Assert.Equal("RenamedOperation", reloaded!.Name);
    }

    [Fact]
    public async Task Rename_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var processId = await CreateProcessAsync(client, "OperationRenameConflictControllerTestApp", "Payments", "PaymentGateway", "ChargeCard");
        await client.PostAsJsonAsync($"/api/v1/admin/processes/{processId}/operations", new CreateOperationRequest("Taken", null));
        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/processes/{processId}/operations", new CreateOperationRequest("ToRename", null));
        var created = await createResponse.Content.ReadFromJsonAsync<OperationResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/processes/{processId}/operations/{created!.Id}",
            new RenameOperationRequest("Taken", null));

        Assert.Equal(HttpStatusCode.Conflict, renameResponse.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownProcessId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/processes/999999/operations",
            new CreateOperationRequest("AuthorizePayment", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var processId = await CreateProcessAsync(client, "OperationDeactivateControllerTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/processes/{processId}/operations",
            new CreateOperationRequest("ToDeactivate", null));
        var created = await createResponse.Content.ReadFromJsonAsync<OperationResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/processes/{processId}/operations/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<OperationResponse>>($"/api/v1/admin/processes/{processId}/operations");
        Assert.DoesNotContain(listResponse!, o => o.Id == created.Id);
    }
}
