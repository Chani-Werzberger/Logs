using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class RateAnomalyDetector
{
    private const double SpikeThreshold = 3;
    private const double MinStdDevFloor = 0.5;
    private const double MinMeaningfulActivity = 5;

    private readonly IMetricsRepository _metrics;
    private readonly IBaselineRepository _baselines;
    private readonly FindingWriter _writer;

    public RateAnomalyDetector(IMetricsRepository metrics, IBaselineRepository baselines, FindingWriter writer)
    {
        _metrics = metrics;
        _baselines = baselines;
        _writer = writer;
    }

    public async Task RunAsync(int applicationId, int environmentId)
    {
        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        var hour = (byte)currentHourStart.Hour;

        var operationIds = await _metrics.GetActiveOperationIdsAsync(applicationId, environmentId);
        foreach (var operationId in operationIds)
        {
            var eventCount = await _metrics.GetHourlyEventCountAsync(applicationId, environmentId, operationId, currentHourStart);
            await EvaluateAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, AnalysisMetricType.EventCount, hour,
                current: eventCount, positiveType: FindingType.ErrorSpike, negativeType: FindingType.MissingActivity,
                titlePrefix: $"Operation {operationId}");

            var averageDuration = await _metrics.GetHourlyAverageDurationAsync(applicationId, environmentId, operationId, currentHourStart);
            if (averageDuration is not null)
            {
                await EvaluateAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, AnalysisMetricType.DurationMs, hour,
                    current: averageDuration.Value, positiveType: FindingType.PerformanceDegradation, negativeType: null,
                    titlePrefix: $"Operation {operationId}");
            }
        }

        var exceptionGroupIds = await _metrics.GetActiveExceptionGroupIdsAsync(applicationId, environmentId);
        foreach (var exceptionGroupId in exceptionGroupIds)
        {
            var exceptionCount = await _metrics.GetHourlyExceptionCountAsync(applicationId, environmentId, exceptionGroupId, currentHourStart);
            await EvaluateAsync(applicationId, environmentId, AnalysisScopeType.ExceptionGroup, exceptionGroupId, AnalysisMetricType.ExceptionCount, hour,
                current: exceptionCount, positiveType: FindingType.ErrorSpike, negativeType: null,
                titlePrefix: $"ExceptionGroup {exceptionGroupId}");
        }
    }

    private async Task EvaluateAsync(
        int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, AnalysisMetricType metricType, byte hour,
        double current, FindingType positiveType, FindingType? negativeType, string titlePrefix)
    {
        var baseline = await _baselines.GetAsync(applicationId, environmentId, scopeType, scopeId, metricType, hour);
        if (baseline is null)
        {
            return;
        }

        var stdDev = Math.Max(baseline.StdDevValue, MinStdDevFloor);
        var z = (current - baseline.MeanValue) / stdDev;

        if (z > SpikeThreshold)
        {
            await WriteRateFindingAsync(applicationId, environmentId, scopeType, scopeId, positiveType, z, current, baseline, titlePrefix, "above");
        }
        else if (negativeType is not null && z < -SpikeThreshold && baseline.MeanValue > MinMeaningfulActivity)
        {
            await WriteRateFindingAsync(applicationId, environmentId, scopeType, scopeId, negativeType.Value, z, current, baseline, titlePrefix, "below");
        }
    }

    private async Task WriteRateFindingAsync(
        int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type,
        double z, double current, Baseline baseline, string titlePrefix, string direction)
    {
        var absZ = Math.Abs(z);
        var severity = absZ > 5 ? FindingSeverity.High : FindingSeverity.Medium;
        var confidence = absZ > 5 && baseline.SampleCount >= 14 ? ConfidenceLevel.High
            : baseline.SampleCount < 14 ? ConfidenceLevel.Low
            : ConfidenceLevel.Medium;

        var fact = $"{titlePrefix} recorded {current:F1} in the current hour.";
        var observation = $"That is {direction} the normal rate for this hour (baseline: {baseline.MeanValue:F1}±{baseline.StdDevValue:F1}, based on {baseline.SampleCount} days).";

        var draft = new FindingDraft(
            applicationId, environmentId, type, scopeType, scopeId,
            $"{titlePrefix}: {type}", severity, confidence,
            new[] { (DetectorStatementKind.Fact, fact), (DetectorStatementKind.Observation, observation) });

        var finding = await _writer.WriteAsync(draft);
    }
}
