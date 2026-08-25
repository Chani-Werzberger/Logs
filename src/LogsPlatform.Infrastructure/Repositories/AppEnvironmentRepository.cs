using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class AppEnvironmentRepository : IAppEnvironmentRepository
{
    private readonly IDbContextFactory<LogsPlatformDbContext> _contextFactory;

    public AppEnvironmentRepository(IDbContextFactory<LogsPlatformDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<AppEnvironment?> GetByIdAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.AppEnvironments.FindAsync(id);
    }

    public async Task<IReadOnlyList<AppEnvironment>> GetByApplicationIdAsync(int applicationId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.AppEnvironments
            .AsNoTracking()
            .Where(e => e.ApplicationId == applicationId)
            .ToListAsync();
    }

    public async Task<AppEnvironment> AddAsync(AppEnvironment environment)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        context.AppEnvironments.Add(environment);
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(environment).State = EntityState.Detached;
            throw;
        }
        return environment;
    }
}
