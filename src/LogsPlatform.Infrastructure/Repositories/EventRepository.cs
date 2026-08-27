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

    private const int MaxPageSize = 200;

    public async Task<(IReadOnlyList<Event> Items, int TotalCount)> QueryAsync(EventQueryParameters parameters)
    {
        var query = _context.Events.AsNoTracking()
            .Where(e => e.ApplicationId == parameters.ApplicationId && e.EnvironmentId == parameters.EnvironmentId);

        if (parameters.From is not null) query = query.Where(e => e.Timestamp >= parameters.From);
        if (parameters.To is not null) query = query.Where(e => e.Timestamp <= parameters.To);
        if (parameters.Severity is not null) query = query.Where(e => e.Severity == parameters.Severity);
        if (parameters.ModuleId is not null) query = query.Where(e => e.ModuleId == parameters.ModuleId);
        if (parameters.ScreenServiceId is not null) query = query.Where(e => e.ScreenServiceId == parameters.ScreenServiceId);
        if (parameters.ProcessId is not null) query = query.Where(e => e.ProcessId == parameters.ProcessId);
        if (parameters.OperationId is not null) query = query.Where(e => e.OperationId == parameters.OperationId);
        if (parameters.CorrelationId is not null) query = query.Where(e => e.CorrelationId == parameters.CorrelationId);
        if (parameters.TraceId is not null) query = query.Where(e => e.TraceId == parameters.TraceId);
        if (int.TryParse(parameters.UserId, out var userId)) query = query.Where(e => e.AppUserId == userId);
        if (int.TryParse(parameters.CustomerId, out var customerId)) query = query.Where(e => e.CustomerId == customerId);
        if (parameters.ExceptionGroupId is not null) query = query.Where(e => e.ExceptionGroupId == parameters.ExceptionGroupId);
        if (parameters.VersionId is not null) query = query.Where(e => e.VersionId == parameters.VersionId);
        if (parameters.DurationMinMs is not null) query = query.Where(e => e.DurationMs >= parameters.DurationMinMs);
        if (parameters.DurationMaxMs is not null) query = query.Where(e => e.DurationMs <= parameters.DurationMaxMs);
        if (!string.IsNullOrWhiteSpace(parameters.MessageContains)) query = query.Where(e => e.Message.Contains(parameters.MessageContains));

        var totalCount = await query.CountAsync();

        var pageSize = Math.Clamp(parameters.PageSize, 1, MaxPageSize);
        var page = Math.Max(parameters.Page, 1);

        var items = await query
            .OrderByDescending(e => e.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(e => e.Module)
            .Include(e => e.ScreenService)
            .Include(e => e.Process)
            .Include(e => e.Operation)
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task<Event?> GetByIdAsync(int applicationId, long id)
    {
        var evt = await _context.Events.AsNoTracking()
            .Include(e => e.Module)
            .Include(e => e.ScreenService)
            .Include(e => e.Process)
            .Include(e => e.Operation)
            .FirstOrDefaultAsync(e => e.Id == id);

        return evt is null || evt.ApplicationId != applicationId ? null : evt;
    }

    public async Task<IReadOnlyList<Event>> GetTimelineAsync(TimelineQuery query)
    {
        var events = _context.Events.AsNoTracking().Where(e => e.ApplicationId == query.ApplicationId);

        if (query.CorrelationId is not null)
        {
            events = events.Where(e => e.CorrelationId == query.CorrelationId);
        }
        else if (query.TraceId is not null)
        {
            events = events.Where(e => e.TraceId == query.TraceId);
        }
        else if (query.OperationId is not null && int.TryParse(query.UserId, out var userId))
        {
            events = events.Where(e => e.OperationId == query.OperationId && e.AppUserId == userId);
        }
        else if (int.TryParse(query.CustomerId, out var customerId))
        {
            events = events.Where(e => e.CustomerId == customerId);
        }
        else
        {
            return Array.Empty<Event>();
        }

        return await events
            .OrderBy(e => e.Timestamp)
            .Include(e => e.Module)
            .Include(e => e.ScreenService)
            .Include(e => e.Process)
            .Include(e => e.Operation)
            .ToListAsync();
    }

    public async Task<int> DeleteOlderThanAsync(int applicationId, DateTime cutoffUtc) =>
        await _context.Events
            .Where(e => e.ApplicationId == applicationId && e.Timestamp < cutoffUtc)
            .ExecuteDeleteAsync();
}
