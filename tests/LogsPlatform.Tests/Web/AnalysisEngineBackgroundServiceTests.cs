using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AnalysisEngineBackgroundServiceTests
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

    private static AnalysisEngineBackgroundService BuildService(LogsPlatformDbContext context)
    {
        // A small real DI container (not a mock) proving the actual scope-per-tick wiring works —
        // every registered type is the real implementation Program.cs uses. Registered Singleton
        // here (unlike Program.cs's real Scoped AddDbContext) so every resolution within the test's
        // one DI scope sees the same in-flight data as the context the test seeded directly.
        var services = new ServiceCollection();
        services.AddSingleton(context);
        // ApplicationRepository/AppEnvironmentRepository/DeploymentRepository now take
        // IDbContextFactory<LogsPlatformDbContext> (see Program.cs) instead of the context
        // directly, so each of their calls gets a fresh context — fine here since SeedAppEnvAsync
        // already committed via SaveChangesAsync, so a fresh context sees the same persisted data.
        services.AddSingleton(TestDatabase.CreateFactory());
        services.AddSingleton<IApplicationRepository, ApplicationRepository>();
        services.AddSingleton<IAppEnvironmentRepository, AppEnvironmentRepository>();
        services.AddSingleton<IMetricsRepository, MetricsRepository>();
        services.AddSingleton<IBaselineRepository, BaselineRepository>();
        services.AddSingleton<IFindingRepository, FindingRepository>();
        services.AddSingleton<IDeploymentRepository, DeploymentRepository>();
        services.AddSingleton<FindingWriter>();
        services.AddSingleton<BaselineCalculator>();
        services.AddSingleton<RateAnomalyDetector>();
        services.AddSingleton<NewExceptionDetector>();
        services.AddSingleton<CustomerOutlierDetector>();
        services.AddSingleton<DeploymentCorrelator>();
        services.AddSingleton<DownstreamFailureCorrelator>();
        services.AddSingleton<UpstreamCauseCorrelator>();
        services.AddSingleton<ConcurrentFindingCorrelator>();
        services.AddSingleton<RecurrenceCorrelator>();
        services.AddScoped<AnalysisEngineTickRunner>();

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new AnalysisEngineBackgroundService(scopeFactory, NullLogger<AnalysisEngineBackgroundService>.Instance);
    }

    [Fact]
    public async Task TryRunOneTickAsync_CalledWhileAlreadyRunning_SecondCallIsSkipped()
    {
        using var context = TestDatabase.CreateContext();
        await SeedAppEnvAsync(context, "BackgroundServiceConcurrentTickTestApp");

        var service = BuildService(context);

        // Both calls go through the guarded entry point. The guard's Interlocked.CompareExchange
        // runs synchronously before the scope-creation/tick-running code's first real await (a DB
        // call), so by the time this line returns control here, _isRunning is already set — the
        // second call's own CompareExchange deterministically sees it and skips.
        var firstTick = service.TryRunOneTickAsync();
        var secondTickRan = await service.TryRunOneTickAsync();
        var firstTickRan = await firstTick;

        Assert.True(firstTickRan);
        Assert.False(secondTickRan);
    }
}
