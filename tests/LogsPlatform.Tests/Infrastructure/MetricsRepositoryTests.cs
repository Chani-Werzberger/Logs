using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class MetricsRepositoryTests
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
    public async Task GetHourlyEventCountAsync_CountsOnlyEventsInTheHourWindow()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "MetricsCountTestApp");

        var hourStart = new DateTime(2026, 8, 23, 14, 0, 0, DateTimeKind.Utc);
        context.Events.AddRange(
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, Timestamp = hourStart.AddMinutes(10), Severity = 17, Message = "e1" },
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, Timestamp = hourStart.AddMinutes(50), Severity = 17, Message = "e2" },
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, Timestamp = hourStart.AddHours(-1), Severity = 17, Message = "outside" });
        await context.SaveChangesAsync();

        var repository = new MetricsRepository(context);
        var count = await repository.GetHourlyEventCountAsync(appId, envId, opId, hourStart);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GetHourlyAverageDurationAsync_AveragesOnlyEventsWithDuration()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "MetricsDurationTestApp");

        var hourStart = new DateTime(2026, 8, 23, 14, 0, 0, DateTimeKind.Utc);
        context.Events.AddRange(
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, Timestamp = hourStart.AddMinutes(10), Severity = 17, Message = "e1", DurationMs = 100 },
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, Timestamp = hourStart.AddMinutes(20), Severity = 17, Message = "e2", DurationMs = 200 },
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, Timestamp = hourStart.AddMinutes(30), Severity = 17, Message = "e3", DurationMs = null });
        await context.SaveChangesAsync();

        var repository = new MetricsRepository(context);
        var average = await repository.GetHourlyAverageDurationAsync(appId, envId, opId, hourStart);

        Assert.Equal(150, average);
    }

    [Fact]
    public async Task GetActiveOperationIdsAsync_ReturnsDistinctOperationsWithRecentEvents()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "MetricsActiveOpsTestApp");

        context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, Timestamp = DateTime.UtcNow, Severity = 17, Message = "recent" });
        await context.SaveChangesAsync();

        var repository = new MetricsRepository(context);
        var activeOps = await repository.GetActiveOperationIdsAsync(appId, envId);

        Assert.Contains(opId, activeOps);
    }

    [Fact]
    public async Task GetCustomerRatesAsync_GroupsCountsByCustomer()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "MetricsCustomerRatesTestApp");
        var customerA = new Customer { ApplicationId = appId, ExternalCustomerId = "cust-a", Name = "A" };
        var customerB = new Customer { ApplicationId = appId, ExternalCustomerId = "cust-b", Name = "B" };
        context.Customers.AddRange(customerA, customerB);
        await context.SaveChangesAsync();

        var windowStart = DateTime.UtcNow.AddHours(-1);
        context.Events.AddRange(
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customerA.Id, Timestamp = DateTime.UtcNow, Severity = 17, Message = "a1" },
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customerA.Id, Timestamp = DateTime.UtcNow, Severity = 17, Message = "a2" },
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customerB.Id, Timestamp = DateTime.UtcNow, Severity = 17, Message = "b1" });
        await context.SaveChangesAsync();

        var repository = new MetricsRepository(context);
        var rates = await repository.GetCustomerRatesAsync(appId, envId, opId, null, windowStart);

        Assert.Equal(2, rates[customerA.Id]);
        Assert.Equal(1, rates[customerB.Id]);
    }
}
