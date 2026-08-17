using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ApplicationRepositoryTests
{
    private const string TestConnectionString =
        "Server=(localdb)\\mssqllocaldb;Database=LogsPlatformTests;Trusted_Connection=True;";

    private static LogsPlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>()
            .UseSqlServer(TestConnectionString)
            .Options;
        var context = new LogsPlatformDbContext(options);
        context.Database.EnsureDeleted();
        context.Database.Migrate();
        return context;
    }

    [Fact]
    public async Task AddAsync_PersistsApplication_RetrievableByGetByIdAsync()
    {
        using var context = CreateContext();
        var repository = new ApplicationRepository(context);

        var created = await repository.AddAsync(new Application
        {
            Name = "FieldOps",
            Description = "Field-service scheduling simulation app",
            CreatedAt = DateTime.UtcNow
        });

        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("FieldOps", loaded!.Name);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllPersistedApplications()
    {
        using var context = CreateContext();
        var repository = new ApplicationRepository(context);

        await repository.AddAsync(new Application { Name = "RetailPulse", CreatedAt = DateTime.UtcNow });
        await repository.AddAsync(new Application { Name = "FieldOps", CreatedAt = DateTime.UtcNow });

        var all = await repository.GetAllAsync();

        Assert.Equal(2, all.Count);
    }
}
