using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ProcessNodeRepository : IProcessNodeRepository
{
    private readonly LogsPlatformDbContext _context;

    public ProcessNodeRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<ProcessNode?> GetByIdAsync(int id) =>
        await _context.Processes.FindAsync(id);

    public async Task<IReadOnlyList<ProcessNode>> GetByScreenServiceIdAsync(int screenServiceId, bool includeInactive = false)
    {
        var query = _context.Processes.AsNoTracking().Where(p => p.ScreenServiceId == screenServiceId);
        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<ProcessNode> AddAsync(ProcessNode process)
    {
        _context.Processes.Add(process);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(process).State = EntityState.Detached;
            throw;
        }
        return process;
    }

    public async Task<ProcessNode> RenameAsync(int id, string name, string? description)
    {
        var process = await _context.Processes.FindAsync(id)
            ?? throw new InvalidOperationException($"ProcessNode {id} not found.");
        process.Name = name;
        process.Description = description;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(process).State = EntityState.Detached;
            throw;
        }
        return process;
    }

    public async Task DeactivateAsync(int id)
    {
        var process = await _context.Processes.FindAsync(id)
            ?? throw new InvalidOperationException($"ProcessNode {id} not found.");
        process.IsActive = false;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(process).State = EntityState.Detached;
            throw;
        }
    }
}
