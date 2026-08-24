using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class PlatformUserRepository : IPlatformUserRepository
{
    private readonly LogsPlatformDbContext _context;

    public PlatformUserRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<PlatformUser?> GetByUsernameAsync(string username) =>
        await _context.PlatformUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username);

    public async Task<IReadOnlyList<PlatformUser>> GetAllAsync() =>
        await _context.PlatformUsers.AsNoTracking().OrderBy(u => u.Username).ToListAsync();

    public async Task<PlatformUser> AddAsync(PlatformUser user)
    {
        _context.PlatformUsers.Add(user);
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
        var user = await _context.PlatformUsers.FindAsync(id)
            ?? throw new InvalidOperationException($"PlatformUser {id} not found.");

        await _context.Entry(user).ReloadAsync();

        if (!user.IsActive)
        {
            return;
        }

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

    public async Task<bool> AnyAsync() =>
        await _context.PlatformUsers.AnyAsync();
}
