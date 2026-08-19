using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IApiKeyRepository
{
    Task<ApiKey?> GetByIdAsync(int id);
    Task<IReadOnlyList<ApiKey>> GetByApplicationIdAsync(int applicationId, bool includeRevoked = false);
    Task<(ApiKey Entity, string RawKey)> AddAsync(int applicationId, string label);
    Task RevokeAsync(int id);
}
