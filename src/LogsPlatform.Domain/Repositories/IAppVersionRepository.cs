using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IAppVersionRepository
{
    Task<AppVersion?> GetByIdAsync(int id);
    Task<IReadOnlyList<AppVersion>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<AppVersion> AddAsync(AppVersion version);
    Task<AppVersion> RenameAsync(int id, string? releaseNotes);
    Task DeactivateAsync(int id);
}
