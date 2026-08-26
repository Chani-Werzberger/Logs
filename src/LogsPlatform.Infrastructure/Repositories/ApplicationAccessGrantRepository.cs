using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ApplicationAccessGrantRepository : IApplicationAccessGrantRepository
{
    private readonly IDbContextFactory<LogsPlatformDbContext> _contextFactory;

    public ApplicationAccessGrantRepository(IDbContextFactory<LogsPlatformDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<bool> HasGrantAsync(int platformUserId, int applicationId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.PlatformUserApplicationGrants
            .AnyAsync(g => g.PlatformUserId == platformUserId && g.ApplicationId == applicationId);
    }

    public async Task<bool> HasAnyGrantAsync(int platformUserId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.PlatformUserApplicationGrants.AnyAsync(g => g.PlatformUserId == platformUserId);
    }

    public async Task<IReadOnlyList<int>> GetGrantedApplicationIdsAsync(int platformUserId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.PlatformUserApplicationGrants
            .Where(g => g.PlatformUserId == platformUserId)
            .Select(g => g.ApplicationId)
            .ToListAsync();
    }

    public async Task GrantAsync(int platformUserId, int applicationId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var exists = await context.PlatformUserApplicationGrants
            .AnyAsync(g => g.PlatformUserId == platformUserId && g.ApplicationId == applicationId);
        if (exists) return;

        context.PlatformUserApplicationGrants.Add(new PlatformUserApplicationGrant
        {
            PlatformUserId = platformUserId,
            ApplicationId = applicationId
        });
        await context.SaveChangesAsync();
    }

    public async Task RevokeAsync(int platformUserId, int applicationId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var grant = await context.PlatformUserApplicationGrants
            .SingleOrDefaultAsync(g => g.PlatformUserId == platformUserId && g.ApplicationId == applicationId);
        if (grant is null) return;

        context.PlatformUserApplicationGrants.Remove(grant);
        await context.SaveChangesAsync();
    }
}
