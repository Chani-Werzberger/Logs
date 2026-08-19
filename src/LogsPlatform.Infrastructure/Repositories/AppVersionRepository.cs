using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class AppVersionRepository : IAppVersionRepository
{
    private readonly LogsPlatformDbContext _context;

    public AppVersionRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<AppVersion?> GetByIdAsync(int id) =>
        await _context.Versions.FindAsync(id);

    public async Task<IReadOnlyList<AppVersion>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        var query = _context.Versions.AsNoTracking().Where(v => v.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(v => v.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<AppVersion> AddAsync(AppVersion version)
    {
        _context.Versions.Add(version);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(version).State = EntityState.Detached;
            throw;
        }
        return version;
    }

    public async Task<AppVersion> RenameAsync(int id, string? releaseNotes)
    {
        var version = await _context.Versions.FindAsync(id)
            ?? throw new InvalidOperationException($"AppVersion {id} not found.");
        version.ReleaseNotes = releaseNotes;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(version).State = EntityState.Detached;
            throw;
        }
        return version;
    }

    public async Task DeactivateAsync(int id)
    {
        var version = await _context.Versions.FindAsync(id)
            ?? throw new InvalidOperationException($"AppVersion {id} not found.");
        version.IsActive = false;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(version).State = EntityState.Detached;
            throw;
        }
    }
}
