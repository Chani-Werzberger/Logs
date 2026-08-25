using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class DeploymentRepository : IDeploymentRepository
{
    private readonly IDbContextFactory<LogsPlatformDbContext> _contextFactory;

    public DeploymentRepository(IDbContextFactory<LogsPlatformDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<Deployment?> GetByIdAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Deployments.FindAsync(id);
    }

    public async Task<IReadOnlyList<Deployment>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Deployments.AsNoTracking().Where(d => d.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(d => d.IsActive);
        }
        return await query.OrderBy(d => d.DeployedAt).ThenBy(d => d.Id).ToListAsync();
    }

    public async Task<IReadOnlyList<Deployment>> GetInWindowAsync(int applicationId, int environmentId, DateTime windowStart, DateTime windowEnd)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Deployments.AsNoTracking()
            .Where(d => d.ApplicationId == applicationId && d.EnvironmentId == environmentId
                && d.DeployedAt >= windowStart && d.DeployedAt <= windowEnd)
            .OrderByDescending(d => d.DeployedAt)
            .ToListAsync();
    }

    public async Task<Deployment> AddAsync(Deployment deployment)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        context.Deployments.Add(deployment);
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(deployment).State = EntityState.Detached;
            throw;
        }
        return deployment;
    }

    public async Task<Deployment> RenameAsync(int id, string? notes)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var deployment = await context.Deployments.FindAsync(id)
            ?? throw new InvalidOperationException($"Deployment {id} not found.");
        deployment.Notes = notes;
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(deployment).State = EntityState.Detached;
            throw;
        }
        return deployment;
    }

    public async Task DeactivateAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var deployment = await context.Deployments.FindAsync(id)
            ?? throw new InvalidOperationException($"Deployment {id} not found.");
        deployment.IsActive = false;
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(deployment).State = EntityState.Detached;
            throw;
        }
    }
}
