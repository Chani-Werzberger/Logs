using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ScreenServiceRepository : IScreenServiceRepository
{
    private readonly LogsPlatformDbContext _context;

    public ScreenServiceRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<ScreenService?> GetByIdAsync(int id) =>
        await _context.ScreenServices.FindAsync(id);

    public async Task<IReadOnlyList<ScreenService>> GetByModuleIdAsync(int moduleId, bool includeInactive = false)
    {
        var query = _context.ScreenServices.AsNoTracking().Where(s => s.ModuleId == moduleId);
        if (!includeInactive)
        {
            query = query.Where(s => s.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<ScreenService> AddAsync(ScreenService screenService)
    {
        _context.ScreenServices.Add(screenService);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(screenService).State = EntityState.Detached;
            throw;
        }
        return screenService;
    }

    public async Task<ScreenService> RenameAsync(int id, string name, string? description)
    {
        var screenService = await _context.ScreenServices.FindAsync(id)
            ?? throw new InvalidOperationException($"ScreenService {id} not found.");
        screenService.Name = name;
        screenService.Description = description;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(screenService).State = EntityState.Detached;
            throw;
        }
        return screenService;
    }

    public async Task DeactivateAsync(int id)
    {
        var screenService = await _context.ScreenServices.FindAsync(id)
            ?? throw new InvalidOperationException($"ScreenService {id} not found.");
        screenService.IsActive = false;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(screenService).State = EntityState.Detached;
            throw;
        }
    }
}
