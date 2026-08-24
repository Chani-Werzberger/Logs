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
public class AuthControllerTests
{
    private static async Task SeedUserAsync(TestWebApplicationFactory factory, string username, string password, bool isAdmin, bool isActive = true)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        context.PlatformUsers.Add(new PlatformUser
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            IsAdmin = isAdmin,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Login_UnknownUsername_Returns401()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("no-such-user", "irrelevant"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "AuthWrongPasswordTest", "correct-password", isAdmin: false);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("AuthWrongPasswordTest", "wrong-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_InactiveUser_Returns401()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "AuthInactiveUserTest", "correct-password", isAdmin: false, isActive: false);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("AuthInactiveUserTest", "correct-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
