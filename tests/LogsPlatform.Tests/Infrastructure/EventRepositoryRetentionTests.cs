using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class EventRepositoryRetentionTests
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
    public async Task DeleteOlderThanAsync_DeletesOnlyEventsOlderThanCutoffForGivenApplication()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "RetentionDeleteTestApp");
        var (otherAppId, otherEnvId) = await SeedAppEnvAsync(context, "RetentionDeleteOtherTestApp");

        var cutoff = DateTime.UtcNow.AddDays(-30);
        var oldEvent = new Event { ApplicationId = appId, EnvironmentId = envId, Timestamp = cutoff.AddDays(-1), Severity = 17, Message = "old" };
        var newEvent = new Event { ApplicationId = appId, EnvironmentId = envId, Timestamp = cutoff.AddDays(1), Severity = 17, Message = "new" };
        var otherAppOldEvent = new Event { ApplicationId = otherAppId, EnvironmentId = otherEnvId, Timestamp = cutoff.AddDays(-1), Severity = 17, Message = "other app old" };
        context.Events.AddRange(oldEvent, newEvent, otherAppOldEvent);
        await context.SaveChangesAsync();

        var repository = new EventRepository(context);
        var deletedCount = await repository.DeleteOlderThanAsync(appId, cutoff);

        Assert.Equal(1, deletedCount);
        var remaining = context.Events.Select(e => e.Id).ToHashSet();
        Assert.DoesNotContain(oldEvent.Id, remaining);
        Assert.Contains(newEvent.Id, remaining);
        Assert.Contains(otherAppOldEvent.Id, remaining);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_NoEventsOlderThanCutoff_ReturnsZero()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "RetentionDeleteNoneTestApp");
        var cutoff = DateTime.UtcNow.AddDays(-30);
        context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, Timestamp = cutoff.AddDays(1), Severity = 17, Message = "new" });
        await context.SaveChangesAsync();

        var repository = new EventRepository(context);
        var deletedCount = await repository.DeleteOlderThanAsync(appId, cutoff);

        Assert.Equal(0, deletedCount);
    }
}
