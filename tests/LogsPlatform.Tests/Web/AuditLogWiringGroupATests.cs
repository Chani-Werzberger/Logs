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
public class AuditLogWiringGroupATests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AuditLogWiringGroupATests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ApplicationCreate_Success_RecordsAuditEntry()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var response = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditGroupATestApp", null));
        var app = await response.Content.ReadFromJsonAsync<ApplicationResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries
            .SingleAsync(e => e.EntityType == "Application" && e.EntityId == app!.Id.ToString());
        Assert.Equal("Create", entry.Action);
        Assert.Contains("AuditGroupATestApp", entry.Description);
    }

    [Fact]
    public async Task ApplicationCreate_DuplicateName_DoesNotRecordAuditEntry()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditGroupADuplicateTestApp", null));

        var second = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditGroupADuplicateTestApp", null));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var count = await context.AdminAuditLogEntries.CountAsync(e => e.EntityType == "Application" && e.Description.Contains("AuditGroupADuplicateTestApp"));
        Assert.Equal(1, count); // only the first, successful Create — not the failed duplicate
    }

    [Fact]
    public async Task EnvironmentCreate_Success_RecordsAuditEntry()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditGroupAEnvTestApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));
        var env = await response.Content.ReadFromJsonAsync<EnvironmentResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries
            .SingleAsync(e => e.EntityType == "AppEnvironment" && e.EntityId == env!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }

    [Fact]
    public async Task ApiKeyCreateThenRevoke_Success_RecordsBothAuditEntries()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditGroupAApiKeyTestApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/api-keys", new CreateApiKeyRequest("Audit test key"));
        var key = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var revokeResponse = await client.DeleteAsync($"/api/v1/admin/applications/{app.Id}/api-keys/{key!.Id}");

        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var createEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "ApiKey" && e.EntityId == key.Id.ToString() && e.Action == "Create");
        var revokeEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "ApiKey" && e.EntityId == key.Id.ToString() && e.Action == "Revoke");
        Assert.NotNull(createEntry);
        Assert.NotNull(revokeEntry);
    }
}
