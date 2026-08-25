using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class AuditLogRepositoryTests
{
    private static async Task<PlatformUser> SeedUserAsync(LogsPlatformDbContext context, string username)
    {
        var user = new PlatformUser { Username = username, PasswordHash = "hash", IsAdmin = true, CreatedAt = DateTime.UtcNow };
        context.PlatformUsers.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task AddAsync_ValidEntry_PersistsAllFields()
    {
        using var context = TestDatabase.CreateContext();
        var user = await SeedUserAsync(context, "AuditRepoAddTestUser");
        var repository = new AuditLogRepository(TestDatabase.CreateFactory());

        var entry = await repository.AddAsync(new AdminAuditLogEntry
        {
            PlatformUserId = user.Id,
            Timestamp = new DateTime(2026, 8, 25, 10, 0, 0, DateTimeKind.Utc),
            EntityType = "Application",
            EntityId = "1",
            Action = "Create",
            Description = "Created application 'Test'"
        });

        var (items, _) = await repository.QueryAsync(new AuditLogQueryParameters(null, "Application", null, null, null, 1, 50));
        var saved = Assert.Single(items);
        Assert.Equal(user.Id, saved.PlatformUserId);
        Assert.Equal("Application", saved.EntityType);
        Assert.Equal("1", saved.EntityId);
        Assert.Equal("Create", saved.Action);
        Assert.Equal("Created application 'Test'", saved.Description);
    }

    [Fact]
    public async Task QueryAsync_FilterByEntityType_ReturnsOnlyMatching()
    {
        using var context = TestDatabase.CreateContext();
        var user = await SeedUserAsync(context, "AuditRepoFilterTestUser");
        var repository = new AuditLogRepository(TestDatabase.CreateFactory());
        await repository.AddAsync(new AdminAuditLogEntry { PlatformUserId = user.Id, Timestamp = DateTime.UtcNow, EntityType = "Application", EntityId = "1", Action = "Create", Description = "a" });
        await repository.AddAsync(new AdminAuditLogEntry { PlatformUserId = user.Id, Timestamp = DateTime.UtcNow, EntityType = "ApiKey", EntityId = "1", Action = "Create", Description = "b" });

        var (items, totalCount) = await repository.QueryAsync(new AuditLogQueryParameters(null, "ApiKey", null, null, null, 1, 50));

        Assert.Equal(1, totalCount);
        Assert.Single(items);
        Assert.Equal("ApiKey", items[0].EntityType);
    }

    [Fact]
    public async Task QueryAsync_OrdersByTimestampDescending()
    {
        using var context = TestDatabase.CreateContext();
        var user = await SeedUserAsync(context, "AuditRepoOrderTestUser");
        var repository = new AuditLogRepository(TestDatabase.CreateFactory());
        var older = await repository.AddAsync(new AdminAuditLogEntry { PlatformUserId = user.Id, Timestamp = new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc), EntityType = "Application", EntityId = "1", Action = "Create", Description = "older" });
        var newer = await repository.AddAsync(new AdminAuditLogEntry { PlatformUserId = user.Id, Timestamp = new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc), EntityType = "Application", EntityId = "2", Action = "Create", Description = "newer" });

        var (items, _) = await repository.QueryAsync(new AuditLogQueryParameters(null, null, null, null, null, 1, 50));

        Assert.Equal(newer.Id, items[0].Id);
        Assert.Equal(older.Id, items[1].Id);
    }

    [Fact]
    public async Task QueryAsync_Paging_ReturnsCorrectPage()
    {
        using var context = TestDatabase.CreateContext();
        var user = await SeedUserAsync(context, "AuditRepoPagingTestUser");
        var repository = new AuditLogRepository(TestDatabase.CreateFactory());
        for (var i = 0; i < 3; i++)
        {
            await repository.AddAsync(new AdminAuditLogEntry { PlatformUserId = user.Id, Timestamp = DateTime.UtcNow.AddMinutes(i), EntityType = "Application", EntityId = i.ToString(), Action = "Create", Description = $"entry {i}" });
        }

        var (items, totalCount) = await repository.QueryAsync(new AuditLogQueryParameters(null, null, null, null, null, 1, 2));

        Assert.Equal(3, totalCount);
        Assert.Equal(2, items.Count);
    }
}
