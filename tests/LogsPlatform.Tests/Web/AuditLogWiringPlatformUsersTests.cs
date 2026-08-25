using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AuditLogWiringPlatformUsersTests
{
    [Fact]
    public async Task CreatePlatformUser_ThenDeactivate_RecordsBothAuditEntries()
    {
        using var context = TestDatabase.CreateContext();
        var admin = new PlatformUser { Username = "AuditPlatformUserWiringAdmin", PasswordHash = "hash", IsAdmin = true, CreatedAt = DateTime.UtcNow };
        context.PlatformUsers.Add(admin);
        await context.SaveChangesAsync();

        // PlatformUserRepository takes a plain LogsPlatformDbContext (it was not one of the
        // repositories converted to IDbContextFactory during the post-M6 concurrency fix) —
        // reuses the same context instance used to seed the admin above.
        var users = new PlatformUserRepository(context);
        var auditRepository = new AuditLogRepository(TestDatabase.CreateFactory());
        var audit = new AuditLogger(auditRepository);

        // Mirrors PlatformUsersSection.razor's CreateUserAsync body exactly (see Step 2).
        var newUser = await users.AddAsync(new PlatformUser
        {
            Username = "AuditPlatformUserWiringNewUser",
            PasswordHash = PasswordHasher.Hash("password123"),
            IsAdmin = false,
            CreatedAt = DateTime.UtcNow
        });
        await audit.RecordAsync(admin.Id, "PlatformUser", newUser.Id.ToString(), "Create", $"Created platform user '{newUser.Username}' (admin: {newUser.IsAdmin})");

        // Mirrors PlatformUsersSection.razor's DeactivateAsync body exactly (see Step 2).
        await users.DeactivateAsync(newUser.Id);
        await audit.RecordAsync(admin.Id, "PlatformUser", newUser.Id.ToString(), "Deactivate", $"Deactivated platform user {newUser.Id}");

        // Verify via the repository's own QueryAsync, not a second TestDatabase.CreateContext()
        // call — CreateContext() does EnsureDeleted()+Migrate() every time, which would wipe out
        // everything just written above.
        var (createEntries, _) = await auditRepository.QueryAsync(new AuditLogQueryParameters(null, "PlatformUser", "Create", null, null, 1, 50));
        var (deactivateEntries, _) = await auditRepository.QueryAsync(new AuditLogQueryParameters(null, "PlatformUser", "Deactivate", null, null, 1, 50));
        var createEntry = Assert.Single(createEntries, e => e.EntityId == newUser.Id.ToString());
        var deactivateEntry = Assert.Single(deactivateEntries, e => e.EntityId == newUser.Id.ToString());
        Assert.Equal(admin.Id, createEntry.PlatformUserId);
        Assert.Equal(admin.Id, deactivateEntry.PlatformUserId);
    }
}
