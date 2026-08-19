// tests/LogsPlatform.Tests/Infrastructure/LogSourceRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class LogSourceRepositoryTests
{
    private static async Task<int> CreateTestApplicationAsync(LogsPlatformDbContext context, string name)
    {
        var application = new Application { Name = name, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsLogSource_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "LogSourceAddTestApp");
        var repository = new LogSourceRepository(context);

        var created = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "PaymentServiceLogs", Description = "Structured logs" });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("PaymentServiceLogs", loaded!.Name);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByApplicationIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "LogSourceFilterTestApp");
        var repository = new LogSourceRepository(context);

        var active = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "Active" });
        var toDeactivate = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "WillBeInactive" });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByApplicationIdAsync(appId);
        var withInactive = await repository.GetByApplicationIdAsync(appId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesNameAndDescription()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "LogSourceRenameTestApp");
        var repository = new LogSourceRepository(context);
        var created = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "OldName" });

        var renamed = await repository.RenameAsync(created.Id, "NewName", "new description");

        Assert.Equal("NewName", renamed.Name);
        Assert.Equal("new description", renamed.Description);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "LogSourceDeactivateTestApp");
        var repository = new LogSourceRepository(context);
        var created = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "ToDeactivate" });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateNameFailure_SubsequentUniqueNameStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "LogSourceCircuitTestApp");
        var repository = new LogSourceRepository(context);

        await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "DupSource" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "DupSource" }));

        var created = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "UniqueSource" });

        Assert.Equal("UniqueSource", created.Name);
    }

    [Fact]
    public async Task RenameAsync_ToExistingSiblingName_ThrowsAndSubsequentWriteStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "LogSourceRenameConflictTestApp");
        var repository = new LogSourceRepository(context);
        await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "Taken" });
        var toRename = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "ToRename" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.RenameAsync(toRename.Id, "Taken", null));

        var created = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "StillWorks" });
        Assert.Equal("StillWorks", created.Name);
    }
}
