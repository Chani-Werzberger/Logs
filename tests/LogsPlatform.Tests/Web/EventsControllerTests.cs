using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class EventsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public EventsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<(int ApplicationId, int EnvironmentId, string ApiKey)> CreateAppWithApiKeyAsync(HttpClient client, string appName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var envResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));
        var env = await envResponse.Content.ReadFromJsonAsync<EnvironmentResponse>();

        var keyResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("Events query test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        return (app.Id, env!.Id, key!.ApiKey);
    }

    private static HttpRequestMessage BuildIngestRequest(string apiKey, List<IngestEventRequest> events)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/events") { Content = JsonContent.Create(events) };
        request.Headers.Add("X-Api-Key", apiKey);
        return request;
    }

    [Fact]
    public async Task GetEvents_FiltersBySeverityAndPaginates()
    {
        var client = _factory.CreateClient();
        var (appId, environmentId, apiKey) = await CreateAppWithApiKeyAsync(client, "EventsQueryTestApp");

        var events = Enumerable.Range(0, 3).Select(i => new IngestEventRequest(
            EventKey: $"evt-{i}", Timestamp: DateTime.UtcNow, Severity: "Error", Environment: "Production",
            Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null,
            DurationMs: null, CustomerId: null, UserId: null, Message: $"error {i}", MessageTemplate: null,
            Exception: null, Metadata: null)).ToList();
        events.Add(new IngestEventRequest(
            EventKey: "evt-info", Timestamp: DateTime.UtcNow, Severity: "Info", Environment: "Production",
            Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null,
            DurationMs: null, CustomerId: null, UserId: null, Message: "info event", MessageTemplate: null,
            Exception: null, Metadata: null));
        await client.SendAsync(BuildIngestRequest(apiKey, events));

        var response = await client.GetAsync($"/api/v1/events?applicationId={appId}&environmentId={environmentId}&severity=Error&page=1&pageSize=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EventListResponse>();
        Assert.Equal(3, body!.TotalCount);
        Assert.Equal(2, body.Items.Count);
    }

    [Fact]
    public async Task GetEventById_MismatchedApplicationId_Returns404()
    {
        var client = _factory.CreateClient();
        var (appId, environmentId, apiKey) = await CreateAppWithApiKeyAsync(client, "EventByIdTestApp");
        var (otherAppId, _, _) = await CreateAppWithApiKeyAsync(client, "EventByIdOtherApp");

        await client.SendAsync(BuildIngestRequest(apiKey, new List<IngestEventRequest> { new(
            EventKey: "evt-single", Timestamp: DateTime.UtcNow, Severity: "Error", Environment: "Production",
            Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null,
            DurationMs: null, CustomerId: null, UserId: null, Message: "single event", MessageTemplate: null,
            Exception: null, Metadata: null) }));

        var listResponse = await client.GetAsync($"/api/v1/events?applicationId={appId}&environmentId={environmentId}");
        var list = await listResponse.Content.ReadFromJsonAsync<EventListResponse>();
        var eventId = list!.Items[0].Id;

        var wrongAppResponse = await client.GetAsync($"/api/v1/events/{eventId}?applicationId={otherAppId}");
        Assert.Equal(HttpStatusCode.NotFound, wrongAppResponse.StatusCode);

        var correctAppResponse = await client.GetAsync($"/api/v1/events/{eventId}?applicationId={appId}");
        Assert.Equal(HttpStatusCode.OK, correctAppResponse.StatusCode);
    }
}
