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
}
