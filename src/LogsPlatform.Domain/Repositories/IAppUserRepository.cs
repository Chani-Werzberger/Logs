using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IAppUserRepository
{
    Task<AppUser?> GetByIdAsync(int id);
    Task<IReadOnlyList<AppUser>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<AppUser> AddAsync(AppUser user);
    Task<AppUser> RenameAsync(int id, string displayName);
    Task DeactivateAsync(int id);
}
