using System.Net;
using LogsPlatform.Client;
using Xunit;

namespace LogsPlatform.Tests.Client;

public class LogsPlatformClientRedactionTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> CapturedRequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                CapturedRequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private static EventPayload BuildEvent(string message, Dictionary<string, object>? metadata = null) => new(
        EventKey: null, Timestamp: DateTime.UtcNow, Severity: "Error", Environment: "Production",
        Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null,
        DurationMs: null, CustomerId: null, UserId: null, Message: message, MessageTemplate: null,
        Exception: null, Metadata: metadata);

    [Fact]
    public async Task SendEventAsync_WithRedactionHook_TransformsMessageBeforeSending()
    {
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var client = new LogsPlatformClient(
            baseUrl: "http://localhost/", apiKey: "irrelevant", httpClient: httpClient,
            batchSize: 1, period: TimeSpan.FromMinutes(10),
            redactMessage: msg => msg.Replace("secret-value", "[REDACTED]"));

        await client.SendEventAsync(BuildEvent("credit card is secret-value here"));
        await Task.Delay(100);

        Assert.Single(handler.CapturedRequestBodies);
        Assert.Contains("[REDACTED]", handler.CapturedRequestBodies[0]);
        Assert.DoesNotContain("secret-value", handler.CapturedRequestBodies[0]);
    }

    [Fact]
    public async Task SendEventAsync_WithRedactionHook_TransformsStringMetadataValuesOnly()
    {
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var client = new LogsPlatformClient(
            baseUrl: "http://localhost/", apiKey: "irrelevant", httpClient: httpClient,
            batchSize: 1, period: TimeSpan.FromMinutes(10),
            redactMessage: msg => msg.Replace("secret-value", "[REDACTED]"));

        await client.SendEventAsync(BuildEvent("no secrets here", new Dictionary<string, object>
        {
            ["note"] = "contains secret-value inline",
            ["retryCount"] = 3
        }));
        await Task.Delay(100);

        Assert.Single(handler.CapturedRequestBodies);
        Assert.Contains("[REDACTED]", handler.CapturedRequestBodies[0]);
        Assert.Contains("\"retryCount\":3", handler.CapturedRequestBodies[0]);
    }

    [Fact]
    public async Task SendEventAsync_NoRedactionHook_MessagePassesThroughUnchanged()
    {
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var client = new LogsPlatformClient(
            baseUrl: "http://localhost/", apiKey: "irrelevant", httpClient: httpClient,
            batchSize: 1, period: TimeSpan.FromMinutes(10));

        await client.SendEventAsync(BuildEvent("secret-value stays as-is"));
        await Task.Delay(100);

        Assert.Single(handler.CapturedRequestBodies);
        Assert.Contains("secret-value stays as-is", handler.CapturedRequestBodies[0]);
    }
}
