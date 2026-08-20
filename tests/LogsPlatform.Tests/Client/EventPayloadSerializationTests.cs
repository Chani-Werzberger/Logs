using System.Text.Json;
using LogsPlatform.Client;

namespace LogsPlatform.Tests.Client;

public class EventPayloadSerializationTests
{
    [Fact]
    public void EventPayload_FullyPopulated_SerializesAllFieldsWithPascalCaseNames()
    {
        var payload = new EventPayload(
            EventKey: "key-1", Timestamp: new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc),
            Severity: "Error", Environment: "Production", Version: "1.2.3",
            Hierarchy: new IngestHierarchyPayload("Billing", "InvoiceService", "ChargeCard", "Charge"),
            CorrelationId: "corr-1", TraceId: "trace-1", SpanId: "span-1", ParentSpanId: "parent-1",
            DurationMs: 12.5, CustomerId: "cust-1", UserId: "user-1",
            Message: "Something failed", MessageTemplate: "Something {What}",
            Exception: new IngestExceptionPayload("System.TimeoutException", "at Foo.Bar() line 1"),
            Metadata: new Dictionary<string, object> { ["Key"] = "Value" });

        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"EventKey\":\"key-1\"", json);
        Assert.Contains("\"Severity\":\"Error\"", json);
        Assert.Contains("\"Environment\":\"Production\"", json);
        Assert.Contains("\"Hierarchy\":{\"Module\":\"Billing\"", json);
        Assert.Contains("\"Exception\":{\"Type\":\"System.TimeoutException\"", json);
        Assert.Contains("\"Message\":\"Something failed\"", json);
    }

    [Fact]
    public void EventPayload_OnlyRequiredFields_SerializesWithoutThrowing()
    {
        var payload = new EventPayload(
            EventKey: null, Timestamp: DateTime.UtcNow, Severity: "Info", Environment: "Production",
            Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null,
            ParentSpanId: null, DurationMs: null, CustomerId: null, UserId: null,
            Message: "hello", MessageTemplate: null, Exception: null, Metadata: null);

        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"Message\":\"hello\"", json);
    }
}
