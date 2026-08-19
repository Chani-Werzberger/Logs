using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class DeploymentRepository : IDeploymentRepository
{
    private readonly LogsPlatformDbContext _context;

    public DeploymentRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<Deployment?> GetByIdAsync(int id) =>
        await _context.Deployments.FindAsync(id);

    public async Task<IReadOnlyList<Deployment>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        var query = _context.Deployments.AsNoTracking().Where(d => d.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(d => d.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<Deployment> AddAsync(Deployment deployment)
    {
        _context.Deployments.Add(deployment);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(deployment).State = EntityState.Detached;
            throw;
        }
        return deployment;
    }

    public async Task<Deployment> RenameAsync(int id, string? notes)
    {
        var deployment = await _context.Deployments.FindAsync(id)
            ?? throw new InvalidOperationException($"Deployment {id} not found.");
        deployment.Notes = notes;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(deployment).State = EntityState.Detached;
            throw;
        }
        return deployment;
    }

    public async Task DeactivateAsync(int id)
    {
        var deployment = await _context.Deployments.FindAsync(id)
            ?? throw new InvalidOperationException($"Deployment {id} not found.");
        deployment.IsActive = false;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(deployment).State = EntityState.Detached;
            throw;
        }
    }
}
