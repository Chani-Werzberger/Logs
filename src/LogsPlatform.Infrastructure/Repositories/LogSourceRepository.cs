using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class LogSourceRepository : ILogSourceRepository
{
    private readonly LogsPlatformDbContext _context;

    public LogSourceRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<LogSource?> GetByIdAsync(int id) =>
        await _context.LogSources.FindAsync(id);

    public async Task<IReadOnlyList<LogSource>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        var query = _context.LogSources.AsNoTracking().Where(l => l.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(l => l.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<LogSource> AddAsync(LogSource logSource)
    {
        _context.LogSources.Add(logSource);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(logSource).State = EntityState.Detached;
            throw;
        }
        return logSource;
    }

    public async Task<LogSource> RenameAsync(int id, string name, string? description)
    {
        var logSource = await _context.LogSources.FindAsync(id)
            ?? throw new InvalidOperationException($"LogSource {id} not found.");
        logSource.Name = name;
        logSource.Description = description;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(logSource).State = EntityState.Detached;
            throw;
        }
        return logSource;
    }

    public async Task DeactivateAsync(int id)
    {
        var logSource = await _context.LogSources.FindAsync(id)
            ?? throw new InvalidOperationException($"LogSource {id} not found.");
        logSource.IsActive = false;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(logSource).State = EntityState.Detached;
            throw;
        }
    }
}
