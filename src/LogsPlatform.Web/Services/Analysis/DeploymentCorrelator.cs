using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class DeploymentCorrelator
{
    private static readonly TimeSpan CorrelationWindow = TimeSpan.FromMinutes(60);

    private readonly IFindingRepository _findings;
    private readonly IDeploymentRepository _deployments;

    public DeploymentCorrelator(IFindingRepository findings, IDeploymentRepository deployments)
    {
        _findings = findings;
        _deployments = deployments;
    }

    public async Task RunAsync(Finding finding)
    {
        if (finding.Type is not (FindingType.ErrorSpike or FindingType.PerformanceDegradation or FindingType.NewException))
        {
            return;
        }

        var windowStart = finding.DetectedAt - CorrelationWindow;
        var deployments = await _deployments.GetInWindowAsync(finding.ApplicationId, finding.EnvironmentId, windowStart, finding.DetectedAt);
        var deployment = deployments.FirstOrDefault();
        if (deployment is null)
        {
            return;
        }

        var minutesBefore = (finding.DetectedAt - deployment.DeployedAt).TotalMinutes;
        var hypothesis = $"A deployment was installed at {deployment.DeployedAt:u}, {minutesBefore:F0} minutes before this anomaly started. There may be a connection, but it has not been confirmed.";

        await _findings.AddEvidenceAsync(finding.Id, EvidenceType.Deployment, deployment.Id, $"Deployment #{deployment.Id} at {deployment.DeployedAt:u}");
        await _findings.AddStatementAsync(finding.Id, DetectorStatementKind.Hypothesis, hypothesis);
    }
}
