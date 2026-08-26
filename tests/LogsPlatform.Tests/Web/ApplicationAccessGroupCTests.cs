using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ApplicationAccessGroupCTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ApplicationAccessGroupCTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task VersionCreate_SuperAdmin_Succeeds()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupCSuperAdminApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var response = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/versions", new CreateVersionRequest("1.0.0", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task VersionCreate_NonAdminWithGrant_Succeeds()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupCGrantedApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var (nonAdminClient, platformUserId) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "AccessGroupCGrantedUser");
        using (var scope = _factory.Services.CreateScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<IApplicationAccessGrantRepository>();
            await grants.GrantAsync(platformUserId, app!.Id);
        }

        var response = await nonAdminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/versions", new CreateVersionRequest("1.0.0", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task VersionCreate_NonAdminWithoutGrant_ReturnsForbidden()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupCNoGrantApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var (nonAdminClient, _) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "AccessGroupCNoGrantUser");

        var response = await nonAdminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/versions", new CreateVersionRequest("1.0.0", null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
