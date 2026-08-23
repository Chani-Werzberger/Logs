using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class EventRepositoryQueryTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> SeedAppAndEnvironmentAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();

        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();

        return (app.Id, env.Id);
    }

    private static Event BuildEvent(int appId, int envId, DateTime timestamp, int severity = 17, string? correlationId = null, string message = "test event") => new()
    {
        ApplicationId = appId, EnvironmentId = envId, Timestamp = timestamp, Severity = severity,
        CorrelationId = correlationId, Message = message
    };

    [Fact]
    public async Task QueryAsync_FiltersByApplicationEnvironmentAndSeverity_OrdersNewestFirstAndPaginates()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppAndEnvironmentAsync(context, "QueryFilterTestApp");
        var (otherAppId, otherEnvId) = await SeedAppAndEnvironmentAsync(context, "OtherApp");

        var now = DateTime.UtcNow;
        context.Events.AddRange(
            BuildEvent(appId, envId, now.AddMinutes(-1), severity: 17),
            BuildEvent(appId, envId, now.AddMinutes(-2), severity: 9),
            BuildEvent(appId, envId, now.AddMinutes(-3), severity: 17),
            BuildEvent(otherAppId, otherEnvId, now, severity: 17));
        await context.SaveChangesAsync();

        var repository = new EventRepository(context);
        var (items, totalCount) = await repository.QueryAsync(new EventQueryParameters(
            ApplicationId: appId, EnvironmentId: envId, From: null, To: null, Severity: 17,
            ModuleId: null, ScreenServiceId: null, ProcessId: null, OperationId: null,
            CorrelationId: null, TraceId: null, UserId: null, CustomerId: null,
            ExceptionGroupId: null, VersionId: null, DurationMinMs: null, DurationMaxMs: null,
            MessageContains: null, Page: 1, PageSize: 50));

        Assert.Equal(2, totalCount);
        Assert.Equal(2, items.Count);
        Assert.True(items[0].Timestamp > items[1].Timestamp);
    }

    [Fact]
    public async Task QueryAsync_PageSizeExceedsMax_IsClampedTo200()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppAndEnvironmentAsync(context, "ClampTestApp");
        var repository = new EventRepository(context);

        var (items, _) = await repository.QueryAsync(new EventQueryParameters(
            ApplicationId: appId, EnvironmentId: envId, From: null, To: null, Severity: null,
            ModuleId: null, ScreenServiceId: null, ProcessId: null, OperationId: null,
            CorrelationId: null, TraceId: null, UserId: null, CustomerId: null,
            ExceptionGroupId: null, VersionId: null, DurationMinMs: null, DurationMaxMs: null,
            MessageContains: null, Page: 1, PageSize: 5000));

        Assert.True(items.Count <= 200);
    }

    [Fact]
    public async Task GetByIdAsync_MismatchedApplicationId_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppAndEnvironmentAsync(context, "IdorTestApp");
        var (otherAppId, _) = await SeedAppAndEnvironmentAsync(context, "IdorOtherApp");
        var evt = BuildEvent(appId, envId, DateTime.UtcNow);
        context.Events.Add(evt);
        await context.SaveChangesAsync();

        var repository = new EventRepository(context);

        Assert.NotNull(await repository.GetByIdAsync(appId, evt.Id));
        Assert.Null(await repository.GetByIdAsync(otherAppId, evt.Id));
    }

    [Fact]
    public async Task GetTimelineAsync_ByCorrelationId_ReturnsOnlyMatchingEventsOrderedAscending()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppAndEnvironmentAsync(context, "TimelineTestApp");
        var now = DateTime.UtcNow;
        context.Events.AddRange(
            BuildEvent(appId, envId, now.AddMinutes(-2), correlationId: "order-1"),
            BuildEvent(appId, envId, now.AddMinutes(-1), correlationId: "order-1"),
            BuildEvent(appId, envId, now, correlationId: "order-2"));
        await context.SaveChangesAsync();

        var repository = new EventRepository(context);
        var timeline = await repository.GetTimelineAsync(new TimelineQuery(
            ApplicationId: appId, CorrelationId: "order-1", TraceId: null, OperationId: null, UserId: null, CustomerId: null));

        Assert.Equal(2, timeline.Count);
        Assert.True(timeline[0].Timestamp < timeline[1].Timestamp);
    }
}
