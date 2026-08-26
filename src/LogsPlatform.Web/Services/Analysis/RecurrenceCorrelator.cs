using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class RecurrenceCorrelator
{
    private readonly IFindingRepository _findings;

    public RecurrenceCorrelator(IFindingRepository findings)
    {
        _findings = findings;
    }

    public async Task RunAsync(Finding finding)
    {
        var prior = await _findings.FindMostRecentClosedAsync(
            finding.ApplicationId, finding.EnvironmentId, finding.ScopeType, finding.ScopeId, finding.Type, finding.Id);
        if (prior is null)
        {
            return;
        }

        await _findings.AddEvidenceAsync(finding.Id, EvidenceType.Finding, prior.Id, $"Finding #{prior.Id} ({prior.Status}) detected at {prior.DetectedAt:u}");

        var hypothesis = $"This appears to be a recurrence of a previously {prior.Status.ToString().ToLowerInvariant()} issue detected at {prior.DetectedAt:u}. It has not been confirmed to be the same root cause.";
        await _findings.AddStatementAsync(finding.Id, DetectorStatementKind.Hypothesis, hypothesis);
    }
}
