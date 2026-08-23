using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class DeploymentCorrelatorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId, int VersionId)> SeedAppEnvVersionAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();
        var version = new AppVersion { ApplicationId = app.Id, VersionNumber = "2.3.1", CreatedAt = DateTime.UtcNow };
        context.Versions.Add(version);
        await context.SaveChangesAsync();
        return (app.Id, env.Id, version.Id);
    }

    [Fact]
    public async Task RunAsync_DeploymentWithinWindow_AddsHypothesisAndEvidence()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, versionId) = await SeedAppEnvVersionAsync(context, "DeploymentCorrelatorTestApp");

        var detectedAt = DateTime.UtcNow;
        context.Deployments.Add(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = versionId, DeployedAt = detectedAt.AddMinutes(-13) });
        await context.SaveChangesAsync();

        var finding = new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = detectedAt, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        };
        context.Findings.Add(finding);
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        var deploymentRepository = new DeploymentRepository(context);
        var correlator = new DeploymentCorrelator(findingRepository, deploymentRepository);

        await correlator.RunAsync(finding);

        var details = await findingRepository.GetByIdAsync(finding.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
        Assert.Contains(details.Evidence, e => e.EvidenceType == EvidenceType.Deployment);
    }

    [Fact]
    public async Task RunAsync_NoDeploymentInWindow_NoHypothesisAdded()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, _) = await SeedAppEnvVersionAsync(context, "DeploymentCorrelatorNoMatchTestApp");

        var finding = new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        };
        context.Findings.Add(finding);
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        var deploymentRepository = new DeploymentRepository(context);
        var correlator = new DeploymentCorrelator(findingRepository, deploymentRepository);

        await correlator.RunAsync(finding);

        var details = await findingRepository.GetByIdAsync(finding.Id);
        Assert.Empty(details!.Statements);
    }
}
