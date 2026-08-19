// tests/LogsPlatform.Tests/Infrastructure/ApiKeyRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ApiKeyRepositoryTests
{
    private static async Task<int> CreateTestApplicationAsync(LogsPlatformDbContext context, string name)
    {
        var application = new Application { Name = name, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application.Id;
    }

    // TestDatabase.CreateContext() runs EnsureDeleted()+Migrate() on every call, which wipes
    // the shared database. That's fine when called once (or before any writes), but a later
    // "verify against a fresh, untracked context" step must NOT re-migrate, or it destroys the
    // rows it's trying to read back. This attaches a brand-new, untracked context to the
    // already-migrated database instead.
    private static LogsPlatformDbContext CreateUntrackedContext()
    {
        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString)
            .Options;
        return new LogsPlatformDbContext(options);
    }

    [Fact]
    public async Task AddAsync_PersistsApiKey_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ApiKeyAddTestApp");
        var repository = new ApiKeyRepository(context);

        var (created, rawKey) = await repository.AddAsync(appId, "CI pipeline key");
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("CI pipeline key", loaded!.Label);
        Assert.Null(loaded.RevokedAt);
        Assert.NotEmpty(rawKey);
    }

    [Fact]
    public async Task AddAsync_RawKeyHasExpectedPrefix_AndIsNotStoredInKeyHash()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ApiKeyPrefixTestApp");
        var repository = new ApiKeyRepository(context);

        var (created, rawKey) = await repository.AddAsync(appId, "Prefix test key");

        Assert.StartsWith("lgp_", rawKey);
        Assert.NotEqual(rawKey, created.KeyHash);
        Assert.NotEmpty(created.KeyHash);
    }

    [Fact]
    public async Task AddAsync_TwoCalls_ProduceDifferentRawKeysAndHashes()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ApiKeyUniquenessTestApp");
        var repository = new ApiKeyRepository(context);

        var (first, firstRawKey) = await repository.AddAsync(appId, "Key A");
        var (second, secondRawKey) = await repository.AddAsync(appId, "Key B");

        Assert.NotEqual(firstRawKey, secondRawKey);
        Assert.NotEqual(first.KeyHash, second.KeyHash);
    }

    [Fact]
    public async Task GetByApplicationIdAsync_ExcludesRevokedByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ApiKeyFilterTestApp");
        var repository = new ApiKeyRepository(context);

        var (active, _) = await repository.AddAsync(appId, "Active");
        var (toRevoke, _) = await repository.AddAsync(appId, "WillBeRevoked");
        await repository.RevokeAsync(toRevoke.Id);

        var defaultResult = await repository.GetByApplicationIdAsync(appId);
        var withRevoked = await repository.GetByApplicationIdAsync(appId, includeRevoked: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withRevoked.Count);
    }

    [Fact]
    public async Task RevokeAsync_SetsRevokedAt()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ApiKeyRevokeTestApp");
        var repository = new ApiKeyRepository(context);
        var (created, _) = await repository.AddAsync(appId, "ToRevoke");

        await repository.RevokeAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.NotNull(reloaded!.RevokedAt);
    }

    [Fact]
    public async Task RevokeAsync_CalledTwice_LeavesOriginalRevokedAtUnchanged()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ApiKeyDoubleRevokeTestApp");
        var repository = new ApiKeyRepository(context);
        var (created, _) = await repository.AddAsync(appId, "DoubleRevoke");

        await repository.RevokeAsync(created.Id);
        var firstRevokedAt = (await repository.GetByIdAsync(created.Id))!.RevokedAt;

        await Task.Delay(50);
        await repository.RevokeAsync(created.Id);
        var secondRevokedAt = (await repository.GetByIdAsync(created.Id))!.RevokedAt;

        Assert.Equal(firstRevokedAt, secondRevokedAt);
    }

    [Fact]
    public async Task RevokeAsync_WithStaleTrackedInstance_DoesNotOverwriteRealRevocation()
    {
        using var contextA = TestDatabase.CreateContext();
        using var contextB = TestDatabase.CreateContext();
        var repoA = new ApiKeyRepository(contextA);
        var repoB = new ApiKeyRepository(contextB);
        var appId = await CreateTestApplicationAsync(contextA, "ApiKeyStaleTrackerTestApp");

        var (created, _) = await repoA.AddAsync(appId, "StaleTracker");

        await repoB.RevokeAsync(created.Id);
        var realRevokedAt = (await repoB.GetByIdAsync(created.Id))!.RevokedAt;

        await Task.Delay(50);
        await repoA.RevokeAsync(created.Id);

        using var verifyContext = CreateUntrackedContext();
        var verified = await new ApiKeyRepository(verifyContext).GetByIdAsync(created.Id);
        Assert.Equal(realRevokedAt, verified!.RevokedAt);
    }
}
