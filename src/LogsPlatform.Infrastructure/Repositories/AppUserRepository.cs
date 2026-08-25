using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class AppUserRepository : IAppUserRepository
{
    private readonly IDbContextFactory<LogsPlatformDbContext> _contextFactory;

    public AppUserRepository(IDbContextFactory<LogsPlatformDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<AppUser?> GetByIdAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Users.FindAsync(id);
    }

    public async Task<IReadOnlyList<AppUser>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Users.AsNoTracking().Where(u => u.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(u => u.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<AppUser> AddAsync(AppUser user)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        context.Users.Add(user);
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(user).State = EntityState.Detached;
            throw;
        }
        return user;
    }

    public async Task<AppUser> RenameAsync(int id, string displayName)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var user = await context.Users.FindAsync(id)
            ?? throw new InvalidOperationException($"AppUser {id} not found.");
        user.DisplayName = displayName;
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(user).State = EntityState.Detached;
            throw;
        }
        return user;
    }

    public async Task DeactivateAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var user = await context.Users.FindAsync(id)
            ?? throw new InvalidOperationException($"AppUser {id} not found.");
        user.IsActive = false;
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(user).State = EntityState.Detached;
            throw;
        }
    }
}
