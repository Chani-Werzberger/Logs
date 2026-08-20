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

        var (toInsert, duplicateCount) = await PartitionByExistingKeysAsync(applicationId, events);

        if (toInsert.Count > 0)
        {
            _context.Events.AddRange(toInsert);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // A concurrent request won the race for one or more EventKeys between our
                // existence check above and this insert (check-then-insert TOCTOU). Detach the
                // failed batch and retry exactly once against a fresh existence check, which will
                // now see whatever the concurrent request committed.
                foreach (var entity in toInsert)
                {
                    _context.Entry(entity).State = EntityState.Detached;
                }

                var (retryInsert, retryDuplicateCount) = await PartitionByExistingKeysAsync(applicationId, toInsert);
                duplicateCount += retryDuplicateCount;

                if (retryInsert.Count > 0)
                {
                    _context.Events.AddRange(retryInsert);
                    try
                    {
                        await _context.SaveChangesAsync();
                    }
                    catch
                    {
                        foreach (var entity in retryInsert)
                        {
                            _context.Entry(entity).State = EntityState.Detached;
                        }
                        throw;
                    }
                }

                return new IngestResult(retryInsert.Count, duplicateCount);
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

    private async Task<(List<Event> ToInsert, int DuplicateCount)> PartitionByExistingKeysAsync(int applicationId, IReadOnlyList<Event> events)
    {
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

        return (toInsert, duplicateCount);
    }

    // Mirrors DbUpdateExceptionExtensions.IsUniqueViolation() (src/LogsPlatform.Web/DbUpdateExceptionExtensions.cs),
    // which isn't reachable from this layer: LogsPlatform.Infrastructure doesn't reference LogsPlatform.Web.
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };
}
