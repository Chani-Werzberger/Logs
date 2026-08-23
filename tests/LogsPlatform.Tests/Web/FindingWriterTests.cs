using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class FindingWriterTests
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
    public async Task WriteAsync_TwoCallsSameScope_ReusesFindingAndAppendsFact()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingWriterDedupTestApp");
        var repository = new FindingRepository(context);
        var writer = new FindingWriter(repository);

        var draft = new FindingDraft(appId, envId, FindingType.ErrorSpike, AnalysisScopeType.Operation, 1,
            "Error spike", FindingSeverity.High, ConfidenceLevel.High,
            new[] { (DetectorStatementKind.Fact, "First detection.") });

        var first = await writer.WriteAsync(draft);
        var second = await writer.WriteAsync(draft with { Statements = new[] { (DetectorStatementKind.Fact, "Second detection, still ongoing.") } });

        Assert.Equal(first.Id, second.Id);
        var reloaded = await repository.GetByIdAsync(first.Id);
        Assert.Equal(2, reloaded!.Statements.Count);
    }

    [Fact]
    public async Task WriteAsync_NoExistingFinding_CreatesNew()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingWriterCreateTestApp");
        var repository = new FindingRepository(context);
        var writer = new FindingWriter(repository);

        var draft = new FindingDraft(appId, envId, FindingType.NewException, AnalysisScopeType.ExceptionGroup, 1,
            "New exception", FindingSeverity.High, ConfidenceLevel.High,
            new[] { (DetectorStatementKind.Fact, "First-ever occurrence.") });

        var finding = await writer.WriteAsync(draft);

        Assert.True(finding.Id > 0);
        Assert.Equal(FindingStatus.New, finding.Status);
    }
}
