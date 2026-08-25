using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ExceptionGroupRepository : IExceptionGroupRepository
{
    private readonly LogsPlatformDbContext _context;

    public ExceptionGroupRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<ExceptionGroup> GetOrCreateAsync(
        int applicationId, string fingerprint, string exceptionType,
        string messageTemplate, string representativeStackTrace, DateTime seenAt)
    {
        var existing = await _context.ExceptionGroups
            .FirstOrDefaultAsync(g => g.ApplicationId == applicationId && g.Fingerprint == fingerprint);
        if (existing is not null)
        {
            await RecordOccurrenceAsync(existing, seenAt);
            return existing;
        }

        var group = new ExceptionGroup
        {
            ApplicationId = applicationId,
            Fingerprint = fingerprint,
            ExceptionType = exceptionType,
            MessageTemplate = messageTemplate,
            RepresentativeStackTrace = representativeStackTrace,
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
            OccurrenceCount = 1
        };

        _context.ExceptionGroups.Add(group);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent request won the race for this (applicationId, fingerprint) pair
            // between our existence check above and this insert (check-then-insert TOCTOU).
            // Detach the failed entity and re-query once - the concurrent request's row should
            // now be visible. If it still isn't, propagate the original exception.
            _context.Entry(group).State = EntityState.Detached;

            var winner = await _context.ExceptionGroups
                .FirstOrDefaultAsync(g => g.ApplicationId == applicationId && g.Fingerprint == fingerprint);
            if (winner is not null)
            {
                await RecordOccurrenceAsync(winner, seenAt);
                return winner;
            }
            throw;
        }
        catch
        {
            _context.Entry(group).State = EntityState.Detached;
            throw;
        }
        return group;
    }

    // Called whenever an event matches an ExceptionGroup that already exists (whether found on the
    // first check or after losing a concurrent-insert race) - every such call represents a real,
    // distinct exception occurrence that must be reflected in OccurrenceCount/LastSeenAt, not just
    // the group's original creation.
    private async Task RecordOccurrenceAsync(ExceptionGroup group, DateTime seenAt)
    {
        group.OccurrenceCount++;
        group.LastSeenAt = seenAt;
        await _context.SaveChangesAsync();
    }

    // Mirrors DbUpdateExceptionExtensions.IsUniqueViolation() (src/LogsPlatform.Web/DbUpdateExceptionExtensions.cs),
    // which isn't reachable from this layer: LogsPlatform.Infrastructure doesn't reference LogsPlatform.Web.
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };

    public async Task<IReadOnlyList<ExceptionGroup>> QueryAsync(ExceptionGroupQueryParameters parameters)
    {
        var query = _context.ExceptionGroups.AsNoTracking()
            .Where(g => g.ApplicationId == parameters.ApplicationId);

        if (parameters.From is not null) query = query.Where(g => g.LastSeenAt >= parameters.From);
        if (parameters.To is not null) query = query.Where(g => g.LastSeenAt <= parameters.To);

        query = parameters.SortBy == "OccurrenceCount"
            ? query.OrderByDescending(g => g.OccurrenceCount)
            : query.OrderByDescending(g => g.LastSeenAt);

        return await query.ToListAsync();
    }

    public async Task<ExceptionGroup?> GetByIdAsync(long id) =>
        await _context.ExceptionGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id);

    public async Task<IReadOnlyDictionary<DateOnly, int>> GetDailyCountsAsync(long exceptionGroupId, int days)
    {
        var since = DateTime.UtcNow.Date.AddDays(-(days - 1));

        var rows = await _context.Events.AsNoTracking()
            .Where(e => e.ExceptionGroupId == exceptionGroupId && e.Timestamp >= since)
            .Select(e => e.Timestamp)
            .ToListAsync();

        return rows
            .GroupBy(timestamp => DateOnly.FromDateTime(timestamp.Date))
            .ToDictionary(group => group.Key, group => group.Count());
    }

    public async Task<IReadOnlyList<AffectedContext>> GetAffectedContextsAsync(long exceptionGroupId)
    {
        var rows = await _context.Events.AsNoTracking()
            .Where(e => e.ExceptionGroupId == exceptionGroupId)
            .Select(e => new
            {
                ApplicationName = e.Application.Name,
                EnvironmentName = e.Environment.Name,
                VersionNumber = e.Version != null ? e.Version.VersionNumber : null,
                OperationName = e.Operation != null ? e.Operation.Name : null
            })
            .Distinct()
            .ToListAsync();

        return rows.Select(r => new AffectedContext(r.ApplicationName, r.EnvironmentName, r.VersionNumber, r.OperationName)).ToList();
    }
}
