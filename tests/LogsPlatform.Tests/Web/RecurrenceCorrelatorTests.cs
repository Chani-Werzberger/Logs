using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class RecurrenceCorrelatorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> SeedAppEnvAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();
        return (app.Id, env.Id);
    }

    [Fact]
    public async Task RunAsync_MatchingResolvedFindingExists_AddsHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "RecurrenceCorrelatorTestApp");
        var findingRepository = new FindingRepository(context);

        await findingRepository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "prior resolved",
            DetectedAt = DateTime.UtcNow.AddDays(-3), Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.Resolved
        });
        var current = await findingRepository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "current",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        var correlator = new RecurrenceCorrelator(findingRepository);
        await correlator.RunAsync(current);

        var details = await findingRepository.GetByIdAsync(current.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
        Assert.Contains(details.Evidence, e => e.EvidenceType == EvidenceType.Finding);
    }

    [Fact]
    public async Task RunAsync_NoPriorClosedFinding_DoesNotAddHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "RecurrenceCorrelatorNoneTestApp");
        var findingRepository = new FindingRepository(context);

        var current = await findingRepository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "current",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        var correlator = new RecurrenceCorrelator(findingRepository);
        await correlator.RunAsync(current);

        var details = await findingRepository.GetByIdAsync(current.Id);
        Assert.Empty(details!.Statements);
        Assert.Empty(details.Evidence);
    }
}
