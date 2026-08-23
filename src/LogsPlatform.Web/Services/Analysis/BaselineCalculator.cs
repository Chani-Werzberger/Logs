using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class BaselineCalculator
{
    private const int BaselineLookbackDays = 28;
    private const int MinSamples = 14;

    private readonly IMetricsRepository _metrics;
    private readonly IBaselineRepository _baselines;

    public BaselineCalculator(IMetricsRepository metrics, IBaselineRepository baselines)
    {
        _metrics = metrics;
        _baselines = baselines;
    }

    public async Task RunAsync(int applicationId, int environmentId)
    {
        var operationIds = await _metrics.GetActiveOperationIdsAsync(applicationId, environmentId);
        foreach (var operationId in operationIds)
        {
            await ComputeAndSaveAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, AnalysisMetricType.EventCount,
                hourStart => _metrics.GetHourlyEventCountAsync(applicationId, environmentId, operationId, hourStart).ContinueWith(t => t.Result == 0 ? (double?)null : t.Result));
            await ComputeAndSaveAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, AnalysisMetricType.DurationMs,
                hourStart => _metrics.GetHourlyAverageDurationAsync(applicationId, environmentId, operationId, hourStart));
        }

        var exceptionGroupIds = await _metrics.GetActiveExceptionGroupIdsAsync(applicationId, environmentId);
        foreach (var exceptionGroupId in exceptionGroupIds)
        {
            await ComputeAndSaveAsync(applicationId, environmentId, AnalysisScopeType.ExceptionGroup, exceptionGroupId, AnalysisMetricType.ExceptionCount,
                hourStart => _metrics.GetHourlyExceptionCountAsync(applicationId, environmentId, exceptionGroupId, hourStart).ContinueWith(t => t.Result == 0 ? (double?)null : t.Result));
        }
    }

    private async Task ComputeAndSaveAsync(
        int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, AnalysisMetricType metricType,
        Func<DateTime, Task<double?>> sampleAt)
    {
        var now = DateTime.UtcNow;

        for (byte hour = 0; hour < 24; hour++)
        {
            var samples = new List<double>();

            for (var dayOffset = 1; dayOffset <= BaselineLookbackDays; dayOffset++)
            {
                var hourStart = now.Date.AddDays(-dayOffset).AddHours(hour);
                var value = await sampleAt(hourStart);
                if (value is not null)
                {
                    samples.Add(value.Value);
                }
            }

            if (samples.Count == 0)
            {
                continue;
            }

            var mean = samples.Average();
            var variance = samples.Count > 1 ? samples.Sum(v => (v - mean) * (v - mean)) / samples.Count : 0;
            var stdDev = Math.Sqrt(variance);

            await _baselines.UpsertAsync(new Baseline
            {
                ApplicationId = applicationId,
                EnvironmentId = environmentId,
                ScopeType = scopeType,
                ScopeId = scopeId,
                MetricType = metricType,
                BucketHourOfDay = hour,
                MeanValue = mean,
                StdDevValue = stdDev,
                SampleCount = samples.Count,
                LastUpdatedAt = now
            });
        }
    }
}
