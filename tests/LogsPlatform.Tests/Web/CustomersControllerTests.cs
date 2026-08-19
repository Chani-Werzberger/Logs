using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class CustomersControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public CustomersControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<int> CreateApplicationAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(name, null));
        var created = await response.Content.ReadFromJsonAsync<ApplicationResponse>();
        return created!.Id;
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsCustomer()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "CustomerControllerTestApp1");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/customers",
            new CreateCustomerRequest("cust-1", "Acme Corp"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.NotNull(created);
        Assert.Equal("cust-1", created!.ExternalCustomerId);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/customers/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateExternalCustomerId_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "CustomerControllerTestApp2");
        var request = new CreateCustomerRequest("cust-dup", "First");

        var first = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/customers", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/customers",
            new CreateCustomerRequest("cust-dup", "Second"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_CustomerBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "CustomerIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "CustomerIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/customers",
            new CreateCustomerRequest("cust-1", "BelongsToApp1"));
        var created = await createResponse.Content.ReadFromJsonAsync<CustomerResponse>();

        var crossAppGet = await client.GetAsync($"/api/v1/admin/applications/{appId2}/customers/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossAppGet.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesName_LeavesExternalCustomerIdUnchanged()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "CustomerRenameControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/customers",
            new CreateCustomerRequest("cust-1", "OriginalName"));
        var created = await createResponse.Content.ReadFromJsonAsync<CustomerResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/customers/{created!.Id}",
            new RenameCustomerRequest("RenamedCustomer"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.Equal("RenamedCustomer", renamed!.Name);
        Assert.Equal("cust-1", renamed.ExternalCustomerId);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/customers/{created.Id}");
        var reloaded = await getResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.Equal("RenamedCustomer", reloaded!.Name);
    }

    [Fact]
    public async Task Create_UnknownApplicationId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications/999999/customers",
            new CreateCustomerRequest("cust-1", "Acme Corp"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "CustomerDeactivateControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/customers",
            new CreateCustomerRequest("cust-1", "ToDeactivate"));
        var created = await createResponse.Content.ReadFromJsonAsync<CustomerResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/customers/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<CustomerResponse>>($"/api/v1/admin/applications/{appId}/customers");
        Assert.DoesNotContain(listResponse!, c => c.Id == created.Id);
    }
}
