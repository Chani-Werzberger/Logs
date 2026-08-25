using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class LogSourceRepository : ILogSourceRepository
{
    private readonly IDbContextFactory<LogsPlatformDbContext> _contextFactory;

    public LogSourceRepository(IDbContextFactory<LogsPlatformDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<LogSource?> GetByIdAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.LogSources.FindAsync(id);
    }

    public async Task<IReadOnlyList<LogSource>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.LogSources.AsNoTracking().Where(l => l.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(l => l.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<LogSource> AddAsync(LogSource logSource)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        context.LogSources.Add(logSource);
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(logSource).State = EntityState.Detached;
            throw;
        }
        return logSource;
    }

    public async Task<LogSource> RenameAsync(int id, string name, string? description)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var logSource = await context.LogSources.FindAsync(id)
            ?? throw new InvalidOperationException($"LogSource {id} not found.");
        logSource.Name = name;
        logSource.Description = description;
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(logSource).State = EntityState.Detached;
            throw;
        }
        return logSource;
    }

    public async Task DeactivateAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var logSource = await context.LogSources.FindAsync(id)
            ?? throw new InvalidOperationException($"LogSource {id} not found.");
        logSource.IsActive = false;
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(logSource).State = EntityState.Detached;
            throw;
        }
    }
}
