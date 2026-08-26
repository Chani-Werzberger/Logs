using System.Net.Http.Json;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LogsPlatform.Tests.Infrastructure;

public static class AuthenticatedTestClientHelper
{
    private const string TestAdminUsername = "test-admin";
    private const string TestAdminPassword = "Test-Password-123!";

    public static async Task<HttpClient> CreateAuthenticatedClientAsync<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory) where TEntryPoint : class
    {
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
            var exists = await context.PlatformUsers.AnyAsync(u => u.Username == TestAdminUsername);
            if (!exists)
            {
                context.PlatformUsers.Add(new PlatformUser
                {
                    Username = TestAdminUsername,
                    PasswordHash = PasswordHasher.Hash(TestAdminPassword),
                    IsAdmin = true,
                    CreatedAt = DateTime.UtcNow
                });
                await context.SaveChangesAsync();
            }
        }

        var client = factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(TestAdminUsername, TestAdminPassword));
        if (loginResponse.StatusCode != System.Net.HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException(
                $"AuthenticatedTestClientHelper: test login failed with {loginResponse.StatusCode}. " +
                "This usually means the PlatformUser schema or login contract changed without updating this helper.");
        }

        return client;
    }

    private const string TestNonAdminPassword = "Test-Password-123!";

    public static async Task<(HttpClient Client, int PlatformUserId)> CreateNonAdminAuthenticatedClientAsync<TEntryPoint>(
        WebApplicationFactory<TEntryPoint> factory, string username) where TEntryPoint : class
    {
        int platformUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
            var user = new PlatformUser
            {
                Username = username,
                PasswordHash = PasswordHasher.Hash(TestNonAdminPassword),
                IsAdmin = false,
                CreatedAt = DateTime.UtcNow
            };
            context.PlatformUsers.Add(user);
            await context.SaveChangesAsync();
            platformUserId = user.Id;
        }

        var client = factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(username, TestNonAdminPassword));
        if (loginResponse.StatusCode != System.Net.HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException(
                $"AuthenticatedTestClientHelper: non-admin test login failed with {loginResponse.StatusCode} for user '{username}'.");
        }

        return (client, platformUserId);
    }
}
