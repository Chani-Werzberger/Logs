using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Contracts;

public record EventSummary(long Id, DateTime Timestamp, string Severity, string? OperationPath, string Message, double? DurationMs, string? CorrelationId);

public record EventDetail(
    long Id, DateTime Timestamp, string Severity, int ApplicationId, int EnvironmentId,
    int? VersionId, int? ModuleId, int? ScreenServiceId, int? ProcessId, int? OperationId,
    int? CustomerId, int? AppUserId, string? EventKey, string? CorrelationId, string? TraceId,
    string? SpanId, string? ParentSpanId, double? DurationMs, string Message, string? MessageTemplate,
    long? ExceptionGroupId, string? StackTrace, string? MetadataJson, string? OperationPath);

public record EventListResponse(IReadOnlyList<EventSummary> Items, int TotalCount);

public record ExceptionGroupSummary(long Id, string Fingerprint, string ExceptionType, int OccurrenceCount, DateTime FirstSeenAt, DateTime LastSeenAt, IReadOnlyDictionary<DateOnly, int> DailyCounts, IReadOnlyList<string> AffectedOperations);

public record ExceptionGroupDetail(long Id, string Fingerprint, string ExceptionType, string RepresentativeStackTrace, int OccurrenceCount, DateTime FirstSeenAt, DateTime LastSeenAt, IReadOnlyDictionary<DateOnly, int> DailyCounts, IReadOnlyList<AffectedContext> AffectedContexts);
