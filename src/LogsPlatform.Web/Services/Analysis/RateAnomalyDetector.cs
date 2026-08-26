using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Services.Analysis;

public class RateAnomalyDetector
{
    private const double SpikeThreshold = 3;
    private const double MinStdDevFloor = 0.5;
    private const double MinMeaningfulActivity = 5;

    private readonly IMetricsRepository _metrics;
    private readonly IBaselineRepository _baselines;
    private readonly FindingWriter _writer;
    private readonly DownstreamFailureCorrelator _downstreamCorrelator;
    private readonly UpstreamCauseCorrelator _upstreamCorrelator;
    private readonly LogsPlatformDbContext _context;

    public RateAnomalyDetector(IMetricsRepository metrics, IBaselineRepository baselines, FindingWriter writer, DownstreamFailureCorrelator downstreamCorrelator, UpstreamCauseCorrelator upstreamCorrelator, LogsPlatformDbContext context)
    {
        _metrics = metrics;
        _baselines = baselines;
        _writer = writer;
        _downstreamCorrelator = downstreamCorrelator;
        _upstreamCorrelator = upstreamCorrelator;
        _context = context;
    }

    public async Task RunAsync(int applicationId, int environmentId)
    {
        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        var hour = (byte)currentHourStart.Hour;

        var operationIds = await _metrics.GetActiveOperationIdsAsync(applicationId, environmentId);
        foreach (var operationId in operationIds)
        {
            var eventCount = await _metrics.GetHourlyEventCountAsync(applicationId, environmentId, operationId, currentHourStart);
            await EvaluateAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, AnalysisMetricType.EventCount, hour, currentHourStart,
                current: eventCount, positiveType: FindingType.ErrorSpike, negativeType: FindingType.MissingActivity,
                titlePrefix: $"Operation {operationId}");

            var averageDuration = await _metrics.GetHourlyAverageDurationAsync(applicationId, environmentId, operationId, currentHourStart);
            if (averageDuration is not null)
            {
                await EvaluateAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, AnalysisMetricType.DurationMs, hour, currentHourStart,
                    current: averageDuration.Value, positiveType: FindingType.PerformanceDegradation, negativeType: null,
                    titlePrefix: $"Operation {operationId}");
            }
        }

        var exceptionGroupIds = await _metrics.GetActiveExceptionGroupIdsAsync(applicationId, environmentId);
        foreach (var exceptionGroupId in exceptionGroupIds)
        {
            var exceptionCount = await _metrics.GetHourlyExceptionCountAsync(applicationId, environmentId, exceptionGroupId, currentHourStart);
            await EvaluateAsync(applicationId, environmentId, AnalysisScopeType.ExceptionGroup, exceptionGroupId, AnalysisMetricType.ExceptionCount, hour, currentHourStart,
                current: exceptionCount, positiveType: FindingType.ErrorSpike, negativeType: null,
                titlePrefix: $"ExceptionGroup {exceptionGroupId}");
        }
    }

    private async Task EvaluateAsync(
        int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, AnalysisMetricType metricType, byte hour, DateTime hourStart,
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
            await WriteRateFindingAsync(applicationId, environmentId, scopeType, scopeId, positiveType, z, current, baseline, titlePrefix, "above", hourStart);
        }
        else if (negativeType is not null && z < -SpikeThreshold && baseline.MeanValue > MinMeaningfulActivity)
        {
            await WriteRateFindingAsync(applicationId, environmentId, scopeType, scopeId, negativeType.Value, z, current, baseline, titlePrefix, "below", hourStart);
        }
    }

    private async Task WriteRateFindingAsync(
        int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type,
        double z, double current, Baseline baseline, string titlePrefix, string direction, DateTime hourStart)
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

        if (type == FindingType.ErrorSpike && scopeType == AnalysisScopeType.Operation)
        {
            var operationId = (int)scopeId;
            var hourEnd = hourStart.AddHours(1);
            var triggerEvent = await _context.Events.AsNoTracking()
                .Where(e => e.ApplicationId == applicationId && e.EnvironmentId == environmentId
                    && e.OperationId == operationId && e.Timestamp >= hourStart && e.Timestamp < hourEnd && e.CorrelationId != null)
                .OrderByDescending(e => e.Timestamp)
                .FirstOrDefaultAsync();

            if (triggerEvent is not null)
            {
                await _downstreamCorrelator.RunAsync(finding, triggerEvent.CorrelationId!, operationId, triggerEvent.Timestamp);
                await _upstreamCorrelator.RunAsync(finding, triggerEvent.CorrelationId!, operationId, triggerEvent.Timestamp);
            }
        }
    }
}
