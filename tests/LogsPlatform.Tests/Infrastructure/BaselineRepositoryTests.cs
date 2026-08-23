using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class BaselineRepositoryTests
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
    public async Task UpsertAsync_NoExistingRow_Inserts()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "BaselineUpsertInsertTestApp");
        var repository = new BaselineRepository(context);

        await repository.UpsertAsync(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = 1,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = 10, MeanValue = 5, StdDevValue = 1, SampleCount = 20, LastUpdatedAt = DateTime.UtcNow
        });

        var result = await repository.GetAsync(appId, envId, AnalysisScopeType.Operation, 1, AnalysisMetricType.EventCount, 10);
        Assert.NotNull(result);
        Assert.Equal(5, result!.MeanValue);
    }

    [Fact]
    public async Task UpsertAsync_ExistingRow_Updates()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "BaselineUpsertUpdateTestApp");
        var repository = new BaselineRepository(context);

        await repository.UpsertAsync(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = 1,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = 10, MeanValue = 5, StdDevValue = 1, SampleCount = 20, LastUpdatedAt = DateTime.UtcNow
        });
        await repository.UpsertAsync(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = 1,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = 10, MeanValue = 8, StdDevValue = 2, SampleCount = 21, LastUpdatedAt = DateTime.UtcNow
        });

        var result = await repository.GetAsync(appId, envId, AnalysisScopeType.Operation, 1, AnalysisMetricType.EventCount, 10);
        Assert.Equal(8, result!.MeanValue);

        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var rowCount = verifyContext.Baselines.Count(b => b.ApplicationId == appId && b.EnvironmentId == envId && b.ScopeId == 1 && b.BucketHourOfDay == 10);
        Assert.Equal(1, rowCount);
    }
}
