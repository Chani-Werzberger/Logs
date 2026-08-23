using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class CustomerOutlierDetector
{
    private const int MinPeerCustomers = 5;
    private const double CustomerOutlierThreshold = 3;
    private const double MinStdDevFloor = 0.5;
    private const int MinSamplesForHighConfidence = 14;
    private static readonly TimeSpan Window = TimeSpan.FromDays(1);

    private readonly IMetricsRepository _metrics;
    private readonly FindingWriter _writer;

    public CustomerOutlierDetector(IMetricsRepository metrics, FindingWriter writer)
    {
        _metrics = metrics;
        _writer = writer;
    }

    public async Task RunAsync(int applicationId, int environmentId)
    {
        var windowStart = DateTime.UtcNow - Window;

        var operationIds = await _metrics.GetActiveOperationIdsAsync(applicationId, environmentId);
        foreach (var operationId in operationIds)
        {
            var rates = await _metrics.GetCustomerRatesAsync(applicationId, environmentId, operationId, null, windowStart);
            await EvaluatePeersAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, rates);
        }

        var exceptionGroupIds = await _metrics.GetActiveExceptionGroupIdsAsync(applicationId, environmentId);
        foreach (var exceptionGroupId in exceptionGroupIds)
        {
            var rates = await _metrics.GetCustomerRatesAsync(applicationId, environmentId, null, exceptionGroupId, windowStart);
            await EvaluatePeersAsync(applicationId, environmentId, AnalysisScopeType.ExceptionGroup, exceptionGroupId, rates);
        }
    }

    private async Task EvaluatePeersAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, IReadOnlyDictionary<int, double> rates)
    {
        if (rates.Count < MinPeerCustomers)
        {
            return;
        }

        foreach (var (customerId, rate) in rates)
        {
            var peerRates = rates.Where(r => r.Key != customerId).Select(r => r.Value).ToList();
            var populationMean = peerRates.Average();
            var variance = peerRates.Count > 1
                ? peerRates.Sum(v => (v - populationMean) * (v - populationMean)) / peerRates.Count
                : 0;
            var populationStdDev = Math.Sqrt(variance);
            var stdDev = Math.Max(populationStdDev, MinStdDevFloor);
            var z = (rate - populationMean) / stdDev;

            if (Math.Abs(z) > CustomerOutlierThreshold)
            {
                var absZ = Math.Abs(z);
                var severity = absZ > 5 ? FindingSeverity.High : FindingSeverity.Medium;
                var confidence = absZ > 5 && rates.Count >= MinSamplesForHighConfidence ? ConfidenceLevel.High
                    : ConfidenceLevel.Medium;

                var fact = $"Customer {customerId} recorded a rate of {rate:F1} in the last 24 hours.";
                var observation = $"That is {absZ:F1} standard deviations from its {peerRates.Count} peers (peer average: {populationMean:F1}±{populationStdDev:F1}).";

                var draft = new FindingDraft(
                    applicationId, environmentId, FindingType.CustomerAnomaly, scopeType, scopeId,
                    $"Customer {customerId}: unusual activity", severity, confidence,
                    new[] { (DetectorStatementKind.Fact, fact), (DetectorStatementKind.Observation, observation) });

                await _writer.WriteAsync(draft);
            }
        }
    }
}
