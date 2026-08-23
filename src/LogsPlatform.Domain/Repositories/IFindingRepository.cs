using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IFindingRepository
{
    Task<Finding?> FindOpenAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type, DateTime cooldownSince);
    Task<Finding> AddAsync(Finding finding);
    Task AddStatementAsync(long findingId, DetectorStatementKind kind, string text);
    Task AddEvidenceAsync(long findingId, EvidenceType evidenceType, long referenceId, string description);
    Task<FindingWithDetails?> GetByIdAsync(long id);
    Task<IReadOnlyList<Finding>> GetDetectedSinceAsync(int applicationId, int environmentId, DateTime since);
    Task<IReadOnlyList<Finding>> QueryAsync(FindingQueryParameters parameters);
    Task<Finding?> UpdateStatusAsync(long findingId, FindingStatus status);
    Task<FindingStatement?> PromoteToConclusionAsync(long findingId, long statementId, string approvedBy);
}

public record FindingWithDetails(Finding Finding, IReadOnlyList<FindingStatement> Statements, IReadOnlyList<Evidence> Evidence)
{
    public long Id => Finding.Id;
    public FindingStatus Status => Finding.Status;
}

public record FindingQueryParameters(int ApplicationId, int EnvironmentId, FindingStatus? Status, FindingSeverity? Severity, FindingType? Type, DateTime? From, DateTime? To);
