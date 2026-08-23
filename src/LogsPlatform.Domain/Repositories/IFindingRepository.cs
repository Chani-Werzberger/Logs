using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IFindingRepository
{
    Task<Finding?> FindOpenAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type, DateTime cooldownSince);
    Task<Finding> AddAsync(Finding finding);
    Task AddStatementAsync(long findingId, DetectorStatementKind kind, string text);
    Task AddEvidenceAsync(long findingId, EvidenceType evidenceType, long referenceId, string description);
    Task<FindingWithDetails?> GetByIdAsync(long id);
}

public record FindingWithDetails(Finding Finding, IReadOnlyList<FindingStatement> Statements, IReadOnlyList<Evidence> Evidence)
{
    public long Id => Finding.Id;
    public FindingStatus Status => Finding.Status;
}
