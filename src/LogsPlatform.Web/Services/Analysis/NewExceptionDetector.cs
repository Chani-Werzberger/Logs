using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Services.Analysis;

public class NewExceptionDetector
{
    private static readonly TimeSpan DetectionWindow = TimeSpan.FromMinutes(5);

    private readonly LogsPlatformDbContext _context;
    private readonly FindingWriter _writer;
    private readonly DownstreamFailureCorrelator _downstreamCorrelator;
    private readonly UpstreamCauseCorrelator _upstreamCorrelator;

    public NewExceptionDetector(LogsPlatformDbContext context, FindingWriter writer, DownstreamFailureCorrelator downstreamCorrelator, UpstreamCauseCorrelator upstreamCorrelator)
    {
        _context = context;
        _writer = writer;
        _downstreamCorrelator = downstreamCorrelator;
        _upstreamCorrelator = upstreamCorrelator;
    }

    public async Task RunAsync(int applicationId, int environmentId)
    {
        var windowStart = DateTime.UtcNow - DetectionWindow;

        var newGroups = await _context.ExceptionGroups.AsNoTracking()
            .Where(g => g.ApplicationId == applicationId && g.FirstSeenAt >= windowStart)
            .ToListAsync();

        foreach (var group in newGroups)
        {
            var events = await _context.Events.AsNoTracking()
                .Where(e => e.ExceptionGroupId == group.Id && e.EnvironmentId == environmentId)
                .OrderBy(e => e.Timestamp)
                .ToListAsync();

            var environmentIds = events.Select(e => e.EnvironmentId).Distinct();

            foreach (var envId in environmentIds)
            {
                var draft = new FindingDraft(
                    applicationId, envId, FindingType.NewException, AnalysisScopeType.ExceptionGroup, group.Id,
                    $"New exception: {group.ExceptionType}", FindingSeverity.High, ConfidenceLevel.High,
                    new[] { (DetectorStatementKind.Fact, $"This exception type ({group.ExceptionType}) has never been seen before. First occurrence at {group.FirstSeenAt:u}.") });

                var finding = await _writer.WriteAsync(draft);

                var triggerEvent = events.First(e => e.EnvironmentId == envId);
                if (triggerEvent.CorrelationId is not null && triggerEvent.OperationId is not null)
                {
                    await _downstreamCorrelator.RunAsync(finding, triggerEvent.CorrelationId, triggerEvent.OperationId.Value, triggerEvent.Timestamp);
                    await _upstreamCorrelator.RunAsync(finding, triggerEvent.CorrelationId, triggerEvent.OperationId.Value, triggerEvent.Timestamp);
                }
            }
        }
    }
}
