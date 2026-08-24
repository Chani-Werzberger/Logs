using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Tests.Web;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class AuthenticatedTestClientHelperTests
{
    [Fact]
    public async Task CreateAuthenticatedClientAsync_ReturnedClientCanReachAGatedAdminEndpoint()
    {
        using var factory = new TestWebApplicationFactory();

        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(factory);
        var response = await client.GetAsync("/api/v1/admin/applications/1");

        // 404 (no Application with id 1), not 401/403 — proves the client is authenticated as an admin.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateAuthenticatedClientAsync_CalledTwiceOnSameFactory_DoesNotFailOnDuplicateSeed()
    {
        using var factory = new TestWebApplicationFactory();

        var first = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(factory);
        var second = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync("/api/v1/admin/applications/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await first.GetAsync("/api/v1/admin/applications/1")).StatusCode);
    }
}
