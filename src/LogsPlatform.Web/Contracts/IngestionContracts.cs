namespace LogsPlatform.Web.Contracts;

public record IngestHierarchyRequest(string? Module, string? ScreenService, string? Process, string? Operation);
public record IngestExceptionRequest(string Type, string? StackTrace);

public record IngestEventRequest(
    string? EventKey,
    DateTime? Timestamp,
    string? Severity,
    string? Environment,
    string? Version,
    IngestHierarchyRequest? Hierarchy,
    string? CorrelationId,
    string? TraceId,
    string? SpanId,
    string? ParentSpanId,
    double? DurationMs,
    string? CustomerId,
    string? UserId,
    string? Message,
    string? MessageTemplate,
    IngestExceptionRequest? Exception,
    Dictionary<string, object>? Metadata);

public record IngestErrorEntry(int Index, string Reason);
public record IngestWarningEntry(int Index, string Field, string Reason);
public record IngestResponse(int Accepted, int Rejected, List<IngestErrorEntry> Errors, List<IngestWarningEntry> HierarchyWarnings);
