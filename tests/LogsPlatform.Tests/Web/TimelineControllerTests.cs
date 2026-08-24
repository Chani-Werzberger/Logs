using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class TimelineControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public TimelineControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<(int ApplicationId, string ApiKey)> CreateAppWithApiKeyAsync(HttpClient client, string appName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));
        var keyResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("Timeline test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();
        return (app.Id, key!.ApiKey);
    }

    [Fact]
    public async Task GetTimeline_ByCorrelationId_ReturnsOrderedEvents()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var (appId, apiKey) = await CreateAppWithApiKeyAsync(client, "TimelineQueryTestApp");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/events")
        {
            Content = JsonContent.Create(new List<IngestEventRequest>
            {
                new(EventKey: "t-1", Timestamp: DateTime.UtcNow.AddSeconds(-2), Severity: "Info", Environment: "Production",
                    Version: null, Hierarchy: null, CorrelationId: "order-99", TraceId: null, SpanId: null, ParentSpanId: null,
                    DurationMs: null, CustomerId: null, UserId: null, Message: "step 1", MessageTemplate: null, Exception: null, Metadata: null),
                new(EventKey: "t-2", Timestamp: DateTime.UtcNow, Severity: "Info", Environment: "Production",
                    Version: null, Hierarchy: null, CorrelationId: "order-99", TraceId: null, SpanId: null, ParentSpanId: null,
                    DurationMs: null, CustomerId: null, UserId: null, Message: "step 2", MessageTemplate: null, Exception: null, Metadata: null)
            })
        };
        request.Headers.Add("X-Api-Key", apiKey);
        await client.SendAsync(request);

        var response = await client.GetAsync($"/api/v1/timeline?applicationId={appId}&correlationId=order-99");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var timeline = await response.Content.ReadFromJsonAsync<List<EventSummary>>();
        Assert.Equal(2, timeline!.Count);
        Assert.Equal("step 1", timeline[0].Message);
    }

    [Fact]
    public async Task GetTimeline_NoLookupModeSupplied_Returns400()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var response = await client.GetAsync("/api/v1/timeline?applicationId=1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
