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
}
