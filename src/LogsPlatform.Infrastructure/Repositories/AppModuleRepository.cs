using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class AppModuleRepository : IAppModuleRepository
{
    private readonly LogsPlatformDbContext _context;

    public AppModuleRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<AppModule?> GetByIdAsync(int id) =>
        await _context.Modules.FindAsync(id);

    public async Task<IReadOnlyList<AppModule>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        var query = _context.Modules.AsNoTracking().Where(m => m.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(m => m.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<AppModule> AddAsync(AppModule module)
    {
        _context.Modules.Add(module);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(module).State = EntityState.Detached;
            throw;
        }
        return module;
    }

    public async Task<AppModule> RenameAsync(int id, string name, string? description)
    {
        var module = await _context.Modules.FindAsync(id)
            ?? throw new InvalidOperationException($"Module {id} not found.");
        module.Name = name;
        module.Description = description;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(module).State = EntityState.Detached;
            throw;
        }
        return module;
    }

    public async Task DeactivateAsync(int id)
    {
        var module = await _context.Modules.FindAsync(id)
            ?? throw new InvalidOperationException($"Module {id} not found.");
        module.IsActive = false;
        await _context.SaveChangesAsync();
    }
}
