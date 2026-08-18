using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IOperationRepository
{
    Task<Operation?> GetByIdAsync(int id);
    Task<IReadOnlyList<Operation>> GetByProcessIdAsync(int processId, bool includeInactive = false);
    Task<Operation> AddAsync(Operation operation);
    Task<Operation> RenameAsync(int id, string name, string? description);
    Task DeactivateAsync(int id);
}
