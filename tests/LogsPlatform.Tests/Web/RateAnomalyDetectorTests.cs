using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class RateAnomalyDetectorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId, int OperationId)> SeedAppEnvOperationAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        var module = new AppModule { ApplicationId = app.Id, Name = "Billing" };
        context.AppEnvironments.Add(env);
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
        return (app.Id, env.Id, operation.Id);
    }

    [Fact]
    public async Task RunAsync_CurrentHourFarAboveBaseline_CreatesErrorSpikeFinding()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "RateAnomalySpikeTestApp");

        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        context.Baselines.Add(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = opId,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = (byte)currentHourStart.Hour,
            MeanValue = 5, StdDevValue = 1, SampleCount = 20, LastUpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        for (var i = 0; i < 40; i++)
        {
            context.Events.Add(new Event
            {
                ApplicationId = appId, EnvironmentId = envId, OperationId = opId,
                Timestamp = currentHourStart.AddMinutes(i % 59), Severity = 17, Message = $"spike-{i}"
            });
        }
        await context.SaveChangesAsync();

        var metrics = new MetricsRepository(context);
        var baselines = new BaselineRepository(context);
        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var downstreamCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var upstreamCorrelator = new UpstreamCauseCorrelator(findingRepository, context);
        var detector = new RateAnomalyDetector(metrics, baselines, writer, downstreamCorrelator, upstreamCorrelator, context);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.ErrorSpike);

        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.High, finding!.Severity);
    }

    [Fact]
    public async Task RunAsync_CurrentHourWithinNormalRange_NoFindingCreated()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "RateAnomalyNormalTestApp");

        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        context.Baselines.Add(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = opId,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = (byte)currentHourStart.Hour,
            MeanValue = 5, StdDevValue = 1, SampleCount = 20, LastUpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        for (var i = 0; i < 5; i++)
        {
            context.Events.Add(new Event
            {
                ApplicationId = appId, EnvironmentId = envId, OperationId = opId,
                Timestamp = currentHourStart.AddMinutes(i), Severity = 17, Message = $"normal-{i}"
            });
        }
        await context.SaveChangesAsync();

        var metrics = new MetricsRepository(context);
        var baselines = new BaselineRepository(context);
        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var downstreamCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var upstreamCorrelator = new UpstreamCauseCorrelator(findingRepository, context);
        var detector = new RateAnomalyDetector(metrics, baselines, writer, downstreamCorrelator, upstreamCorrelator, context);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var findingCount = await verifyContext.Findings.CountAsync(f => f.ApplicationId == appId);

        Assert.Equal(0, findingCount);
    }

    [Fact]
    public async Task RunAsync_ErrorSpikeWithCorrelatedDownstreamFailure_AddsHypothesisFromCorrelator()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "RateAnomalyDownstreamTestApp");

        var triggerOperation = await context.Operations.FirstAsync(o => o.Id == opId);
        var downstreamOperation = new Operation { ProcessId = triggerOperation.ProcessId, Name = "Capture" };
        context.Operations.Add(downstreamOperation);
        await context.SaveChangesAsync();

        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        context.Baselines.Add(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = opId,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = (byte)currentHourStart.Hour,
            MeanValue = 5, StdDevValue = 1, SampleCount = 20, LastUpdatedAt = DateTime.UtcNow
        });
        for (var i = 0; i < 40; i++)
        {
            context.Events.Add(new Event
            {
                ApplicationId = appId, EnvironmentId = envId, OperationId = opId,
                Timestamp = currentHourStart.AddMinutes(i % 59), Severity = 17, Message = $"spike-{i}"
            });
        }
        var triggerTime = currentHourStart.AddMinutes(45);
        context.Events.Add(new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CorrelationId = "order-77",
            Timestamp = triggerTime, Severity = 17, Message = "the trigger event"
        });
        context.Events.Add(new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = downstreamOperation.Id, CorrelationId = "order-77",
            Timestamp = triggerTime.AddSeconds(3), Severity = 17, Message = "downstream failure"
        });
        await context.SaveChangesAsync();

        var metrics = new MetricsRepository(context);
        var baselines = new BaselineRepository(context);
        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var downstreamCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var upstreamCorrelator = new UpstreamCauseCorrelator(findingRepository, context);
        var detector = new RateAnomalyDetector(metrics, baselines, writer, downstreamCorrelator, upstreamCorrelator, context);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.ErrorSpike);
        Assert.NotNull(finding);

        var findingRepositoryForVerify = new FindingRepository(verifyContext);
        var details = await findingRepositoryForVerify.GetByIdAsync(finding!.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
    }
}
