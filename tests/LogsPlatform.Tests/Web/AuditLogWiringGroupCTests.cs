using System.Net.Http.Json;
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AuditLogWiringGroupCTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AuditLogWiringGroupCTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, int AppId)> CreateAppAsync(string appName)
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        return (client, app!.Id);
    }

    [Fact]
    public async Task VersionCreateThenRenameThenDeactivate_Success_RecordsThreeAuditEntries()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupCVersionTestApp");

        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/versions", new CreateVersionRequest("1.0.0", "Initial release"));
        var version = await createResponse.Content.ReadFromJsonAsync<VersionResponse>();
        await client.PutAsJsonAsync($"/api/v1/admin/applications/{appId}/versions/{version!.Id}", new RenameVersionRequest("Updated notes"));
        await client.DeleteAsync($"/api/v1/admin/applications/{appId}/versions/{version.Id}");

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var createEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "AppVersion" && e.EntityId == version.Id.ToString() && e.Action == "Create");
        var updateEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "AppVersion" && e.EntityId == version.Id.ToString() && e.Action == "Update");
        var deactivateEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "AppVersion" && e.EntityId == version.Id.ToString() && e.Action == "Deactivate");
        Assert.NotNull(createEntry);
        Assert.NotNull(updateEntry);
        Assert.NotNull(deactivateEntry);
    }

    [Fact]
    public async Task DeploymentCreate_Success_RecordsAuditEntry()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupCDeploymentTestApp");
        var envResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/environments", new CreateEnvironmentRequest("Production", true));
        var env = await envResponse.Content.ReadFromJsonAsync<EnvironmentResponse>();
        var versionResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/versions", new CreateVersionRequest("1.0.0", null));
        var version = await versionResponse.Content.ReadFromJsonAsync<VersionResponse>();

        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/deployments", new CreateDeploymentRequest(env!.Id, version!.Id, DateTime.UtcNow, "First deploy"));
        var deployment = await response.Content.ReadFromJsonAsync<DeploymentResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "Deployment" && e.EntityId == deployment!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }

    [Fact]
    public async Task ModuleCreate_Success_RecordsAuditEntry()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupCModuleTestApp");

        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/modules", new CreateModuleRequest("Payments", "Payment processing"));
        var module = await response.Content.ReadFromJsonAsync<ModuleResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "AppModule" && e.EntityId == module!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }
}
