using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class BaselineCalculatorTests
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
    public async Task RunAsync_KnownDistribution_ComputesExpectedMeanAndStdDev()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "BaselineCalcKnownDistTestApp");

        // Seed exactly 20 daily samples at hour 14: 10 events each day.
        var today = DateTime.UtcNow.Date;
        for (var day = 1; day <= 20; day++)
        {
            var hourStart = today.AddDays(-day).AddHours(14);
            for (var i = 0; i < 10; i++)
            {
                context.Events.Add(new Event
                {
                    ApplicationId = appId, EnvironmentId = envId, OperationId = opId,
                    Timestamp = hourStart.AddMinutes(i), Severity = 17, Message = $"evt-{day}-{i}"
                });
            }
        }
        await context.SaveChangesAsync();

        var metricsRepository = new MetricsRepository(context);
        var baselineRepository = new BaselineRepository(context);
        var calculator = new BaselineCalculator(metricsRepository, baselineRepository);

        await calculator.RunAsync(appId, envId);

        var baseline = await baselineRepository.GetAsync(appId, envId, AnalysisScopeType.Operation, opId, AnalysisMetricType.EventCount, 14);
        Assert.NotNull(baseline);
        Assert.Equal(10, baseline!.MeanValue, precision: 5);
        Assert.Equal(0, baseline.StdDevValue, precision: 5);
        Assert.Equal(20, baseline.SampleCount);
    }

    [Fact]
    public async Task RunAsync_FewerThanMinSamples_StillSavesRow()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "BaselineCalcFewSamplesTestApp");

        var today = DateTime.UtcNow.Date;
        for (var day = 1; day <= 3; day++)
        {
            context.Events.Add(new Event
            {
                ApplicationId = appId, EnvironmentId = envId, OperationId = opId,
                Timestamp = today.AddDays(-day).AddHours(9), Severity = 17, Message = $"evt-{day}"
            });
        }
        await context.SaveChangesAsync();

        var metricsRepository = new MetricsRepository(context);
        var baselineRepository = new BaselineRepository(context);
        var calculator = new BaselineCalculator(metricsRepository, baselineRepository);

        await calculator.RunAsync(appId, envId);

        var baseline = await baselineRepository.GetAsync(appId, envId, AnalysisScopeType.Operation, opId, AnalysisMetricType.EventCount, 9);
        Assert.NotNull(baseline);
        Assert.Equal(3, baseline!.SampleCount);
    }
}
