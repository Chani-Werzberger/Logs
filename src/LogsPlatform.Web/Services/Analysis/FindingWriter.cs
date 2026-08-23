using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public record FindingDraft(
    int ApplicationId,
    int EnvironmentId,
    FindingType Type,
    AnalysisScopeType ScopeType,
    long ScopeId,
    string Title,
    FindingSeverity Severity,
    ConfidenceLevel ConfidenceLevel,
    IReadOnlyList<(DetectorStatementKind Kind, string Text)> Statements);

public class FindingWriter
{
    private static readonly TimeSpan CooldownWindow = TimeSpan.FromHours(24);

    private readonly IFindingRepository _findings;

    public FindingWriter(IFindingRepository findings)
    {
        _findings = findings;
    }

    public async Task<Finding> WriteAsync(FindingDraft draft)
    {
        var existing = await _findings.FindOpenAsync(
            draft.ApplicationId, draft.EnvironmentId, draft.ScopeType, draft.ScopeId, draft.Type,
            cooldownSince: DateTime.UtcNow - CooldownWindow);

        Finding finding;
        if (existing is not null)
        {
            finding = existing;
        }
        else
        {
            finding = await _findings.AddAsync(new Finding
            {
                ApplicationId = draft.ApplicationId,
                EnvironmentId = draft.EnvironmentId,
                Type = draft.Type,
                ScopeType = draft.ScopeType,
                ScopeId = draft.ScopeId,
                Title = draft.Title,
                DetectedAt = DateTime.UtcNow,
                Severity = draft.Severity,
                ConfidenceLevel = draft.ConfidenceLevel,
                Status = FindingStatus.New
            });
        }

        foreach (var (kind, text) in draft.Statements)
        {
            await _findings.AddStatementAsync(finding.Id, kind, text);
        }

        return finding;
    }
}
