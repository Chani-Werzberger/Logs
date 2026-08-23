using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class FindingRepositoryTests
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
    public async Task FindOpenAsync_MatchingOpenFinding_ReturnsIt()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoOpenTestApp");
        var repository = new FindingRepository(context);

        var finding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        var found = await repository.FindOpenAsync(appId, envId, AnalysisScopeType.Operation, 1, FindingType.ErrorSpike, cooldownSince: DateTime.UtcNow.AddHours(-24));

        Assert.NotNull(found);
        Assert.Equal(finding.Id, found!.Id);
    }

    [Fact]
    public async Task FindOpenAsync_ResolvedFinding_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoResolvedTestApp");
        var repository = new FindingRepository(context);

        await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.Resolved
        });

        var found = await repository.FindOpenAsync(appId, envId, AnalysisScopeType.Operation, 1, FindingType.ErrorSpike, cooldownSince: DateTime.UtcNow.AddHours(-24));

        Assert.Null(found);
    }

    [Fact]
    public async Task AddStatementAsync_PersistsWithCorrectKind()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoStatementTestApp");
        var repository = new FindingRepository(context);
        var finding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.NewException,
            ScopeType = AnalysisScopeType.ExceptionGroup, ScopeId = 1, Title = "test",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        await repository.AddStatementAsync(finding.Id, DetectorStatementKind.Fact, "A fact.");

        var reloaded = await repository.GetByIdAsync(finding.Id);
        Assert.Single(reloaded!.Statements);
        Assert.Equal(FindingStatementKind.Fact, reloaded.Statements[0].Kind);
    }
}
