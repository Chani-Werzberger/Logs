using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class AppEnvironmentRepository : IAppEnvironmentRepository
{
    private readonly LogsPlatformDbContext _context;

    public AppEnvironmentRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<AppEnvironment?> GetByIdAsync(int id) =>
        await _context.AppEnvironments.FindAsync(id);

    public async Task<IReadOnlyList<AppEnvironment>> GetByApplicationIdAsync(int applicationId) =>
        await _context.AppEnvironments
            .AsNoTracking()
            .Where(e => e.ApplicationId == applicationId)
            .ToListAsync();

    public async Task<AppEnvironment> AddAsync(AppEnvironment environment)
    {
        _context.AppEnvironments.Add(environment);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(environment).State = EntityState.Detached;
            throw;
        }
        return environment;
    }
}
