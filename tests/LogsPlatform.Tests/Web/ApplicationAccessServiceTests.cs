using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ApplicationAccessServiceTests
{
    private static async Task<(PlatformUser User, Application App)> SeedAsync(LogsPlatformDbContext context, string username, bool isAdmin)
    {
        var user = new PlatformUser { Username = username, PasswordHash = "hash", IsAdmin = isAdmin, CreatedAt = DateTime.UtcNow };
        var app = new Application { Name = $"{username}-App", CreatedAt = DateTime.UtcNow };
        context.PlatformUsers.Add(user);
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        return (user, app);
    }

    [Fact]
    public async Task CanManageApplicationAsync_SuperAdmin_AlwaysTrue()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "AccessServiceSuperAdminTest", isAdmin: true);
        var service = new ApplicationAccessService(new ApplicationAccessGrantRepository(TestDatabase.CreateFactory()));

        var result = await service.CanManageApplicationAsync(isSuperAdmin: true, user.Id, app.Id);

        Assert.True(result);
    }

    [Fact]
    public async Task CanManageApplicationAsync_NonAdminWithGrant_ReturnsTrue()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "AccessServiceGrantedTest", isAdmin: false);
        var grantRepository = new ApplicationAccessGrantRepository(TestDatabase.CreateFactory());
        await grantRepository.GrantAsync(user.Id, app.Id);
        var service = new ApplicationAccessService(grantRepository);

        var result = await service.CanManageApplicationAsync(isSuperAdmin: false, user.Id, app.Id);

        Assert.True(result);
    }

    [Fact]
    public async Task CanManageApplicationAsync_NonAdminWithoutGrant_ReturnsFalse()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "AccessServiceNoGrantTest", isAdmin: false);
        var service = new ApplicationAccessService(new ApplicationAccessGrantRepository(TestDatabase.CreateFactory()));

        var result = await service.CanManageApplicationAsync(isSuperAdmin: false, user.Id, app.Id);

        Assert.False(result);
    }
}
