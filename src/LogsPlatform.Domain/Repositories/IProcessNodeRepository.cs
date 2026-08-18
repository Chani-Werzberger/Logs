using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IProcessNodeRepository
{
    Task<ProcessNode?> GetByIdAsync(int id);
    Task<IReadOnlyList<ProcessNode>> GetByScreenServiceIdAsync(int screenServiceId, bool includeInactive = false);
    Task<ProcessNode> AddAsync(ProcessNode process);
    Task<ProcessNode> RenameAsync(int id, string name, string? description);
    Task DeactivateAsync(int id);
}
