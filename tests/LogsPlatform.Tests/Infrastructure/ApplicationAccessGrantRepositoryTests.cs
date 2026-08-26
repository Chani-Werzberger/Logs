using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ApplicationAccessGrantRepositoryTests
{
    private static async Task<(PlatformUser User, Application App)> SeedAsync(LogsPlatformDbContext context, string username)
    {
        var user = new PlatformUser { Username = username, PasswordHash = "hash", IsAdmin = false, CreatedAt = DateTime.UtcNow };
        var app = new Application { Name = $"{username}-App", CreatedAt = DateTime.UtcNow };
        context.PlatformUsers.Add(user);
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        return (user, app);
    }

    [Fact]
    public async Task GrantAsync_ThenHasGrantAsync_ReturnsTrue()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "GrantRepoAddTestUser");
        var repository = new ApplicationAccessGrantRepository(TestDatabase.CreateFactory());

        await repository.GrantAsync(user.Id, app.Id);

        Assert.True(await repository.HasGrantAsync(user.Id, app.Id));
    }

    [Fact]
    public async Task HasGrantAsync_NoGrant_ReturnsFalse()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "GrantRepoNoGrantTestUser");
        var repository = new ApplicationAccessGrantRepository(TestDatabase.CreateFactory());

        Assert.False(await repository.HasGrantAsync(user.Id, app.Id));
    }

    [Fact]
    public async Task GrantAsync_CalledTwice_DoesNotDuplicate()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "GrantRepoDuplicateTestUser");
        var repository = new ApplicationAccessGrantRepository(TestDatabase.CreateFactory());

        await repository.GrantAsync(user.Id, app.Id);
        await repository.GrantAsync(user.Id, app.Id);

        var granted = await repository.GetGrantedApplicationIdsAsync(user.Id);
        Assert.Single(granted);
    }

    [Fact]
    public async Task RevokeAsync_RemovesGrant()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "GrantRepoRevokeTestUser");
        var repository = new ApplicationAccessGrantRepository(TestDatabase.CreateFactory());
        await repository.GrantAsync(user.Id, app.Id);

        await repository.RevokeAsync(user.Id, app.Id);

        Assert.False(await repository.HasGrantAsync(user.Id, app.Id));
    }

    [Fact]
    public async Task HasAnyGrantAsync_WithAtLeastOneGrant_ReturnsTrue()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "GrantRepoHasAnyTestUser");
        var repository = new ApplicationAccessGrantRepository(TestDatabase.CreateFactory());
        await repository.GrantAsync(user.Id, app.Id);

        Assert.True(await repository.HasAnyGrantAsync(user.Id));
    }

    [Fact]
    public async Task HasAnyGrantAsync_NoGrants_ReturnsFalse()
    {
        using var context = TestDatabase.CreateContext();
        var (user, _) = await SeedAsync(context, "GrantRepoHasAnyFalseTestUser");
        var repository = new ApplicationAccessGrantRepository(TestDatabase.CreateFactory());

        Assert.False(await repository.HasAnyGrantAsync(user.Id));
    }
}
