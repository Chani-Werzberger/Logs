using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AnalysisEngineTickRunnerTests
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

    private static AnalysisEngineTickRunner BuildRunner(LogsPlatformDbContext context)
    {
        var applicationRepository = new ApplicationRepository(context);
        var environmentRepository = new AppEnvironmentRepository(context);
        var metricsRepository = new MetricsRepository(context);
        var baselineRepository = new BaselineRepository(context);
        var findingRepository = new FindingRepository(context);
        var deploymentRepository = new DeploymentRepository(context);
        var writer = new FindingWriter(findingRepository);
        var baselineCalculator = new BaselineCalculator(metricsRepository, baselineRepository);
        var rateAnomalyDetector = new RateAnomalyDetector(metricsRepository, baselineRepository, writer);
        var newExceptionDetector = new NewExceptionDetector(context, writer);
        var customerOutlierDetector = new CustomerOutlierDetector(metricsRepository, writer);
        var deploymentCorrelator = new DeploymentCorrelator(findingRepository, deploymentRepository);

        return new AnalysisEngineTickRunner(
            applicationRepository, environmentRepository, baselineRepository, findingRepository,
            baselineCalculator, rateAnomalyDetector, newExceptionDetector, customerOutlierDetector, deploymentCorrelator);
    }

    [Fact]
    public async Task RunOneTickAsync_NewApplicationWithNoData_CompletesWithoutThrowing()
    {
        using var context = TestDatabase.CreateContext();
        await SeedAppEnvAsync(context, "TickRunnerEmptyTestApp");

        var runner = BuildRunner(context);

        // No active Operations/ExceptionGroups exist yet, so no Baseline rows or Findings are
        // expected — the real assertion is that a tick over a completely empty (Application,
        // Environment) pair completes cleanly rather than throwing.
        var exception = await Record.ExceptionAsync(() => runner.RunOneTickAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task RunOneTickAsync_ErrorSpikeWithRecentDeployment_CorrelatorAttachesHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "TickRunnerCorrelationTestApp");

        var module = new AppModule { ApplicationId = appId, Name = "Billing" };
        context.Modules.Add(module);
        await context.SaveChangesAsync();
        var screenService = new ScreenService { ModuleId = module.Id, Name = "Invoicing", Type = ScreenServiceType.Service };
        context.ScreenServices.Add(screenService);
        await context.SaveChangesAsync();
        var process = new ProcessNode { ScreenServiceId = screenService.Id, Name = "ChargeCard" };
        context.Processes.Add(process);
        await context.SaveChangesAsync();
        var operation = new Operation { ProcessId = process.Id, Name = "Authorize" };
        context.Operations.Add(operation);
        await context.SaveChangesAsync();
        var version = new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0", CreatedAt = DateTime.UtcNow };
        context.Versions.Add(version);
        await context.SaveChangesAsync();

        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        context.Baselines.Add(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = operation.Id,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = (byte)currentHourStart.Hour,
            MeanValue = 5, StdDevValue = 1, SampleCount = 20, LastUpdatedAt = DateTime.UtcNow
        });
        context.Deployments.Add(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = version.Id, DeployedAt = DateTime.UtcNow.AddMinutes(-10) });
        for (var i = 0; i < 40; i++)
        {
            context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = operation.Id, Timestamp = currentHourStart.AddMinutes(i % 59), Severity = 17, Message = $"spike-{i}" });
        }
        await context.SaveChangesAsync();

        var runner = BuildRunner(context);
        await runner.RunOneTickAsync();

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.ErrorSpike);
        Assert.NotNull(finding);

        var findingRepository = new FindingRepository(verifyContext);
        var details = await findingRepository.GetByIdAsync(finding!.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
    }
}
