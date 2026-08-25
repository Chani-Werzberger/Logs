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

    [Fact]
    public async Task AddAsync_PersistsApiKey_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ApiKeyAddTestApp");
        var repository = new ApiKeyRepository(TestDatabase.CreateFactory());

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
        var repository = new ApiKeyRepository(TestDatabase.CreateFactory());

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
        var repository = new ApiKeyRepository(TestDatabase.CreateFactory());

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
        var repository = new ApiKeyRepository(TestDatabase.CreateFactory());

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
        var repository = new ApiKeyRepository(TestDatabase.CreateFactory());
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
        var repository = new ApiKeyRepository(TestDatabase.CreateFactory());
        var (created, _) = await repository.AddAsync(appId, "DoubleRevoke");

        await repository.RevokeAsync(created.Id);
        var firstRevokedAt = (await repository.GetByIdAsync(created.Id))!.RevokedAt;

        await Task.Delay(50);
        await repository.RevokeAsync(created.Id);
        var secondRevokedAt = (await repository.GetByIdAsync(created.Id))!.RevokedAt;

        Assert.Equal(firstRevokedAt, secondRevokedAt);
    }

    [Fact]
    public async Task GetByKeyHashAsync_ExistingHash_ReturnsMatchingKey()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ApiKeyHashLookupTestApp");
        var repository = new ApiKeyRepository(TestDatabase.CreateFactory());
        var (created, rawKey) = await repository.AddAsync(appId, "Hash lookup test key");

        var found = await repository.GetByKeyHashAsync(created.KeyHash);

        Assert.NotNull(found);
        Assert.Equal(created.Id, found!.Id);
        Assert.NotEqual(rawKey, found.KeyHash);
    }

    [Fact]
    public async Task GetByKeyHashAsync_UnknownHash_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new ApiKeyRepository(TestDatabase.CreateFactory());

        var found = await repository.GetByKeyHashAsync("0000000000000000000000000000000000000000000000000000000000000000");

        Assert.Null(found);
    }
}
