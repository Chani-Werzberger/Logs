using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IAppEnvironmentRepository
{
    Task<AppEnvironment?> GetByIdAsync(int id);
    Task<IReadOnlyList<AppEnvironment>> GetByApplicationIdAsync(int applicationId);
    Task<AppEnvironment> AddAsync(AppEnvironment environment);
}
