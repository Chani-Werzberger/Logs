using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ConcurrentFindingCorrelatorTests
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
    public async Task RunAsync_OtherOpenFindingExistsOnSameApplication_AddsHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "ConcurrentCorrelatorTestApp");
        var findingRepository = new FindingRepository(context);

        var thisFinding = await findingRepository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "this one",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });
        await findingRepository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.PerformanceDegradation,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 2, Title = "other one",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.Medium, ConfidenceLevel = ConfidenceLevel.Medium, Status = FindingStatus.Acknowledged
        });

        var correlator = new ConcurrentFindingCorrelator(findingRepository);
        await correlator.RunAsync(thisFinding);

        var details = await findingRepository.GetByIdAsync(thisFinding.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
        Assert.Contains(details.Evidence, e => e.EvidenceType == EvidenceType.Finding);
    }

    [Fact]
    public async Task RunAsync_NoOtherOpenFindings_DoesNotAddHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "ConcurrentCorrelatorNoneTestApp");
        var findingRepository = new FindingRepository(context);

        var thisFinding = await findingRepository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "this one",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        var correlator = new ConcurrentFindingCorrelator(findingRepository);
        await correlator.RunAsync(thisFinding);

        var details = await findingRepository.GetByIdAsync(thisFinding.Id);
        Assert.Empty(details!.Statements);
        Assert.Empty(details.Evidence);
    }
}
