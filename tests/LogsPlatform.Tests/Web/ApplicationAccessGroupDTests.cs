using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ApplicationAccessGroupDTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ApplicationAccessGroupDTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ScreenServiceCreate_SuperAdmin_Succeeds()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupDSuperAdminApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var moduleResponse = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/modules", new CreateModuleRequest("Module1", null));
        var module = await moduleResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var response = await adminClient.PostAsJsonAsync($"/api/v1/admin/modules/{module!.Id}/screen-services", new CreateScreenServiceRequest("Screen1", "Screen", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ScreenServiceCreate_NonAdminWithGrant_Succeeds()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupDGrantedApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var moduleResponse = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/modules", new CreateModuleRequest("Module1", null));
        var module = await moduleResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var (nonAdminClient, platformUserId) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "AccessGroupDGrantedUser");
        using (var scope = _factory.Services.CreateScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<IApplicationAccessGrantRepository>();
            await grants.GrantAsync(platformUserId, app!.Id);
        }

        var response = await nonAdminClient.PostAsJsonAsync($"/api/v1/admin/modules/{module!.Id}/screen-services", new CreateScreenServiceRequest("Screen1", "Screen", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ScreenServiceCreate_NonAdminWithoutGrant_ReturnsForbidden()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupDNoGrantApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var moduleResponse = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/modules", new CreateModuleRequest("Module1", null));
        var module = await moduleResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var (nonAdminClient, _) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "AccessGroupDNoGrantUser");

        var response = await nonAdminClient.PostAsJsonAsync($"/api/v1/admin/modules/{module!.Id}/screen-services", new CreateScreenServiceRequest("Screen1", "Screen", null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
