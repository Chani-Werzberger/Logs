using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class OperationRepository : IOperationRepository
{
    private readonly LogsPlatformDbContext _context;

    public OperationRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<Operation?> GetByIdAsync(int id) =>
        await _context.Operations.FindAsync(id);

    public async Task<IReadOnlyList<Operation>> GetByProcessIdAsync(int processId, bool includeInactive = false)
    {
        var query = _context.Operations.AsNoTracking().Where(o => o.ProcessId == processId);
        if (!includeInactive)
        {
            query = query.Where(o => o.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<Operation> AddAsync(Operation operation)
    {
        _context.Operations.Add(operation);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(operation).State = EntityState.Detached;
            throw;
        }
        return operation;
    }

    public async Task<Operation> RenameAsync(int id, string name, string? description)
    {
        var operation = await _context.Operations.FindAsync(id)
            ?? throw new InvalidOperationException($"Operation {id} not found.");
        operation.Name = name;
        operation.Description = description;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(operation).State = EntityState.Detached;
            throw;
        }
        return operation;
    }

    public async Task DeactivateAsync(int id)
    {
        var operation = await _context.Operations.FindAsync(id)
            ?? throw new InvalidOperationException($"Operation {id} not found.");
        operation.IsActive = false;
        await _context.SaveChangesAsync();
    }
}
