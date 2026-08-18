using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IAppModuleRepository
{
    Task<AppModule?> GetByIdAsync(int id);
    Task<IReadOnlyList<AppModule>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<AppModule> AddAsync(AppModule module);
    Task<AppModule> RenameAsync(int id, string name, string? description);
    Task DeactivateAsync(int id);
}
