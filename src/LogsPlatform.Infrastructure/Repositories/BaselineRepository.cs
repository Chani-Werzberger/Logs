using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class BaselineRepository : IBaselineRepository
{
    private readonly LogsPlatformDbContext _context;

    public BaselineRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task UpsertAsync(Baseline baseline)
    {
        var existing = await _context.Baselines.FirstOrDefaultAsync(b =>
            b.ApplicationId == baseline.ApplicationId && b.EnvironmentId == baseline.EnvironmentId &&
            b.ScopeType == baseline.ScopeType && b.ScopeId == baseline.ScopeId &&
            b.MetricType == baseline.MetricType && b.BucketHourOfDay == baseline.BucketHourOfDay);

        if (existing is null)
        {
            _context.Baselines.Add(baseline);
        }
        else
        {
            existing.MeanValue = baseline.MeanValue;
            existing.StdDevValue = baseline.StdDevValue;
            existing.SampleCount = baseline.SampleCount;
            existing.LastUpdatedAt = baseline.LastUpdatedAt;
        }

        await _context.SaveChangesAsync();
    }

    public async Task<Baseline?> GetAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, AnalysisMetricType metricType, byte hourOfDay) =>
        await _context.Baselines.AsNoTracking().FirstOrDefaultAsync(b =>
            b.ApplicationId == applicationId && b.EnvironmentId == environmentId &&
            b.ScopeType == scopeType && b.ScopeId == scopeId &&
            b.MetricType == metricType && b.BucketHourOfDay == hourOfDay);

    public async Task<bool> HasUpdatedTodayAsync(int applicationId, int environmentId)
    {
        var todayStart = DateTime.UtcNow.Date;
        return await _context.Baselines.AsNoTracking().AnyAsync(b =>
            b.ApplicationId == applicationId && b.EnvironmentId == environmentId && b.LastUpdatedAt >= todayStart);
    }
}
