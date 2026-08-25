using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AuditLogWiringGroupBTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AuditLogWiringGroupBTests(TestWebApplicationFactory factory)
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
    public async Task CustomerCreate_Success_RecordsAuditEntry()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupBCustomerTestApp");

        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/customers", new CreateCustomerRequest("cust-1", "Acme"));
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "Customer" && e.EntityId == customer!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }

    [Fact]
    public async Task CustomerRenameThenDeactivate_Success_RecordsBothAuditEntries()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupBCustomerRenameTestApp");
        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/customers", new CreateCustomerRequest("cust-2", "Old Name"));
        var customer = await createResponse.Content.ReadFromJsonAsync<CustomerResponse>();

        await client.PutAsJsonAsync($"/api/v1/admin/applications/{appId}/customers/{customer!.Id}", new RenameCustomerRequest("New Name"));
        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/customers/{customer.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var updateEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "Customer" && e.EntityId == customer.Id.ToString() && e.Action == "Update");
        var deactivateEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "Customer" && e.EntityId == customer.Id.ToString() && e.Action == "Deactivate");
        Assert.NotNull(updateEntry);
        Assert.NotNull(deactivateEntry);
    }

    [Fact]
    public async Task CustomerCreate_DuplicateExternalId_DoesNotRecordAuditEntry()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupBCustomerDuplicateTestApp");
        await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/customers", new CreateCustomerRequest("cust-dup", "First"));

        var second = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/customers", new CreateCustomerRequest("cust-dup", "Second"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var count = await context.AdminAuditLogEntries.CountAsync(e => e.EntityType == "Customer" && e.Description.Contains("cust-dup"));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AppUserCreate_Success_RecordsAuditEntry()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupBAppUserTestApp");

        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/users", new CreateAppUserRequest("user-1", "Jane Doe"));
        var appUser = await response.Content.ReadFromJsonAsync<AppUserResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "AppUser" && e.EntityId == appUser!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }

    [Fact]
    public async Task LogSourceCreate_Success_RecordsAuditEntry()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupBLogSourceTestApp");

        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/log-sources", new CreateLogSourceRequest("Backend", "The backend service"));
        var logSource = await response.Content.ReadFromJsonAsync<LogSourceResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "LogSource" && e.EntityId == logSource!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }
}
