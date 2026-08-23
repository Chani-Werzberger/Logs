using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IBaselineRepository
{
    Task UpsertAsync(Baseline baseline);
    Task<Baseline?> GetAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, AnalysisMetricType metricType, byte hourOfDay);
    Task<bool> HasUpdatedTodayAsync(int applicationId, int environmentId);
}
