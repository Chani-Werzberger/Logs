using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IScreenServiceRepository
{
    Task<ScreenService?> GetByIdAsync(int id);
    Task<IReadOnlyList<ScreenService>> GetByModuleIdAsync(int moduleId, bool includeInactive = false);
    Task<ScreenService> AddAsync(ScreenService screenService);
    Task<ScreenService> RenameAsync(int id, string name, string? description);
    Task DeactivateAsync(int id);
}
