using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class EventRepository : IEventRepository
{
    private readonly LogsPlatformDbContext _context;

    public EventRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<IngestResult> AddEventsAsync(int applicationId, IReadOnlyList<Event> events)
    {
        if (events.Count == 0)
        {
            return new IngestResult(0, 0);
        }

        var requestedKeys = events.Where(e => e.EventKey is not null).Select(e => e.EventKey!).Distinct().ToList();
        var existingKeys = requestedKeys.Count == 0
            ? new HashSet<string>()
            : (await _context.Events.AsNoTracking()
                .Where(e => e.ApplicationId == applicationId && e.EventKey != null && requestedKeys.Contains(e.EventKey!))
                .Select(e => e.EventKey!)
                .ToListAsync())
                .ToHashSet();

        var seenInBatch = new HashSet<string>();
        var toInsert = new List<Event>();
        var duplicateCount = 0;

        foreach (var evt in events)
        {
            if (evt.EventKey is not null && (existingKeys.Contains(evt.EventKey) || !seenInBatch.Add(evt.EventKey)))
            {
                duplicateCount++;
                continue;
            }
            toInsert.Add(evt);
        }

        if (toInsert.Count > 0)
        {
            _context.Events.AddRange(toInsert);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch
            {
                foreach (var entity in toInsert)
                {
                    _context.Entry(entity).State = EntityState.Detached;
                }
                throw;
            }
        }

        return new IngestResult(toInsert.Count, duplicateCount);
    }
}
