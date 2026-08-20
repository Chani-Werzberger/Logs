using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class EventRepositoryTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> CreateFixtureAsync(LogsPlatformDbContext context, string appName)
    {
        var application = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        var environment = new AppEnvironment { Name = "Production", IsProduction = true };
        application.Environments.Add(environment);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return (application.Id, environment.Id);
    }

    private static Event MakeEvent(int appId, int envId, string? eventKey = null) => new()
    {
        ApplicationId = appId,
        EnvironmentId = envId,
        Timestamp = DateTime.UtcNow,
        Severity = 9,
        Message = "test event",
        EventKey = eventKey
    };

    [Fact]
    public async Task AddEventsAsync_PersistsEvents_ReturnsAcceptedCount()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await CreateFixtureAsync(context, "EventAddTestApp");
        var repository = new EventRepository(context);

        var result = await repository.AddEventsAsync(appId, new[] { MakeEvent(appId, envId), MakeEvent(appId, envId) });

        Assert.Equal(2, result.Accepted);
        Assert.Equal(0, result.DuplicateEventKeysSkipped);
        Assert.Equal(2, await context.Events.CountAsync());
    }

    [Fact]
    public async Task AddEventsAsync_DuplicateEventKeyAcrossRequests_SkipsAndCountsAsDuplicate()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await CreateFixtureAsync(context, "EventIdempotencyTestApp");
        var repository = new EventRepository(context);

        await repository.AddEventsAsync(appId, new[] { MakeEvent(appId, envId, "evt-dup") });
        var second = await repository.AddEventsAsync(appId, new[] { MakeEvent(appId, envId, "evt-dup") });

        Assert.Equal(0, second.Accepted);
        Assert.Equal(1, second.DuplicateEventKeysSkipped);
        Assert.Equal(1, await context.Events.CountAsync(e => e.EventKey == "evt-dup"));
    }

    [Fact]
    public async Task AddEventsAsync_DuplicateEventKeyWithinSameBatch_InsertsOnlyFirstOccurrence()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await CreateFixtureAsync(context, "EventIntraBatchDupTestApp");
        var repository = new EventRepository(context);

        var result = await repository.AddEventsAsync(appId, new[] { MakeEvent(appId, envId, "evt-same"), MakeEvent(appId, envId, "evt-same") });

        Assert.Equal(1, result.Accepted);
        Assert.Equal(1, result.DuplicateEventKeysSkipped);
        Assert.Equal(1, await context.Events.CountAsync(e => e.EventKey == "evt-same"));
    }

    [Fact]
    public async Task AddEventsAsync_EventKeyAlreadyPersistedByBypassedInsert_CountsAsDuplicateWithoutThrowing()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await CreateFixtureAsync(context, "EventUniqueViolationRetryTestApp");
        var repository = new EventRepository(context);

        // Simulate a concurrent request winning the (ApplicationId, EventKey) race: insert the
        // row directly against the DB, bypassing the repository's own existence check, right
        // before calling AddEventsAsync with the same key.
        context.Events.Add(MakeEvent(appId, envId, "evt-race"));
        await context.SaveChangesAsync();

        var result = await repository.AddEventsAsync(appId, new[] { MakeEvent(appId, envId, "evt-race") });

        Assert.Equal(0, result.Accepted);
        Assert.Equal(1, result.DuplicateEventKeysSkipped);
        Assert.Equal(1, await context.Events.CountAsync(e => e.EventKey == "evt-race"));
    }
}
