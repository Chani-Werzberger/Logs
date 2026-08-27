using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ApplicationRepositoryTests
{
    [Fact]
    public async Task AddAsync_PersistsApplication_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new ApplicationRepository(TestDatabase.CreateFactory());

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
        var repository = new ApplicationRepository(TestDatabase.CreateFactory());

        await repository.AddAsync(new Application { Name = "RetailPulse", CreatedAt = DateTime.UtcNow });
        await repository.AddAsync(new Application { Name = "FieldOps", CreatedAt = DateTime.UtcNow });

        var all = await repository.GetAllAsync();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateNameFailure_SubsequentUniqueNameStillSucceeds()
    {
        // Reproduces the exact Blazor-Server circuit-scoped-DbContext bug found in final review:
        // a single DbContext instance (matching one Blazor circuit's lifetime) used across
        // multiple sequential AddAsync calls, where an earlier failure must not poison later ones.
        using var context = TestDatabase.CreateContext();
        var repository = new ApplicationRepository(TestDatabase.CreateFactory());

        await repository.AddAsync(new Application { Name = "CircuitTest1", CreatedAt = DateTime.UtcNow });

        // Second insert with the same Name fails on the unique index -- on the SAME context/repository instance,
        // simulating two form submissions on the same live Blazor circuit (no page reload in between).
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new Application { Name = "CircuitTest1", CreatedAt = DateTime.UtcNow }));

        // Before the fix: this next call would ALSO fail, because the rejected "CircuitTest1" from the
        // previous call is still tracked as Added and gets re-sent alongside this genuinely new, unique row.
        var created = await repository.AddAsync(new Application { Name = "CircuitTest2", CreatedAt = DateTime.UtcNow });

        Assert.Equal("CircuitTest2", created.Name);
        var all = await repository.GetAllAsync();
        Assert.Contains(all, a => a.Name == "CircuitTest2");
    }

    [Fact]
    public async Task UpdateRetentionAsync_ExistingApplication_UpdatesAndReturnsIt()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new ApplicationRepository(TestDatabase.CreateFactory());
        var created = await repository.AddAsync(new Application { Name = "RetentionUpdateTestApp", CreatedAt = DateTime.UtcNow });

        var updated = await repository.UpdateRetentionAsync(created.Id, 30);

        Assert.NotNull(updated);
        Assert.Equal(30, updated!.RetentionDays);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.Equal(30, reloaded!.RetentionDays);
    }

    [Fact]
    public async Task UpdateRetentionAsync_NoSuchApplication_ReturnsNull()
    {
        var repository = new ApplicationRepository(TestDatabase.CreateFactory());

        var updated = await repository.UpdateRetentionAsync(999999, 30);

        Assert.Null(updated);
    }
}
