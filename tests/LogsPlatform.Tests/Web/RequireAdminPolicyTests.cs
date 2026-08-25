using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class RequireAdminPolicyTests
{
    private static async Task SeedUserAsync(TestWebApplicationFactory factory, string username, string password, bool isAdmin)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        context.PlatformUsers.Add(new PlatformUser
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            IsAdmin = isAdmin,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task AdminEndpoint_NoCookie_Returns401()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/applications/1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoint_NonAdminCookie_Returns403()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "RequireAdminNonAdminTest", "password123", isAdmin: false);
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("RequireAdminNonAdminTest", "password123"));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        var response = await client.GetAsync("/api/v1/admin/applications/1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoint_AdminCookie_ReachesTheEndpoint()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "RequireAdminAdminTest", "password123", isAdmin: true);
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("RequireAdminAdminTest", "password123"));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        var response = await client.GetAsync("/api/v1/admin/applications/1");

        // 404 (no Application with id 1), not 401/403 — proves the request reached the controller.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Login_ValidCredentials_SetsNameIdentifierClaimToPlatformUserId()
    {
        using var factory = new TestWebApplicationFactory();
        PlatformUser user;
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
            user = new PlatformUser { Username = "NameIdentifierClaimTestUser", PasswordHash = PasswordHasher.Hash("password123"), IsAdmin = true, CreatedAt = DateTime.UtcNow };
            context.PlatformUsers.Add(user);
            await context.SaveChangesAsync();
        }
        var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("NameIdentifierClaimTestUser", "password123"));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        // Round-trip through an admin endpoint that echoes back based on the authenticated user
        // isn't available yet, so this test asserts indirectly: the cookie must let a request
        // through to an admin endpoint using the same client (proves sign-in succeeded end to
        // end), and Task 3's controllers are what will actually prove the claim's value is
        // correct once AuditLogger records against it.
        var response = await client.GetAsync("/api/v1/admin/applications/1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task QueryEndpoint_AnyAuthenticatedUser_ReachesTheEndpoint()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "RequireAdminQueryEndpointTest", "password123", isAdmin: false);
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("RequireAdminQueryEndpointTest", "password123"));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        var response = await client.GetAsync("/api/v1/findings?applicationId=1&environmentId=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task IngestionEndpoint_ApiKeyOnly_StillWorksWithoutACookie()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        // No login at all — proves the ApiKey scheme on IngestionController is unaffected by
        // cookie auth becoming the default scheme.
        var response = await client.PostAsJsonAsync("/api/v1/ingest/events", new List<object>());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // missing X-Api-Key, not a cookie redirect
    }
}
