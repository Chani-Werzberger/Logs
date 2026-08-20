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

    // Mirrors DbUpdateExceptionExtensions.IsUniqueViolation() (src/LogsPlatform.Web/DbUpdateExceptionExtensions.cs),
    // which isn't reachable from this layer: LogsPlatform.Infrastructure doesn't reference LogsPlatform.Web.
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };
}
