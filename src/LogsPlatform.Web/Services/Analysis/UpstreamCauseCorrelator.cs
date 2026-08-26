using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Services.Analysis;

public class UpstreamCauseCorrelator
{
    private const int ErrorSeverityFloor = 17; // matches SeverityLevels.ByName["Error"]

    private readonly IFindingRepository _findings;
    private readonly LogsPlatformDbContext _context;

    public UpstreamCauseCorrelator(IFindingRepository findings, LogsPlatformDbContext context)
    {
        _findings = findings;
        _context = context;
    }

    public async Task RunAsync(Finding finding, string correlationId, int triggeringOperationId, DateTime triggerTimestamp)
    {
        if (finding.Type is not (FindingType.NewException or FindingType.ErrorSpike))
        {
            return;
        }

        var relatedEvents = await _context.Events.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId && e.Timestamp < triggerTimestamp
                && e.OperationId != triggeringOperationId && e.Severity >= ErrorSeverityFloor)
            .ToListAsync();

        if (relatedEvents.Count == 0)
        {
            return;
        }

        foreach (var relatedEvent in relatedEvents)
        {
            await _findings.AddEvidenceAsync(finding.Id, EvidenceType.Event, relatedEvent.Id, $"Event #{relatedEvent.Id} at {relatedEvent.Timestamp:u}");
        }

        var operationIds = relatedEvents.Select(e => e.OperationId).Distinct().Count();
        var hypothesis = $"Before this event, {relatedEvents.Count} earlier error(s) were recorded in the same correlation chain, across {operationIds} other operation(s). This may be an upstream cause of this event, but it has not been confirmed.";

        await _findings.AddStatementAsync(finding.Id, DetectorStatementKind.Hypothesis, hypothesis);
    }
}
