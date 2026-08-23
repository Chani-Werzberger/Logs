using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class CustomerOutlierDetectorTests
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
    public async Task RunAsync_OneCustomerFarAbovePeers_CreatesCustomerAnomalyFinding()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "CustomerOutlierSpikeTestApp");

        var customers = new List<Customer>();
        for (var i = 0; i < 6; i++)
        {
            var customer = new Customer { ApplicationId = appId, ExternalCustomerId = $"cust-{i}", Name = $"Customer {i}" };
            customers.Add(customer);
        }
        context.Customers.AddRange(customers);
        await context.SaveChangesAsync();

        var now = DateTime.UtcNow;
        // 5 peers with 2 events each; the 6th customer has 50.
        for (var i = 0; i < 5; i++)
        {
            context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customers[i].Id, Timestamp = now, Severity = 17, Message = $"peer-{i}-a" });
            context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customers[i].Id, Timestamp = now, Severity = 17, Message = $"peer-{i}-b" });
        }
        for (var i = 0; i < 50; i++)
        {
            context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customers[5].Id, Timestamp = now, Severity = 17, Message = $"outlier-{i}" });
        }
        await context.SaveChangesAsync();

        var metrics = new MetricsRepository(context);
        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var detector = new CustomerOutlierDetector(metrics, writer);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.CustomerAnomaly);

        Assert.NotNull(finding);
    }

    [Fact]
    public async Task RunAsync_FewerThanMinPeerCustomers_NoFindingCreated()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "CustomerOutlierFewPeersTestApp");

        var customers = new List<Customer>();
        for (var i = 0; i < 3; i++)
        {
            customers.Add(new Customer { ApplicationId = appId, ExternalCustomerId = $"cust-{i}", Name = $"Customer {i}" });
        }
        context.Customers.AddRange(customers);
        await context.SaveChangesAsync();

        var now = DateTime.UtcNow;
        context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customers[0].Id, Timestamp = now, Severity = 17, Message = "a" });
        context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customers[1].Id, Timestamp = now, Severity = 17, Message = "b" });
        context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customers[2].Id, Timestamp = now, Severity = 17, Message = "c" });
        await context.SaveChangesAsync();

        var metrics = new MetricsRepository(context);
        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var detector = new CustomerOutlierDetector(metrics, writer);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var findingCount = await verifyContext.Findings.CountAsync(f => f.ApplicationId == appId);

        Assert.Equal(0, findingCount);
    }
}
