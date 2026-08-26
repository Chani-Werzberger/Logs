using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class FindingRepository : IFindingRepository
{
    private readonly LogsPlatformDbContext _context;

    public FindingRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<Finding?> FindOpenAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type, DateTime cooldownSince) =>
        await _context.Findings.AsNoTracking().FirstOrDefaultAsync(f =>
            f.ApplicationId == applicationId && f.EnvironmentId == environmentId &&
            f.ScopeType == scopeType && f.ScopeId == scopeId && f.Type == type &&
            (f.Status == FindingStatus.New || f.Status == FindingStatus.Acknowledged) &&
            f.DetectedAt >= cooldownSince);

    public async Task<Finding> AddAsync(Finding finding)
    {
        _context.Findings.Add(finding);
        await _context.SaveChangesAsync();
        return finding;
    }

    public async Task AddStatementAsync(long findingId, DetectorStatementKind kind, string text)
    {
        var maxOrderIndex = await _context.FindingStatements
            .Where(s => s.FindingId == findingId)
            .Select(s => (int?)s.OrderIndex)
            .MaxAsync() ?? -1;

        _context.FindingStatements.Add(new FindingStatement
        {
            FindingId = findingId,
            Kind = (FindingStatementKind)kind,
            Text = text,
            OrderIndex = maxOrderIndex + 1
        });
        await _context.SaveChangesAsync();
    }

    public async Task AddEvidenceAsync(long findingId, EvidenceType evidenceType, long referenceId, string description)
    {
        _context.Evidence.Add(new Evidence
        {
            FindingId = findingId,
            EvidenceType = evidenceType,
            ReferenceId = referenceId,
            Description = description
        });
        await _context.SaveChangesAsync();
    }

    public async Task<FindingWithDetails?> GetByIdAsync(long id)
    {
        var finding = await _context.Findings.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
        if (finding is null)
        {
            return null;
        }

        var statements = await _context.FindingStatements.AsNoTracking()
            .Where(s => s.FindingId == id).OrderBy(s => s.OrderIndex).ToListAsync();
        var evidence = await _context.Evidence.AsNoTracking()
            .Where(e => e.FindingId == id).ToListAsync();

        return new FindingWithDetails(finding, statements, evidence);
    }

    public async Task<IReadOnlyList<Finding>> GetDetectedSinceAsync(int applicationId, int environmentId, DateTime since) =>
        await _context.Findings.AsNoTracking()
            .Where(f => f.ApplicationId == applicationId && f.EnvironmentId == environmentId && f.DetectedAt >= since)
            .ToListAsync();

    public async Task<IReadOnlyList<Finding>> QueryAsync(FindingQueryParameters parameters)
    {
        var query = _context.Findings.AsNoTracking()
            .Where(f => f.ApplicationId == parameters.ApplicationId && f.EnvironmentId == parameters.EnvironmentId);

        if (parameters.Status is not null) query = query.Where(f => f.Status == parameters.Status);
        if (parameters.Severity is not null) query = query.Where(f => f.Severity == parameters.Severity);
        if (parameters.Type is not null) query = query.Where(f => f.Type == parameters.Type);
        if (parameters.From is not null) query = query.Where(f => f.DetectedAt >= parameters.From);
        if (parameters.To is not null) query = query.Where(f => f.DetectedAt <= parameters.To);

        return await query.OrderByDescending(f => f.Severity).ThenByDescending(f => f.DetectedAt).ToListAsync();
    }

    public async Task<Finding?> UpdateStatusAsync(long findingId, FindingStatus status)
    {
        var finding = await _context.Findings.FirstOrDefaultAsync(f => f.Id == findingId);
        if (finding is null)
        {
            return null;
        }

        finding.Status = status;
        await _context.SaveChangesAsync();
        return finding;
    }

    public async Task<FindingStatement?> PromoteToConclusionAsync(long findingId, long statementId, string approvedBy)
    {
        var statement = await _context.FindingStatements.FirstOrDefaultAsync(s => s.Id == statementId && s.FindingId == findingId);
        if (statement is null || statement.Kind != FindingStatementKind.Hypothesis)
        {
            return null;
        }

        statement.Kind = FindingStatementKind.Conclusion;
        statement.ApprovedBy = approvedBy;
        statement.ApprovedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return statement;
    }

    public async Task<IReadOnlyList<Finding>> GetOtherOpenFindingsForApplicationAsync(int applicationId, long excludeFindingId) =>
        await _context.Findings.AsNoTracking()
            .Where(f => f.ApplicationId == applicationId && f.Id != excludeFindingId &&
                (f.Status == FindingStatus.New || f.Status == FindingStatus.Acknowledged))
            .ToListAsync();

    public async Task<Finding?> FindMostRecentClosedAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type, long excludeFindingId) =>
        await _context.Findings.AsNoTracking()
            .Where(f => f.ApplicationId == applicationId && f.EnvironmentId == environmentId &&
                f.ScopeType == scopeType && f.ScopeId == scopeId && f.Type == type && f.Id != excludeFindingId &&
                (f.Status == FindingStatus.Resolved || f.Status == FindingStatus.Dismissed))
            .OrderByDescending(f => f.DetectedAt)
            .FirstOrDefaultAsync();
}
