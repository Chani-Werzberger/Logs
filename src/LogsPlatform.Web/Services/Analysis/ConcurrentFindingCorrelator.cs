using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class ConcurrentFindingCorrelator
{
    private readonly IFindingRepository _findings;

    public ConcurrentFindingCorrelator(IFindingRepository findings)
    {
        _findings = findings;
    }

    public async Task RunAsync(Finding finding)
    {
        var others = await _findings.GetOtherOpenFindingsForApplicationAsync(finding.ApplicationId, finding.Id);
        if (others.Count == 0)
        {
            return;
        }

        foreach (var other in others)
        {
            await _findings.AddEvidenceAsync(finding.Id, EvidenceType.Finding, other.Id, $"Finding #{other.Id} ({other.Type}) detected at {other.DetectedAt:u}");
        }

        var hypothesis = $"{others.Count} other Finding(s) are currently open on this Application. There may be a shared root cause, but this has not been confirmed.";

        await _findings.AddStatementAsync(finding.Id, DetectorStatementKind.Hypothesis, hypothesis);
    }
}
