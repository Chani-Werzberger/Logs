using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class AuditLogRepository : IAuditLogRepository
{
    private readonly IDbContextFactory<LogsPlatformDbContext> _contextFactory;

    public AuditLogRepository(IDbContextFactory<LogsPlatformDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<AdminAuditLogEntry> AddAsync(AdminAuditLogEntry entry)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        context.AdminAuditLogEntries.Add(entry);
        await context.SaveChangesAsync();
        return entry;
    }

    public async Task<(IReadOnlyList<AdminAuditLogEntry> Items, int TotalCount)> QueryAsync(AuditLogQueryParameters parameters)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.AdminAuditLogEntries.AsNoTracking().Include(e => e.PlatformUser).AsQueryable();

        if (parameters.PlatformUserId is not null)
        {
            query = query.Where(e => e.PlatformUserId == parameters.PlatformUserId);
        }
        if (parameters.EntityType is not null)
        {
            query = query.Where(e => e.EntityType == parameters.EntityType);
        }
        if (parameters.Action is not null)
        {
            query = query.Where(e => e.Action == parameters.Action);
        }
        if (parameters.From is not null)
        {
            query = query.Where(e => e.Timestamp >= parameters.From);
        }
        if (parameters.To is not null)
        {
            query = query.Where(e => e.Timestamp <= parameters.To);
        }

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id)
            .Skip((parameters.Page - 1) * parameters.PageSize)
            .Take(parameters.PageSize)
            .ToListAsync();

        return (items, totalCount);
    }
}
