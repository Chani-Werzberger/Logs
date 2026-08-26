using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ApplicationAccessGroupBTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ApplicationAccessGroupBTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CustomerCreate_SuperAdmin_Succeeds()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupBSuperAdminApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var response = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/customers", new CreateCustomerRequest("ext-1", "Customer One"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CustomerCreate_NonAdminWithGrant_Succeeds()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupBGrantedApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var (nonAdminClient, platformUserId) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "AccessGroupBGrantedUser");
        using (var scope = _factory.Services.CreateScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<IApplicationAccessGrantRepository>();
            await grants.GrantAsync(platformUserId, app!.Id);
        }

        var response = await nonAdminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/customers", new CreateCustomerRequest("ext-1", "Customer One"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CustomerCreate_NonAdminWithoutGrant_ReturnsForbidden()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupBNoGrantApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var (nonAdminClient, _) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "AccessGroupBNoGrantUser");

        var response = await nonAdminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/customers", new CreateCustomerRequest("ext-1", "Customer One"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
