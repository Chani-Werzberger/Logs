using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ExceptionGroupsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ExceptionGroupsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<(int ApplicationId, string ApiKey)> CreateAppWithApiKeyAsync(HttpClient client, string appName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));
        var keyResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("ExceptionGroups test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();
        return (app.Id, key!.ApiKey);
    }

    [Fact]
    public async Task GetExceptionGroups_ReturnsGroupWithDailyCounts()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var (appId, apiKey) = await CreateAppWithApiKeyAsync(client, "ExGroupApiTestApp");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/events")
        {
            Content = JsonContent.Create(new List<IngestEventRequest> { new(
                EventKey: null, Timestamp: DateTime.UtcNow, Severity: "Error", Environment: "Production",
                Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null,
                DurationMs: null, CustomerId: null, UserId: null, Message: "boom", MessageTemplate: null,
                Exception: new IngestExceptionRequest("System.Exception", "at Foo.Bar() line 1"), Metadata: null) })
        };
        request.Headers.Add("X-Api-Key", apiKey);
        await client.SendAsync(request);

        var response = await client.GetAsync($"/api/v1/exception-groups?applicationId={appId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var groups = await response.Content.ReadFromJsonAsync<List<ExceptionGroupSummary>>();
        Assert.Single(groups!);
        Assert.Equal(1, groups![0].OccurrenceCount);
        Assert.NotEmpty(groups[0].DailyCounts);
    }

    [Fact]
    public async Task GetExceptionGroupById_ReturnsFullStackTraceAndAffectedContexts()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var (appId, apiKey) = await CreateAppWithApiKeyAsync(client, "ExGroupDetailTestApp");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/events")
        {
            Content = JsonContent.Create(new List<IngestEventRequest> { new(
                EventKey: null, Timestamp: DateTime.UtcNow, Severity: "Error", Environment: "Production",
                Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null,
                DurationMs: null, CustomerId: null, UserId: null, Message: "boom", MessageTemplate: null,
                Exception: new IngestExceptionRequest("System.Exception", "at Foo.Bar() line 1"), Metadata: null) })
        };
        request.Headers.Add("X-Api-Key", apiKey);
        await client.SendAsync(request);

        var listResponse = await client.GetAsync($"/api/v1/exception-groups?applicationId={appId}");
        var groups = await listResponse.Content.ReadFromJsonAsync<List<ExceptionGroupSummary>>();

        var detailResponse = await client.GetAsync($"/api/v1/exception-groups/{groups![0].Id}");

        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<ExceptionGroupDetail>();
        Assert.Equal("at Foo.Bar() line 1", detail!.RepresentativeStackTrace);
        Assert.NotEmpty(detail.AffectedContexts);
    }
}
