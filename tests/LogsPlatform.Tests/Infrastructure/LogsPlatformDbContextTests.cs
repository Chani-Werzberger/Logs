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
}
