namespace LogsPlatform.Client;

public record IngestHierarchyPayload(string? Module, string? ScreenService, string? Process, string? Operation);

public record IngestExceptionPayload(string Type, string? StackTrace);

public record EventPayload(
    string? EventKey,
    DateTime Timestamp,
    string Severity,
    string Environment,
    string? Version,
    IngestHierarchyPayload? Hierarchy,
    string? CorrelationId,
    string? TraceId,
    string? SpanId,
    string? ParentSpanId,
    double? DurationMs,
    string? CustomerId,
    string? UserId,
    string Message,
    string? MessageTemplate,
    IngestExceptionPayload? Exception,
    Dictionary<string, object>? Metadata);
