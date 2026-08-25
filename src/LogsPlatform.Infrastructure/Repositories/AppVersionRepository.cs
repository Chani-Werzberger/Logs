using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class AppVersionRepository : IAppVersionRepository
{
    private readonly IDbContextFactory<LogsPlatformDbContext> _contextFactory;

    public AppVersionRepository(IDbContextFactory<LogsPlatformDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<AppVersion?> GetByIdAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Versions.FindAsync(id);
    }

    public async Task<IReadOnlyList<AppVersion>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Versions.AsNoTracking().Where(v => v.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(v => v.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<AppVersion> AddAsync(AppVersion version)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        context.Versions.Add(version);
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(version).State = EntityState.Detached;
            throw;
        }
        return version;
    }

    public async Task<AppVersion> RenameAsync(int id, string? releaseNotes)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var version = await context.Versions.FindAsync(id)
            ?? throw new InvalidOperationException($"AppVersion {id} not found.");
        version.ReleaseNotes = releaseNotes;
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(version).State = EntityState.Detached;
            throw;
        }
        return version;
    }

    public async Task DeactivateAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var version = await context.Versions.FindAsync(id)
            ?? throw new InvalidOperationException($"AppVersion {id} not found.");
        version.IsActive = false;
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(version).State = EntityState.Detached;
            throw;
        }
    }
}
