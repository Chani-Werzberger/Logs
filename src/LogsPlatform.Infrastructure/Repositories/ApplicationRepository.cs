using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ApplicationRepository : IApplicationRepository
{
    private readonly IDbContextFactory<LogsPlatformDbContext> _contextFactory;

    public ApplicationRepository(IDbContextFactory<LogsPlatformDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<Application?> GetByIdAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Applications.FindAsync(id);
    }

    public async Task<IReadOnlyList<Application>> GetAllAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Applications.AsNoTracking().ToListAsync();
    }

    public async Task<Application> AddAsync(Application application)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        context.Applications.Add(application);
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(application).State = EntityState.Detached;
            throw;
        }
        return application;
    }
}
