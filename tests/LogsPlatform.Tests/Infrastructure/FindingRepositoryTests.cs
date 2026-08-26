using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

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

    [Fact]
    public async Task QueryAsync_FiltersByStatusAndOrdersBySeverityThenDetectedAt()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoQueryTestApp");
        var repository = new FindingRepository(context);

        var now = DateTime.UtcNow;
        await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "low, older",
            DetectedAt = now.AddMinutes(-10), Severity = FindingSeverity.Low, ConfidenceLevel = ConfidenceLevel.Medium, Status = FindingStatus.New
        });
        var high = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 2, Title = "high, newer",
            DetectedAt = now, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });
        await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 3, Title = "high, but resolved",
            DetectedAt = now.AddMinutes(-5), Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.Resolved
        });

        var results = await repository.QueryAsync(new FindingQueryParameters(appId, envId, FindingStatus.New, null, null, null, null));

        Assert.Equal(2, results.Count);
        Assert.Equal(high.Id, results[0].Id);
    }

    [Fact]
    public async Task UpdateStatusAsync_ExistingFinding_UpdatesAndReturnsIt()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoUpdateStatusTestApp");
        var repository = new FindingRepository(context);
        var finding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        var updated = await repository.UpdateStatusAsync(finding.Id, FindingStatus.Acknowledged);

        Assert.NotNull(updated);
        Assert.Equal(FindingStatus.Acknowledged, updated!.Status);

        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var reloaded = await verifyContext.Findings.FindAsync(finding.Id);
        Assert.Equal(FindingStatus.Acknowledged, reloaded!.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_NoSuchFinding_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new FindingRepository(context);

        var updated = await repository.UpdateStatusAsync(999999, FindingStatus.Acknowledged);

        Assert.Null(updated);
    }

    [Fact]
    public async Task PromoteToConclusionAsync_HypothesisStatement_PromotesWithApprovalMetadata()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoPromoteTestApp");
        var repository = new FindingRepository(context);
        var finding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });
        await repository.AddStatementAsync(finding.Id, DetectorStatementKind.Hypothesis, "Maybe a deployment caused this.");
        var reloaded = await repository.GetByIdAsync(finding.Id);
        var statementId = reloaded!.Statements[0].Id;

        var promoted = await repository.PromoteToConclusionAsync(finding.Id, statementId, "Dana");

        Assert.NotNull(promoted);
        Assert.Equal(FindingStatementKind.Conclusion, promoted!.Kind);
        Assert.Equal("Dana", promoted.ApprovedBy);
        Assert.NotNull(promoted.ApprovedAt);
    }

    [Fact]
    public async Task PromoteToConclusionAsync_StatementNotHypothesis_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoPromoteFactTestApp");
        var repository = new FindingRepository(context);
        var finding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });
        await repository.AddStatementAsync(finding.Id, DetectorStatementKind.Fact, "A measured fact.");
        var reloaded = await repository.GetByIdAsync(finding.Id);
        var statementId = reloaded!.Statements[0].Id;

        var promoted = await repository.PromoteToConclusionAsync(finding.Id, statementId, "Dana");

        Assert.Null(promoted);
    }

    [Fact]
    public async Task GetOtherOpenFindingsForApplicationAsync_OtherOpenFindingExists_ReturnsIt()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoConcurrentTestApp");
        var repository = new FindingRepository(context);

        var thisFinding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "this one",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });
        var otherFinding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.PerformanceDegradation,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 2, Title = "other one",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.Medium, ConfidenceLevel = ConfidenceLevel.Medium, Status = FindingStatus.Acknowledged
        });

        var others = await repository.GetOtherOpenFindingsForApplicationAsync(appId, thisFinding.Id);

        Assert.Single(others);
        Assert.Equal(otherFinding.Id, others[0].Id);
    }

    [Fact]
    public async Task GetOtherOpenFindingsForApplicationAsync_OnlyResolvedFindingsExist_ReturnsEmpty()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoConcurrentResolvedTestApp");
        var repository = new FindingRepository(context);

        var thisFinding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "this one",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });
        await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.PerformanceDegradation,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 2, Title = "resolved one",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.Medium, ConfidenceLevel = ConfidenceLevel.Medium, Status = FindingStatus.Resolved
        });

        var others = await repository.GetOtherOpenFindingsForApplicationAsync(appId, thisFinding.Id);

        Assert.Empty(others);
    }

    [Fact]
    public async Task FindMostRecentClosedAsync_MatchingResolvedFindingExists_ReturnsIt()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoRecurrenceTestApp");
        var repository = new FindingRepository(context);

        var older = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "older resolved",
            DetectedAt = DateTime.UtcNow.AddDays(-2), Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.Resolved
        });
        var newer = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "newer resolved",
            DetectedAt = DateTime.UtcNow.AddDays(-1), Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.Dismissed
        });
        var current = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "current",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        var found = await repository.FindMostRecentClosedAsync(appId, envId, AnalysisScopeType.Operation, 1, FindingType.ErrorSpike, current.Id);

        Assert.NotNull(found);
        Assert.Equal(newer.Id, found!.Id);
        _ = older; // only used to prove "most recent" ordering, not the older one
    }

    [Fact]
    public async Task FindMostRecentClosedAsync_NoClosedMatch_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoRecurrenceNoneTestApp");
        var repository = new FindingRepository(context);

        var current = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "current",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        var found = await repository.FindMostRecentClosedAsync(appId, envId, AnalysisScopeType.Operation, 1, FindingType.ErrorSpike, current.Id);

        Assert.Null(found);
    }
}
