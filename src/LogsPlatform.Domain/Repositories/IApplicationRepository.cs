using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IApplicationRepository
{
    Task<Application?> GetByIdAsync(int id);
    Task<IReadOnlyList<Application>> GetAllAsync();
    Task<Application> AddAsync(Application application);
}
