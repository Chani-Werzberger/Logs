using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class AppVersionRepositoryTests
{
    private static async Task<int> CreateTestApplicationAsync(LogsPlatformDbContext context, string name)
    {
        var application = new Application { Name = name, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsVersion_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppVersionAddTestApp");
        var repository = new AppVersionRepository(TestDatabase.CreateFactory());

        var created = await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0", ReleaseNotes = "Initial release", CreatedAt = DateTime.UtcNow });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("1.0.0", loaded!.VersionNumber);
        Assert.Equal("Initial release", loaded.ReleaseNotes);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByApplicationIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppVersionFilterTestApp");
        var repository = new AppVersionRepository(TestDatabase.CreateFactory());

        var active = await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0", CreatedAt = DateTime.UtcNow });
        var toDeactivate = await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "0.9.0", CreatedAt = DateTime.UtcNow });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByApplicationIdAsync(appId);
        var withInactive = await repository.GetByApplicationIdAsync(appId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesReleaseNotes_LeavesVersionNumberUnchanged()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppVersionRenameTestApp");
        var repository = new AppVersionRepository(TestDatabase.CreateFactory());
        var created = await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0", ReleaseNotes = "OldNotes", CreatedAt = DateTime.UtcNow });

        var renamed = await repository.RenameAsync(created.Id, "NewNotes");

        Assert.Equal("NewNotes", renamed.ReleaseNotes);
        Assert.Equal("1.0.0", renamed.VersionNumber);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppVersionDeactivateTestApp");
        var repository = new AppVersionRepository(TestDatabase.CreateFactory());
        var created = await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0", CreatedAt = DateTime.UtcNow });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateVersionNumberFailure_SubsequentUniqueVersionNumberStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppVersionCircuitTestApp");
        var repository = new AppVersionRepository(TestDatabase.CreateFactory());

        await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0-dup", CreatedAt = DateTime.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0-dup", CreatedAt = DateTime.UtcNow }));

        var created = await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0-unique", CreatedAt = DateTime.UtcNow });

        Assert.Equal("1.0.0-unique", created.VersionNumber);
    }
}
