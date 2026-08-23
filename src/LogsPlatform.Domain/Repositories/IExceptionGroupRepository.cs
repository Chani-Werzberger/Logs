using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public record ExceptionGroupQueryParameters(int ApplicationId, DateTime? From, DateTime? To, string SortBy);

public record AffectedContext(string ApplicationName, string EnvironmentName, string? VersionNumber, string? OperationName);

public interface IExceptionGroupRepository
{
    Task<ExceptionGroup> GetOrCreateAsync(
        int applicationId, string fingerprint, string exceptionType,
        string messageTemplate, string representativeStackTrace, DateTime seenAt);
    Task<IReadOnlyList<ExceptionGroup>> QueryAsync(ExceptionGroupQueryParameters parameters);
    Task<ExceptionGroup?> GetByIdAsync(long id);
    Task<IReadOnlyDictionary<DateOnly, int>> GetDailyCountsAsync(long exceptionGroupId, int days);
    Task<IReadOnlyList<AffectedContext>> GetAffectedContextsAsync(long exceptionGroupId);
}
