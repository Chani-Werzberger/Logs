using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class RetentionCleanupServiceTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> SeedAppEnvAsync(LogsPlatformDbContext context, string appName, int? retentionDays)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow, RetentionDays = retentionDays };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();
        return (app.Id, env.Id);
    }

    private static RetentionCleanupService BuildService(LogsPlatformDbContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton(TestDatabase.CreateFactory());
        services.AddSingleton<IApplicationRepository, ApplicationRepository>();
        services.AddSingleton<IEventRepository, EventRepository>();

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new RetentionCleanupService(scopeFactory, NullLogger<RetentionCleanupService>.Instance);
    }

    [Fact]
    public async Task TryRunOneCleanupAsync_DeletesOldEventsForApplicationsWithRetentionSet_LeavesOthersUntouched()
    {
        using var context = TestDatabase.CreateContext();
        var (retainedAppId, retainedEnvId) = await SeedAppEnvAsync(context, "RetentionCleanupRetainedTestApp", retentionDays: 30);
        var (foreverAppId, foreverEnvId) = await SeedAppEnvAsync(context, "RetentionCleanupForeverTestApp", retentionDays: null);

        var cutoff = DateTime.UtcNow.AddDays(-30);
        var oldRetainedEvent = new Event { ApplicationId = retainedAppId, EnvironmentId = retainedEnvId, Timestamp = cutoff.AddDays(-1), Severity = 17, Message = "old" };
        var newRetainedEvent = new Event { ApplicationId = retainedAppId, EnvironmentId = retainedEnvId, Timestamp = cutoff.AddDays(1), Severity = 17, Message = "new" };
        var oldForeverEvent = new Event { ApplicationId = foreverAppId, EnvironmentId = foreverEnvId, Timestamp = cutoff.AddDays(-1), Severity = 17, Message = "old but null retention" };
        context.Events.AddRange(oldRetainedEvent, newRetainedEvent, oldForeverEvent);
        await context.SaveChangesAsync();

        var service = BuildService(context);
        var result = await service.TryRunOneCleanupAsync();

        Assert.True(result);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var remainingIds = await verifyContext.Events.Select(e => e.Id).ToListAsync();

        Assert.DoesNotContain(oldRetainedEvent.Id, remainingIds);
        Assert.Contains(newRetainedEvent.Id, remainingIds);
        Assert.Contains(oldForeverEvent.Id, remainingIds);
    }

    [Fact]
    public async Task TryRunOneCleanupAsync_CalledWhileAlreadyRunning_SecondCallIsSkipped()
    {
        using var context = TestDatabase.CreateContext();
        await SeedAppEnvAsync(context, "RetentionCleanupConcurrentTestApp", retentionDays: null);

        var service = BuildService(context);

        var firstRun = service.TryRunOneCleanupAsync();
        var secondRunRan = await service.TryRunOneCleanupAsync();
        var firstRunRan = await firstRun;

        Assert.True(firstRunRan);
        Assert.False(secondRunRan);
    }
}
