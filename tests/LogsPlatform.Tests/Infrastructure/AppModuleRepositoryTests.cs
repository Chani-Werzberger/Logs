using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class AppModuleRepositoryTests
{
    private static async Task<int> CreateTestApplicationAsync(LogsPlatformDbContext context, string name)
    {
        var application = new Application { Name = name, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsModule_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ModuleAddTestApp");
        var repository = new AppModuleRepository(context);

        var created = await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "Payments" });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Payments", loaded!.Name);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByApplicationIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ModuleFilterTestApp");
        var repository = new AppModuleRepository(context);

        var active = await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "Active" });
        var toDeactivate = await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "WillBeInactive" });
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
        var appId = await CreateTestApplicationAsync(context, "ModuleRenameTestApp");
        var repository = new AppModuleRepository(context);
        var created = await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "OldName" });

        var renamed = await repository.RenameAsync(created.Id, "NewName", "new description");

        Assert.Equal("NewName", renamed.Name);
        Assert.Equal("new description", renamed.Description);
        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.Equal("NewName", reloaded!.Name);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ModuleDeactivateTestApp");
        var repository = new AppModuleRepository(context);
        var created = await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "ToDeactivate" });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateNameFailure_SubsequentUniqueNameStillSucceeds()
    {
        // Same circuit-scoped-DbContext regression this project already found and fixed once
        // (see the prior plan's final review) -- proactively guarded here from Task 3 onward.
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ModuleCircuitTestApp");
        var repository = new AppModuleRepository(context);

        await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "DupModule" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "DupModule" }));

        var created = await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "UniqueModule" });

        Assert.Equal("UniqueModule", created.Name);
    }

    [Fact]
    public async Task RenameAsync_ToExistingSiblingName_ThrowsAndSubsequentWriteStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ModuleRenameConflictTestApp");
        var repository = new AppModuleRepository(context);
        await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "Taken" });
        var toRename = await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "ToRename" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.RenameAsync(toRename.Id, "Taken", null));

        var created = await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "StillWorks" });
        Assert.Equal("StillWorks", created.Name);
    }
}
