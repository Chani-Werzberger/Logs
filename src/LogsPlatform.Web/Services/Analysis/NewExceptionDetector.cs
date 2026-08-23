using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Services.Analysis;

public class NewExceptionDetector
{
    private static readonly TimeSpan DetectionWindow = TimeSpan.FromMinutes(5);

    private readonly LogsPlatformDbContext _context;
    private readonly FindingWriter _writer;

    public NewExceptionDetector(LogsPlatformDbContext context, FindingWriter writer)
    {
        _context = context;
        _writer = writer;
    }

    public async Task RunAsync(int applicationId, int environmentId)
    {
        var windowStart = DateTime.UtcNow - DetectionWindow;

        var newGroups = await _context.ExceptionGroups.AsNoTracking()
            .Where(g => g.ApplicationId == applicationId && g.FirstSeenAt >= windowStart)
            .ToListAsync();

        foreach (var group in newGroups)
        {
            var environmentIds = await _context.Events.AsNoTracking()
                .Where(e => e.ExceptionGroupId == group.Id && e.EnvironmentId == environmentId)
                .Select(e => e.EnvironmentId)
                .Distinct()
                .ToListAsync();

            foreach (var envId in environmentIds)
            {
                var draft = new FindingDraft(
                    applicationId, envId, FindingType.NewException, AnalysisScopeType.ExceptionGroup, group.Id,
                    $"New exception: {group.ExceptionType}", FindingSeverity.High, ConfidenceLevel.High,
                    new[] { (DetectorStatementKind.Fact, $"This exception type ({group.ExceptionType}) has never been seen before. First occurrence at {group.FirstSeenAt:u}.") });

                await _writer.WriteAsync(draft);
            }
        }
    }
}
