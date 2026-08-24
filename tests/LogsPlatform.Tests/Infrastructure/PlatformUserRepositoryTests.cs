using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class PlatformUserRepositoryTests
{
    private static LogsPlatformDbContext CreateUntrackedContext()
    {
        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString)
            .Options;
        return new LogsPlatformDbContext(options);
    }

    [Fact]
    public async Task AddAsync_PersistsUser_PasswordHashIsNeverThePlaintextPassword()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new PlatformUserRepository(context);
        const string plaintextPassword = "correct horse battery staple";

        var created = await repository.AddAsync(new PlatformUser
        {
            Username = "PlatformUserAddTest",
            PasswordHash = PasswordHasher.Hash(plaintextPassword),
            IsAdmin = true,
            CreatedAt = DateTime.UtcNow
        });

        await using var verify = CreateUntrackedContext();
        var loaded = await verify.Set<PlatformUser>().SingleAsync(u => u.Id == created.Id);

        Assert.NotEqual(plaintextPassword, loaded.PasswordHash);
        Assert.DoesNotContain(plaintextPassword, loaded.PasswordHash);
        Assert.True(PasswordHasher.Verify(plaintextPassword, loaded.PasswordHash));
    }

    [Fact]
    public async Task GetByUsernameAsync_ExistingUser_ReturnsIt()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new PlatformUserRepository(context);
        await repository.AddAsync(new PlatformUser
        {
            Username = "PlatformUserLookupTest",
            PasswordHash = PasswordHasher.Hash("irrelevant"),
            CreatedAt = DateTime.UtcNow
        });

        var found = await repository.GetByUsernameAsync("PlatformUserLookupTest");

        Assert.NotNull(found);
        Assert.Equal("PlatformUserLookupTest", found!.Username);
    }

    [Fact]
    public async Task GetByUsernameAsync_UnknownUsername_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new PlatformUserRepository(context);

        var found = await repository.GetByUsernameAsync("no-such-user");

        Assert.Null(found);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new PlatformUserRepository(context);
        var created = await repository.AddAsync(new PlatformUser
        {
            Username = "PlatformUserDeactivateTest",
            PasswordHash = PasswordHasher.Hash("irrelevant"),
            CreatedAt = DateTime.UtcNow
        });

        await repository.DeactivateAsync(created.Id);

        await using var verify = CreateUntrackedContext();
        var loaded = await verify.Set<PlatformUser>().SingleAsync(u => u.Id == created.Id);
        Assert.False(loaded.IsActive);
    }

    [Fact]
    public async Task AnyAsync_NoUsers_ReturnsFalse()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new PlatformUserRepository(context);

        Assert.False(await repository.AnyAsync());
    }

    [Fact]
    public async Task AnyAsync_OneUserExists_ReturnsTrue()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new PlatformUserRepository(context);
        await repository.AddAsync(new PlatformUser
        {
            Username = "PlatformUserAnyTest",
            PasswordHash = PasswordHasher.Hash("irrelevant"),
            CreatedAt = DateTime.UtcNow
        });

        Assert.True(await repository.AnyAsync());
    }
}
