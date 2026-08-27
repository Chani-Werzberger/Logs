using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public record IngestResult(int Accepted, int DuplicateEventKeysSkipped);

public record EventQueryParameters(
    int ApplicationId,
    int EnvironmentId,
    DateTime? From,
    DateTime? To,
    int? Severity,
    int? ModuleId,
    int? ScreenServiceId,
    int? ProcessId,
    int? OperationId,
    string? CorrelationId,
    string? TraceId,
    string? UserId,
    string? CustomerId,
    long? ExceptionGroupId,
    int? VersionId,
    double? DurationMinMs,
    double? DurationMaxMs,
    string? MessageContains,
    int Page,
    int PageSize);

public record TimelineQuery(
    int ApplicationId,
    string? CorrelationId,
    string? TraceId,
    int? OperationId,
    string? UserId,
    string? CustomerId);

public interface IEventRepository
{
    Task<IngestResult> AddEventsAsync(int applicationId, IReadOnlyList<Event> events);
    Task<(IReadOnlyList<Event> Items, int TotalCount)> QueryAsync(EventQueryParameters parameters);
    Task<Event?> GetByIdAsync(int applicationId, long id);
    Task<IReadOnlyList<Event>> GetTimelineAsync(TimelineQuery query);
    Task<int> DeleteOlderThanAsync(int applicationId, DateTime cutoffUtc);
}
