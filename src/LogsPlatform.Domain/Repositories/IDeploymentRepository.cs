using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IDeploymentRepository
{
    Task<Deployment?> GetByIdAsync(int id);
    Task<IReadOnlyList<Deployment>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<IReadOnlyList<Deployment>> GetInWindowAsync(int applicationId, int environmentId, DateTime windowStart, DateTime windowEnd);
    Task<Deployment> AddAsync(Deployment deployment);
    Task<Deployment> RenameAsync(int id, string? notes);
    Task DeactivateAsync(int id);
}
