using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AuditLogWiringGroupDTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AuditLogWiringGroupDTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, int ModuleId)> CreateAppAndModuleAsync(string appName)
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var moduleResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/modules", new CreateModuleRequest("Module", null));
        var module = await moduleResponse.Content.ReadFromJsonAsync<ModuleResponse>();
        return (client, module!.Id);
    }

    [Fact]
    public async Task ScreenServiceCreateThenRenameThenDeactivate_Success_RecordsThreeAuditEntries()
    {
        var (client, moduleId) = await CreateAppAndModuleAsync("AuditGroupDScreenServiceTestApp");

        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/modules/{moduleId}/screen-services", new CreateScreenServiceRequest("Checkout", "Screen", null));
        var screenService = await createResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();
        await client.PutAsJsonAsync($"/api/v1/admin/modules/{moduleId}/screen-services/{screenService!.Id}", new RenameScreenServiceRequest("Checkout Renamed", null));
        await client.DeleteAsync($"/api/v1/admin/modules/{moduleId}/screen-services/{screenService.Id}");

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var createEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "ScreenService" && e.EntityId == screenService.Id.ToString() && e.Action == "Create");
        var updateEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "ScreenService" && e.EntityId == screenService.Id.ToString() && e.Action == "Update");
        var deactivateEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "ScreenService" && e.EntityId == screenService.Id.ToString() && e.Action == "Deactivate");
        Assert.NotNull(createEntry);
        Assert.NotNull(updateEntry);
        Assert.NotNull(deactivateEntry);
    }

    [Fact]
    public async Task ProcessCreate_Success_RecordsAuditEntry()
    {
        var (client, moduleId) = await CreateAppAndModuleAsync("AuditGroupDProcessTestApp");
        var screenServiceResponse = await client.PostAsJsonAsync($"/api/v1/admin/modules/{moduleId}/screen-services", new CreateScreenServiceRequest("Checkout", "Screen", null));
        var screenService = await screenServiceResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();

        var response = await client.PostAsJsonAsync($"/api/v1/admin/screen-services/{screenService!.Id}/processes", new CreateProcessRequest("ChargeCard", null));
        var process = await response.Content.ReadFromJsonAsync<ProcessResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "ProcessNode" && e.EntityId == process!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }

    [Fact]
    public async Task OperationCreate_Success_RecordsAuditEntry()
    {
        var (client, moduleId) = await CreateAppAndModuleAsync("AuditGroupDOperationTestApp");
        var screenServiceResponse = await client.PostAsJsonAsync($"/api/v1/admin/modules/{moduleId}/screen-services", new CreateScreenServiceRequest("Checkout", "Screen", null));
        var screenService = await screenServiceResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();
        var processResponse = await client.PostAsJsonAsync($"/api/v1/admin/screen-services/{screenService!.Id}/processes", new CreateProcessRequest("ChargeCard", null));
        var process = await processResponse.Content.ReadFromJsonAsync<ProcessResponse>();

        var response = await client.PostAsJsonAsync($"/api/v1/admin/processes/{process!.Id}/operations", new CreateOperationRequest("Authorize", null));
        var operation = await response.Content.ReadFromJsonAsync<OperationResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "Operation" && e.EntityId == operation!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }
}
