using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class IngestionControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public IngestionControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<(int ApplicationId, string ApiKey)> CreateAppWithApiKeyAsync(HttpClient client, string appName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));

        var keyResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("Ingestion test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        return (app.Id, key!.ApiKey);
    }

    private static IngestEventRequest ValidEvent(string? eventKey = null) => new(
        EventKey: eventKey, Timestamp: DateTime.UtcNow, Severity: "Error", Environment: "Production", Version: null,
        Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null, DurationMs: null,
        CustomerId: null, UserId: null, Message: "something failed", MessageTemplate: null, Exception: null, Metadata: null);

    private HttpRequestMessage BuildRequest(string apiKey, List<IngestEventRequest> events)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/events") { Content = JsonContent.Create(events) };
        request.Headers.Add("X-Api-Key", apiKey);
        return request;
    }

    [Fact]
    public async Task IngestEvents_ValidBatchWithApiKey_Returns202AndPersistsEvents()
    {
        var client = _factory.CreateClient();
        var (_, apiKey) = await CreateAppWithApiKeyAsync(client, "IngestValidBatchTestApp");

        var response = await client.SendAsync(BuildRequest(apiKey, new List<IngestEventRequest> { ValidEvent(), ValidEvent() }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.Equal(2, body!.Accepted);
        Assert.Equal(0, body.Rejected);
    }

    [Fact]
    public async Task IngestEvents_MissingApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/ingest/events", new List<IngestEventRequest> { ValidEvent() });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IngestEvents_InvalidApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.SendAsync(BuildRequest("lgp_not-a-real-key", new List<IngestEventRequest> { ValidEvent() }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IngestEvents_RevokedApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("IngestRevokedKeyTestApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var keyResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/api-keys", new CreateApiKeyRequest("To be revoked"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();
        await client.DeleteAsync($"/api/v1/admin/applications/{app.Id}/api-keys/{key!.Id}");

        var response = await client.SendAsync(BuildRequest(key.ApiKey, new List<IngestEventRequest> { ValidEvent() }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IngestEvents_EventWithInvalidRequiredField_RejectedButBatchContinues()
    {
        var client = _factory.CreateClient();
        var (_, apiKey) = await CreateAppWithApiKeyAsync(client, "IngestPartialFailureTestApp");
        var badEvent = ValidEvent() with { Message = null };

        var response = await client.SendAsync(BuildRequest(apiKey, new List<IngestEventRequest> { ValidEvent(), badEvent, ValidEvent() }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.Equal(2, body!.Accepted);
        Assert.Equal(1, body.Rejected);
        Assert.Single(body.Errors);
        Assert.Equal(1, body.Errors[0].Index);
    }

    [Fact]
    public async Task IngestEvents_HierarchyTypo_EventStoredWithWarningNotRejected()
    {
        var client = _factory.CreateClient();
        var (_, apiKey) = await CreateAppWithApiKeyAsync(client, "IngestHierarchyTypoTestApp");
        var eventWithTypo = ValidEvent() with { Hierarchy = new IngestHierarchyRequest("ChrgePayment", null, null, null) };

        var response = await client.SendAsync(BuildRequest(apiKey, new List<IngestEventRequest> { eventWithTypo }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.Equal(1, body!.Accepted);
        Assert.Equal(0, body.Rejected);
        Assert.Single(body.HierarchyWarnings);
        Assert.Equal("module", body.HierarchyWarnings[0].Field);
    }

    [Fact]
    public async Task IngestEvents_DuplicateEventKeyRetry_DoesNotDuplicateRow()
    {
        var client = _factory.CreateClient();
        var (_, apiKey) = await CreateAppWithApiKeyAsync(client, "IngestRetryTestApp");
        var evt = ValidEvent("retry-key-1");

        var first = await client.SendAsync(BuildRequest(apiKey, new List<IngestEventRequest> { evt }));
        var second = await client.SendAsync(BuildRequest(apiKey, new List<IngestEventRequest> { evt }));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.Equal(1, secondBody!.Accepted);
        Assert.Equal(0, secondBody.Rejected);
    }

    [Fact]
    public async Task IngestEvents_TwoEventsSameException_ShareOneExceptionGroup()
    {
        var client = _factory.CreateClient();
        var (_, apiKey) = await CreateAppWithApiKeyAsync(client, "IngestSharedExceptionGroupTestApp");
        var exceptionInfo = new IngestExceptionRequest("System.TimeoutException", "at Foo.Bar() in Foo.cs:line 10");
        var first = ValidEvent() with { Exception = exceptionInfo };
        var second = ValidEvent() with { Exception = exceptionInfo with { StackTrace = "at Foo.Bar() in Foo.cs:line 99" } };

        var response = await client.SendAsync(BuildRequest(apiKey, new List<IngestEventRequest> { first, second }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.Equal(2, body!.Accepted);
    }
}
