using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure.Repositories;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ApplicationRepositoryTests
{
    [Fact]
    public async Task AddAsync_PersistsApplication_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
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
        using var context = TestDatabase.CreateContext();
        var repository = new ApplicationRepository(context);

        await repository.AddAsync(new Application { Name = "RetailPulse", CreatedAt = DateTime.UtcNow });
        await repository.AddAsync(new Application { Name = "FieldOps", CreatedAt = DateTime.UtcNow });

        var all = await repository.GetAllAsync();

        Assert.Equal(2, all.Count);
    }
}
