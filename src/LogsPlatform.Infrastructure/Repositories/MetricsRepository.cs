using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class MetricsRepository : IMetricsRepository
{
    private const int ActiveLookbackDays = 28;

    private readonly LogsPlatformDbContext _context;

    public MetricsRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<int> GetHourlyEventCountAsync(int applicationId, int environmentId, int operationId, DateTime hourStart)
    {
        var hourEnd = hourStart.AddHours(1);
        return await _context.Events.AsNoTracking()
            .CountAsync(e => e.ApplicationId == applicationId && e.EnvironmentId == environmentId
                && e.OperationId == operationId && e.Timestamp >= hourStart && e.Timestamp < hourEnd);
    }

    public async Task<double?> GetHourlyAverageDurationAsync(int applicationId, int environmentId, int operationId, DateTime hourStart)
    {
        var hourEnd = hourStart.AddHours(1);
        var durations = await _context.Events.AsNoTracking()
            .Where(e => e.ApplicationId == applicationId && e.EnvironmentId == environmentId
                && e.OperationId == operationId && e.Timestamp >= hourStart && e.Timestamp < hourEnd
                && e.DurationMs != null)
            .Select(e => e.DurationMs!.Value)
            .ToListAsync();

        return durations.Count == 0 ? null : durations.Average();
    }

    public async Task<int> GetHourlyExceptionCountAsync(int applicationId, int environmentId, long exceptionGroupId, DateTime hourStart)
    {
        var hourEnd = hourStart.AddHours(1);
        return await _context.Events.AsNoTracking()
            .CountAsync(e => e.ApplicationId == applicationId && e.EnvironmentId == environmentId
                && e.ExceptionGroupId == exceptionGroupId && e.Timestamp >= hourStart && e.Timestamp < hourEnd);
    }

    public async Task<IReadOnlyList<int>> GetActiveOperationIdsAsync(int applicationId, int environmentId)
    {
        var since = DateTime.UtcNow.AddDays(-ActiveLookbackDays);
        return await _context.Events.AsNoTracking()
            .Where(e => e.ApplicationId == applicationId && e.EnvironmentId == environmentId
                && e.OperationId != null && e.Timestamp >= since)
            .Select(e => e.OperationId!.Value)
            .Distinct()
            .ToListAsync();
    }

    public async Task<IReadOnlyList<long>> GetActiveExceptionGroupIdsAsync(int applicationId, int environmentId)
    {
        var since = DateTime.UtcNow.AddDays(-ActiveLookbackDays);
        return await _context.Events.AsNoTracking()
            .Where(e => e.ApplicationId == applicationId && e.EnvironmentId == environmentId
                && e.ExceptionGroupId != null && e.Timestamp >= since)
            .Select(e => e.ExceptionGroupId!.Value)
            .Distinct()
            .ToListAsync();
    }

    public async Task<IReadOnlyDictionary<int, double>> GetCustomerRatesAsync(
        int applicationId, int environmentId, int? operationId, long? exceptionGroupId, DateTime windowStart)
    {
        var query = _context.Events.AsNoTracking()
            .Where(e => e.ApplicationId == applicationId && e.EnvironmentId == environmentId
                && e.CustomerId != null && e.Timestamp >= windowStart);

        if (operationId is not null) query = query.Where(e => e.OperationId == operationId);
        if (exceptionGroupId is not null) query = query.Where(e => e.ExceptionGroupId == exceptionGroupId);

        var counts = await query
            .GroupBy(e => e.CustomerId!.Value)
            .Select(g => new { CustomerId = g.Key, Count = g.Count() })
            .ToListAsync();

        return counts.ToDictionary(c => c.CustomerId, c => (double)c.Count);
    }
}
