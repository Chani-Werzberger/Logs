using System.Net.Http.Json;
using LogsPlatform.SyntheticDataGenerator;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

public static class IngestionSender
{
    public static async Task SendBatchedAsync(HttpClient client, string apiKey, IReadOnlyList<SimulatedEvent> events, int batchSize = 500)
    {
        foreach (var batch in Chunk(events, batchSize))
        {
            var requests = batch.Select(ToRequest).ToList();
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/events") { Content = JsonContent.Create(requests) };
            httpRequest.Headers.Add("X-Api-Key", apiKey);

            var response = await client.SendAsync(httpRequest);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<IngestResponse>();

            Assert.Equal(batch.Count, body!.Accepted);
            Assert.Equal(0, body.Rejected);
        }
    }

    private static IngestEventRequest ToRequest(SimulatedEvent evt)
    {
        IngestHierarchyRequest? hierarchy = evt.Operation is null ? null
            : new IngestHierarchyRequest(evt.Module, evt.ScreenService, evt.Process, evt.Operation);

        IngestExceptionRequest? exception = evt.ExceptionType is null ? null
            : new IngestExceptionRequest(evt.ExceptionType, evt.StackTrace);

        return new IngestEventRequest(
            EventKey: null, Timestamp: evt.Timestamp, Severity: evt.Severity, Environment: "Production",
            Version: null, Hierarchy: hierarchy, CorrelationId: evt.CorrelationId, TraceId: null, SpanId: null,
            ParentSpanId: null, DurationMs: evt.DurationMs, CustomerId: evt.CustomerId, UserId: null,
            Message: evt.Message, MessageTemplate: null, Exception: exception, Metadata: null);
    }

    private static IEnumerable<List<SimulatedEvent>> Chunk(IReadOnlyList<SimulatedEvent> events, int size)
    {
        for (var i = 0; i < events.Count; i += size)
        {
            yield return events.Skip(i).Take(size).ToList();
        }
    }
}
