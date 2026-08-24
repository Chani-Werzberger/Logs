using LogsPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class StartupSeedingTests
{
    [Fact]
    public async Task FreshDatabase_SeedsExactlyOneAdminUser()
    {
        using var factory = new TestWebApplicationFactory();
        // Force host startup (and therefore the seeding block) by issuing one request.
        var client = factory.CreateClient();
        await client.GetAsync("/login");

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var users = await context.PlatformUsers.ToListAsync();

        Assert.Single(users);
        Assert.Equal("admin", users[0].Username);
        Assert.True(users[0].IsAdmin);
        Assert.True(users[0].IsActive);
    }
}
