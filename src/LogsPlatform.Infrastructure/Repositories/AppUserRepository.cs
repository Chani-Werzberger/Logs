using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class AppUserRepository : IAppUserRepository
{
    private readonly LogsPlatformDbContext _context;

    public AppUserRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<AppUser?> GetByIdAsync(int id) =>
        await _context.Users.FindAsync(id);

    public async Task<IReadOnlyList<AppUser>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        var query = _context.Users.AsNoTracking().Where(u => u.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(u => u.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<AppUser> AddAsync(AppUser user)
    {
        _context.Users.Add(user);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(user).State = EntityState.Detached;
            throw;
        }
        return user;
    }

    public async Task<AppUser> RenameAsync(int id, string displayName)
    {
        var user = await _context.Users.FindAsync(id)
            ?? throw new InvalidOperationException($"AppUser {id} not found.");
        user.DisplayName = displayName;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(user).State = EntityState.Detached;
            throw;
        }
        return user;
    }

    public async Task DeactivateAsync(int id)
    {
        var user = await _context.Users.FindAsync(id)
            ?? throw new InvalidOperationException($"AppUser {id} not found.");
        user.IsActive = false;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(user).State = EntityState.Detached;
            throw;
        }
    }
}
