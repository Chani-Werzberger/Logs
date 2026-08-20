using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class LogsPlatformDbContextTests
{
    [Fact]
    public async Task CanInsertAndRetrieveApplicationWithEnvironment()
    {
        using var context = TestDatabase.CreateContext();

        var application = new Application
        {
            Name = "RetailPulse",
            Description = "E-commerce simulation app",
            CreatedAt = DateTime.UtcNow
        };
        application.Environments.Add(new AppEnvironment { Name = "Production", IsProduction = true });

        context.Applications.Add(application);
        await context.SaveChangesAsync();

        using var readContext = new LogsPlatformDbContext(
            new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

        var loaded = await readContext.Applications
            .Include(a => a.Environments)
            .FirstAsync(a => a.Name == "RetailPulse");

        Assert.Single(loaded.Environments);
        Assert.Equal("Production", loaded.Environments.First().Name);
        Assert.True(loaded.Environments.First().IsProduction);
    }

    [Fact]
    public async Task UnspecifiedDateTime_RoundTripsWithoutLocalOffsetShift()
    {
        using var context = TestDatabase.CreateContext();

        // Simulates a timestamp deserialized from JSON with no "Z"/offset suffix —
        // Kind.Unspecified, NOT Kind.Local. The bug this test guards against:
        // ToUniversalTime() would (wrongly) treat this as local time and shift it
        // by the machine's UTC offset before storing it.
        var unspecified = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var application = new Application
        {
            Name = "UtcConversionTest",
            CreatedAt = unspecified
        };

        context.Applications.Add(application);
        await context.SaveChangesAsync();

        using var readContext = new LogsPlatformDbContext(
            new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

        var loaded = await readContext.Applications.FirstAsync(a => a.Name == "UtcConversionTest");

        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAt.Kind);
        Assert.Equal(unspecified.Year, loaded.CreatedAt.Year);
        Assert.Equal(unspecified.Month, loaded.CreatedAt.Month);
        Assert.Equal(unspecified.Day, loaded.CreatedAt.Day);
        Assert.Equal(unspecified.Hour, loaded.CreatedAt.Hour);
        Assert.Equal(unspecified.Minute, loaded.CreatedAt.Minute);
        // Clock components must be unchanged — only the Kind label should differ.
        // If ToUniversalTime() shifted this by the local UTC offset, Hour (or Day, near
        // midnight) would differ from the original Unspecified value.
    }

    [Fact]
    public async Task CanInsertAndRetrieveModuleWithScreenService()
    {
        using var context = TestDatabase.CreateContext();

        var application = new Application { Name = "HierarchyDbContextTestApp", CreatedAt = DateTime.UtcNow };
        var module = new AppModule { Name = "Payments" };
        module.ScreenServices.Add(new ScreenService { Name = "PaymentGateway", Type = ScreenServiceType.Service });
        application.Modules.Add(module);

        context.Applications.Add(application);
        await context.SaveChangesAsync();

        using var readContext = new LogsPlatformDbContext(
            new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

        var loaded = await readContext.Modules
            .Include(m => m.ScreenServices)
            .FirstAsync(m => m.Name == "Payments");

        Assert.True(loaded.IsActive);
        Assert.Single(loaded.ScreenServices);
        Assert.Equal("PaymentGateway", loaded.ScreenServices.First().Name);
        Assert.Equal(ScreenServiceType.Service, loaded.ScreenServices.First().Type);
        Assert.True(loaded.ScreenServices.First().IsActive);
    }

    [Fact]
    public async Task CanInsertAndRetrieveProcessNodeWithOperation()
    {
        using var context = TestDatabase.CreateContext();

        var application = new Application { Name = "HierarchyDbContextTestApp2", CreatedAt = DateTime.UtcNow };
        var module = new AppModule { Name = "Payments" };
        var screenService = new ScreenService { Name = "PaymentGateway", Type = ScreenServiceType.Service };
        var process = new ProcessNode { Name = "ChargeCard" };
        process.Operations.Add(new Operation { Name = "AuthorizePayment" });
        screenService.Processes.Add(process);
        module.ScreenServices.Add(screenService);
        application.Modules.Add(module);

        context.Applications.Add(application);
        await context.SaveChangesAsync();

        using var readContext = new LogsPlatformDbContext(
            new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

        var loaded = await readContext.Processes
            .Include(p => p.Operations)
            .FirstAsync(p => p.Name == "ChargeCard");

        Assert.True(loaded.IsActive);
        Assert.Single(loaded.Operations);
        Assert.Equal("AuthorizePayment", loaded.Operations.First().Name);
        Assert.True(loaded.Operations.First().IsActive);
    }

    [Fact]
    public async Task CanInsertAndRetrieveCustomerAppUserLogSource()
    {
        using var context = TestDatabase.CreateContext();

        var application = new Application { Name = "GroupB1DbContextTestApp", CreatedAt = DateTime.UtcNow };
        application.Customers.Add(new Customer { ExternalCustomerId = "cust-1", Name = "Acme Corp" });
        application.Users.Add(new AppUser { ExternalUserId = "user-1", DisplayName = "Jane Doe" });
        application.LogSources.Add(new LogSource { Name = "PaymentServiceLogs", Description = "Structured logs from the payment microservice" });

        context.Applications.Add(application);
        await context.SaveChangesAsync();

        using var readContext = new LogsPlatformDbContext(
            new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

        var loadedApp = await readContext.Applications
            .Include(a => a.Customers)
            .Include(a => a.Users)
            .Include(a => a.LogSources)
            .FirstAsync(a => a.Name == "GroupB1DbContextTestApp");

        Assert.Single(loadedApp.Customers);
        Assert.Equal("cust-1", loadedApp.Customers.First().ExternalCustomerId);
        Assert.True(loadedApp.Customers.First().IsActive);
        Assert.Single(loadedApp.Users);
        Assert.Equal("user-1", loadedApp.Users.First().ExternalUserId);
        Assert.True(loadedApp.Users.First().IsActive);
        Assert.Single(loadedApp.LogSources);
        Assert.Equal("PaymentServiceLogs", loadedApp.LogSources.First().Name);
        Assert.True(loadedApp.LogSources.First().IsActive);
    }

    [Fact]
    public async Task CanInsertAndRetrieveApiKey()
    {
        using var context = TestDatabase.CreateContext();

        var application = new Application { Name = "ApiKeyDbContextTestApp", CreatedAt = DateTime.UtcNow };
        application.ApiKeys.Add(new ApiKey { KeyHash = new string('a', 64), Label = "CI pipeline key", CreatedAt = DateTime.UtcNow });

        context.Applications.Add(application);
        await context.SaveChangesAsync();

        using var readContext = new LogsPlatformDbContext(
            new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

        var loadedApp = await readContext.Applications
            .Include(a => a.ApiKeys)
            .FirstAsync(a => a.Name == "ApiKeyDbContextTestApp");

        Assert.Single(loadedApp.ApiKeys);
        Assert.Equal("CI pipeline key", loadedApp.ApiKeys.First().Label);
        Assert.Null(loadedApp.ApiKeys.First().RevokedAt);
        Assert.Equal(new string('a', 64), loadedApp.ApiKeys.First().KeyHash);
    }

    [Fact]
    public async Task CanInsertAndRetrieveAppVersionAndDeployment()
    {
        using var context = TestDatabase.CreateContext();

        var application = new Application { Name = "B3DbContextTestApp", CreatedAt = DateTime.UtcNow };
        var environment = new AppEnvironment { Name = "Production", IsProduction = true };
        application.Environments.Add(environment);
        var version = new AppVersion { VersionNumber = "1.0.0", ReleaseNotes = "Initial release", CreatedAt = DateTime.UtcNow };
        application.Versions.Add(version);

        context.Applications.Add(application);
        await context.SaveChangesAsync();

        application.Deployments.Add(new Deployment
        {
            ApplicationId = application.Id,
            EnvironmentId = environment.Id,
            VersionId = version.Id,
            DeployedAt = DateTime.UtcNow,
            Notes = "First deploy"
        });
        await context.SaveChangesAsync();

        using var readContext = new LogsPlatformDbContext(
            new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

        var loadedApp = await readContext.Applications
            .Include(a => a.Versions)
            .Include(a => a.Deployments)
            .FirstAsync(a => a.Name == "B3DbContextTestApp");

        Assert.Single(loadedApp.Versions);
        Assert.Equal("1.0.0", loadedApp.Versions.First().VersionNumber);
        Assert.True(loadedApp.Versions.First().IsActive);
        Assert.Single(loadedApp.Deployments);
        Assert.Equal("First deploy", loadedApp.Deployments.First().Notes);
        Assert.True(loadedApp.Deployments.First().IsActive);
    }

    [Fact]
    public async Task CanInsertAndRetrieveEventAndExceptionGroup()
    {
        using var context = TestDatabase.CreateContext();

        var application = new Application { Name = "M2aDbContextTestApp", CreatedAt = DateTime.UtcNow };
        var environment = new AppEnvironment { Name = "Production", IsProduction = true };
        application.Environments.Add(environment);
        context.Applications.Add(application);
        await context.SaveChangesAsync();

        var group = new ExceptionGroup
        {
            ApplicationId = application.Id,
            Fingerprint = "abc123",
            ExceptionType = "System.InvalidOperationException",
            MessageTemplate = "Something failed",
            RepresentativeStackTrace = "at Foo.Bar()",
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            OccurrenceCount = 1
        };
        context.ExceptionGroups.Add(group);
        await context.SaveChangesAsync();

        context.Events.Add(new Event
        {
            ApplicationId = application.Id,
            EnvironmentId = environment.Id,
            Timestamp = DateTime.UtcNow,
            Severity = 17,
            Message = "Card authorization failed",
            ExceptionGroupId = group.Id,
            EventKey = "evt-1"
        });
        await context.SaveChangesAsync();

        using var readContext = new LogsPlatformDbContext(
            new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

        var loadedEvent = await readContext.Events.SingleAsync(e => e.EventKey == "evt-1");
        Assert.Equal("Card authorization failed", loadedEvent.Message);
        Assert.Equal(group.Id, loadedEvent.ExceptionGroupId);

        var loadedGroup = await readContext.ExceptionGroups.SingleAsync(g => g.Fingerprint == "abc123");
        Assert.Equal(1, loadedGroup.OccurrenceCount);
    }
}
