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

public record FindingSummary(long Id, string Type, string Title, string Severity, string ConfidenceLevel, string Status, DateTime DetectedAt, string ApplicationName, string? OperationName);

public record FindingStatementDto(long Id, string Kind, string Text, int OrderIndex, string? ApprovedBy, DateTime? ApprovedAt);

public record EvidenceDto(long Id, string EvidenceType, long ReferenceId, string Description);

public record FindingDetail(long Id, string Type, string Title, string Severity, string ConfidenceLevel, string Status, DateTime DetectedAt, string ApplicationName, string EnvironmentName, IReadOnlyList<FindingStatementDto> Statements, IReadOnlyList<EvidenceDto> Evidence);
