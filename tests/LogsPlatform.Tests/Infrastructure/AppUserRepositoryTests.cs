using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class AppUserRepositoryTests
{
    private static async Task<int> CreateTestApplicationAsync(LogsPlatformDbContext context, string name)
    {
        var application = new Application { Name = name, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsAppUser_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppUserAddTestApp");
        var repository = new AppUserRepository(context);

        var created = await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-1", DisplayName = "Jane Doe" });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("user-1", loaded!.ExternalUserId);
        Assert.Equal("Jane Doe", loaded.DisplayName);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByApplicationIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppUserFilterTestApp");
        var repository = new AppUserRepository(context);

        var active = await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-active", DisplayName = "Active" });
        var toDeactivate = await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-inactive", DisplayName = "WillBeInactive" });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByApplicationIdAsync(appId);
        var withInactive = await repository.GetByApplicationIdAsync(appId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesDisplayName_LeavesExternalUserIdUnchanged()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppUserRenameTestApp");
        var repository = new AppUserRepository(context);
        var created = await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-1", DisplayName = "OldName" });

        var renamed = await repository.RenameAsync(created.Id, "NewName");

        Assert.Equal("NewName", renamed.DisplayName);
        Assert.Equal("user-1", renamed.ExternalUserId);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppUserDeactivateTestApp");
        var repository = new AppUserRepository(context);
        var created = await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-1", DisplayName = "ToDeactivate" });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateExternalIdFailure_SubsequentUniqueExternalIdStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppUserCircuitTestApp");
        var repository = new AppUserRepository(context);

        await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-dup", DisplayName = "First" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-dup", DisplayName = "Second" }));

        var created = await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-unique", DisplayName = "Third" });

        Assert.Equal("user-unique", created.ExternalUserId);
    }
}
