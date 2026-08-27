using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ApplicationsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ApplicationsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsApplication()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/applications",
            new CreateApplicationRequest("RetailPulse", "E-commerce simulation app"));

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.NotNull(created);
        Assert.Equal("RetailPulse", created!.Name);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409Conflict()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var request = new CreateApplicationRequest("DuplicateNameTest", null);

        var first = await client.PostAsJsonAsync("/api/v1/admin/applications", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/admin/applications", request);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task PostThenGet_CreatedAtRoundTripsAsUtc()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/applications",
            new CreateApplicationRequest("UtcRoundTripTest", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{created!.Id}");
        var fetched = await getResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        Assert.Equal(DateTimeKind.Utc, fetched!.CreatedAt.Kind);
    }

    [Fact]
    public async Task UpdateRetention_ValidRequest_UpdatesAndReturnsIt()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/applications",
            new CreateApplicationRequest("RetentionUpdateEndpointTestApp", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var updateResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{created!.Id}",
            new UpdateApplicationRetentionRequest(60));

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.Equal(60, updated!.RetentionDays);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{created.Id}");
        var fetched = await getResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.Equal(60, fetched!.RetentionDays);
    }

    [Fact]
    public async Task UpdateRetention_NoSuchApplication_Returns404()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/admin/applications/999999",
            new UpdateApplicationRetentionRequest(30));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRetention_NonAdminUser_Returns403()
    {
        var (client, _) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "RetentionUpdateNonAdminTestUser");
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var createResponse = await adminClient.PostAsJsonAsync(
            "/api/v1/admin/applications",
            new CreateApplicationRequest("RetentionUpdateNonAdminTestApp", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var response = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{created!.Id}",
            new UpdateApplicationRetentionRequest(30));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
