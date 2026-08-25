using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Google.Protobuf;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class OtlpLogsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public OtlpLogsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<(int ApplicationId, int EnvironmentId, string ApiKey)> CreateAppWithApiKeyAsync(HttpClient client, string appName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var envResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));
        var env = await envResponse.Content.ReadFromJsonAsync<EnvironmentResponse>();

        var keyResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("OTLP test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        return (app.Id, env!.Id, key!.ApiKey);
    }

    private static ExportLogsServiceRequest ValidRequest(string message = "otlp event")
    {
        var logRecord = new LogRecord
        {
            TimeUnixNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
            SeverityNumber = SeverityNumber.Error,
            Body = new AnyValue { StringValue = message }
        };
        var resource = new Resource();
        resource.Attributes.Add(new KeyValue { Key = "deployment.environment", Value = new AnyValue { StringValue = "Production" } });

        var scopeLogs = new ScopeLogs();
        scopeLogs.LogRecords.Add(logRecord);
        var resourceLogs = new ResourceLogs { Resource = resource };
        resourceLogs.ScopeLogs.Add(scopeLogs);

        var request = new ExportLogsServiceRequest();
        request.ResourceLogs.Add(resourceLogs);
        return request;
    }

    private static HttpRequestMessage BuildRequest(string? apiKey, ExportLogsServiceRequest body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/logs")
        {
            Content = new ByteArrayContent(body.ToByteArray())
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        if (apiKey is not null)
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }
        return request;
    }

    [Fact]
    public async Task Export_ValidLogRecord_Returns200AndPersistsEvent()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var (applicationId, environmentId, apiKey) = await CreateAppWithApiKeyAsync(client, "OtlpValidTestApp");

        var response = await client.SendAsync(BuildRequest(apiKey, ValidRequest()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        var parsed = ExportLogsServiceResponse.Parser.ParseFrom(responseBytes);
        Assert.Null(parsed.PartialSuccess);

        var eventsResponse = await client.GetAsync($"/api/v1/events?applicationId={applicationId}&environmentId={environmentId}&page=1&pageSize=10");
        var events = await eventsResponse.Content.ReadFromJsonAsync<EventListResponse>();
        Assert.Equal(1, events!.TotalCount);
        Assert.Equal("otlp event", events.Items[0].Message);
    }

    [Fact]
    public async Task Export_MissingApiKey_Returns401()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var response = await client.SendAsync(BuildRequest(null, ValidRequest()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Export_InvalidApiKey_Returns401()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var response = await client.SendAsync(BuildRequest("lgp_not-a-real-key", ValidRequest()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Export_UnmappableSeverity_ReportsPartialSuccessWithRejectedCount()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var (_, _, apiKey) = await CreateAppWithApiKeyAsync(client, "OtlpRejectTestApp");
        var badRequest = ValidRequest();
        badRequest.ResourceLogs[0].ScopeLogs[0].LogRecords[0].SeverityNumber = SeverityNumber.Unspecified;

        var response = await client.SendAsync(BuildRequest(apiKey, badRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        var parsed = ExportLogsServiceResponse.Parser.ParseFrom(responseBytes);
        Assert.NotNull(parsed.PartialSuccess);
        Assert.Equal(1, parsed.PartialSuccess.RejectedLogRecords);
    }

    [Fact]
    public async Task Export_TwoLogRecordsOneInvalid_AcceptsValidRejectsInvalid()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var (applicationId, environmentId, apiKey) = await CreateAppWithApiKeyAsync(client, "OtlpPartialBatchTestApp");
        var request = ValidRequest("good event");
        var badRecord = new LogRecord
        {
            TimeUnixNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
            SeverityNumber = SeverityNumber.Unspecified,
            Body = new AnyValue { StringValue = "bad event" }
        };
        request.ResourceLogs[0].ScopeLogs[0].LogRecords.Add(badRecord);

        var response = await client.SendAsync(BuildRequest(apiKey, request));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        var parsed = ExportLogsServiceResponse.Parser.ParseFrom(responseBytes);
        Assert.Equal(1, parsed.PartialSuccess.RejectedLogRecords);

        var eventsResponse = await client.GetAsync($"/api/v1/events?applicationId={applicationId}&environmentId={environmentId}&page=1&pageSize=10");
        var events = await eventsResponse.Content.ReadFromJsonAsync<EventListResponse>();
        Assert.Equal(1, events!.TotalCount);
        Assert.Equal("good event", events.Items[0].Message);
    }
}
