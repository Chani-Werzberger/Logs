using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface ILogSourceRepository
{
    Task<LogSource?> GetByIdAsync(int id);
    Task<IReadOnlyList<LogSource>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<LogSource> AddAsync(LogSource logSource);
    Task<LogSource> RenameAsync(int id, string name, string? description);
    Task DeactivateAsync(int id);
}
