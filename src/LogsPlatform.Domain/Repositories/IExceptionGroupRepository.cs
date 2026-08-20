using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IExceptionGroupRepository
{
    Task<ExceptionGroup> GetOrCreateAsync(
        int applicationId, string fingerprint, string exceptionType,
        string messageTemplate, string representativeStackTrace, DateTime seenAt);
}
