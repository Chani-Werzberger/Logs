using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AuditLogControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AuditLogControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Query_NoFilters_ReturnsRecentEntriesDescending()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var firstApp = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditQueryTestAppOne", null));
        var secondApp = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditQueryTestAppTwo", null));
        var second = await secondApp.Content.ReadFromJsonAsync<ApplicationResponse>();

        var response = await client.GetAsync("/api/v1/admin/audit-log?page=1&pageSize=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuditLogListResponse>();
        Assert.True(body!.TotalCount >= 2);
        var secondAppEntry = body.Items.First(e => e.EntityId == second!.Id.ToString() && e.EntityType == "Application");
        Assert.Equal("Create", secondAppEntry.Action);
    }

    [Fact]
    public async Task Query_FilterByEntityType_ReturnsOnlyMatching()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditQueryFilterTestApp", null));

        var response = await client.GetAsync("/api/v1/admin/audit-log?entityType=Application&page=1&pageSize=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuditLogListResponse>();
        Assert.All(body!.Items, e => Assert.Equal("Application", e.EntityType));
    }

    [Fact]
    public async Task Query_MissingCookie_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/audit-log");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Query_PageSizeOne_ReturnsSingleItemWithCorrectTotalCount()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditQueryPagingTestAppOne", null));
        await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditQueryPagingTestAppTwo", null));

        var response = await client.GetAsync("/api/v1/admin/audit-log?page=1&pageSize=1");

        var body = await response.Content.ReadFromJsonAsync<AuditLogListResponse>();
        Assert.Single(body!.Items);
        Assert.True(body.TotalCount >= 2);
    }
}
