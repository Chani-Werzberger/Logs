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
        catch
        {
            _context.Entry(group).State = EntityState.Detached;
            throw;
        }
        return group;
    }
}
