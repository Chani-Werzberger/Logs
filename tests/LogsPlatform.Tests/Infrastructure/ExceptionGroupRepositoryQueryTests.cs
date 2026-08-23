using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ExceptionGroupRepositoryQueryTests
{
    private static async Task<(int ApplicationId, int EnvironmentId, int ModuleId, int ScreenServiceId, int ProcessId, int OperationId)> SeedFullHierarchyAsync(LogsPlatformDbContext context, string appName)
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

        return (app.Id, env.Id, module.Id, screenService.Id, process.Id, operation.Id);
    }

    [Fact]
    public async Task QueryAsync_FiltersByApplicationAndSortsByLastSeenAt()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _, _, _, _, _) = await SeedFullHierarchyAsync(context, "ExGroupQueryTestApp");
        var otherApp = new Application { Name = "OtherExGroupApp", CreatedAt = DateTime.UtcNow };
        context.Applications.Add(otherApp);
        await context.SaveChangesAsync();

        var now = DateTime.UtcNow;
        context.ExceptionGroups.AddRange(
            new ExceptionGroup { ApplicationId = appId, Fingerprint = "fp-1", ExceptionType = "A", MessageTemplate = "a", RepresentativeStackTrace = "at A()", FirstSeenAt = now.AddDays(-2), LastSeenAt = now.AddDays(-2), OccurrenceCount = 1 },
            new ExceptionGroup { ApplicationId = appId, Fingerprint = "fp-2", ExceptionType = "B", MessageTemplate = "b", RepresentativeStackTrace = "at B()", FirstSeenAt = now.AddDays(-1), LastSeenAt = now, OccurrenceCount = 1 },
            new ExceptionGroup { ApplicationId = otherApp.Id, Fingerprint = "fp-3", ExceptionType = "C", MessageTemplate = "c", RepresentativeStackTrace = "at C()", FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
        await context.SaveChangesAsync();

        var repository = new ExceptionGroupRepository(context);
        var result = await repository.QueryAsync(new ExceptionGroupQueryParameters(appId, From: null, To: null, SortBy: "LastSeenAt"));

        Assert.Equal(2, result.Count);
        Assert.Equal("fp-2", result[0].Fingerprint);
    }

    [Fact]
    public async Task GetDailyCountsAsync_CountsEventsPerDayWithinWindow()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, _, _, _, _) = await SeedFullHierarchyAsync(context, "DailyCountsTestApp");
        var group = new ExceptionGroup { ApplicationId = appId, Fingerprint = "fp-daily", ExceptionType = "E", MessageTemplate = "e", RepresentativeStackTrace = "at E()", FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow, OccurrenceCount = 2 };
        context.ExceptionGroups.Add(group);
        await context.SaveChangesAsync();

        var today = DateTime.UtcNow;
        context.Events.AddRange(
            new Event { ApplicationId = appId, EnvironmentId = envId, Timestamp = today, Severity = 17, Message = "e1", ExceptionGroupId = group.Id },
            new Event { ApplicationId = appId, EnvironmentId = envId, Timestamp = today, Severity = 17, Message = "e2", ExceptionGroupId = group.Id },
            new Event { ApplicationId = appId, EnvironmentId = envId, Timestamp = today.AddDays(-20), Severity = 17, Message = "e3", ExceptionGroupId = group.Id });
        await context.SaveChangesAsync();

        var repository = new ExceptionGroupRepository(context);
        var counts = await repository.GetDailyCountsAsync(group.Id, days: 14);

        Assert.Equal(2, counts[DateOnly.FromDateTime(today)]);
        Assert.DoesNotContain(DateOnly.FromDateTime(today.AddDays(-20)), counts.Keys);
    }

    [Fact]
    public async Task GetAffectedContextsAsync_ReturnsDistinctApplicationEnvironmentVersionOperation()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, _, _, _, operationId) = await SeedFullHierarchyAsync(context, "AffectedContextsTestApp");
        var group = new ExceptionGroup { ApplicationId = appId, Fingerprint = "fp-ctx", ExceptionType = "E", MessageTemplate = "e", RepresentativeStackTrace = "at E()", FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow, OccurrenceCount = 2 };
        context.ExceptionGroups.Add(group);
        await context.SaveChangesAsync();

        context.Events.AddRange(
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = operationId, Timestamp = DateTime.UtcNow, Severity = 17, Message = "e1", ExceptionGroupId = group.Id },
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = operationId, Timestamp = DateTime.UtcNow, Severity = 17, Message = "e2", ExceptionGroupId = group.Id });
        await context.SaveChangesAsync();

        var repository = new ExceptionGroupRepository(context);
        var contexts = await repository.GetAffectedContextsAsync(group.Id);

        Assert.Single(contexts);
        Assert.Equal("Authorize", contexts[0].OperationName);
    }
}
