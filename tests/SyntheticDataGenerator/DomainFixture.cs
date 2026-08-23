using System.Net.Http.Json;

namespace LogsPlatform.SyntheticDataGenerator;

public record AppFixture(int ApplicationId, int EnvironmentId, string ApiKey);

public static class DomainFixture
{
    public static async Task<AppFixture> BuildRetailPulseAsync(HttpClient client)
    {
        var appId = await CreateApplicationAsync(client, ScenarioConstants.RetailPulseApp);
        var envId = await CreateEnvironmentAsync(client, appId, "Production");
        var apiKey = await CreateApiKeyAsync(client, appId);

        var ordersModuleId = await CreateModuleAsync(client, appId, ScenarioConstants.OrdersModule);
        var orderApiServiceId = await CreateScreenServiceAsync(client, ordersModuleId, ScenarioConstants.OrderApiServiceScreenService);
        var createOrderProcessId = await CreateProcessAsync(client, orderApiServiceId, ScenarioConstants.CreateOrderProcess);
        await CreateOperationAsync(client, createOrderProcessId, ScenarioConstants.ValidateCartOperation);
        await CreateOperationAsync(client, createOrderProcessId, ScenarioConstants.ReserveStockOperation);
        await CreateOperationAsync(client, createOrderProcessId, ScenarioConstants.ChargePaymentOperation);
        await CreateOperationAsync(client, createOrderProcessId, ScenarioConstants.ConfirmOrderOperation);

        var inventoryModuleId = await CreateModuleAsync(client, appId, ScenarioConstants.InventoryModule);
        var stockServiceId = await CreateScreenServiceAsync(client, inventoryModuleId, ScenarioConstants.StockServiceScreenService);
        var stockSyncProcessId = await CreateProcessAsync(client, stockServiceId, ScenarioConstants.StockSyncProcess);
        await CreateOperationAsync(client, stockSyncProcessId, ScenarioConstants.PullSupplierFeedOperation);

        return new AppFixture(appId, envId, apiKey);
    }

    public static async Task<AppFixture> BuildFieldOpsAsync(HttpClient client)
    {
        var appId = await CreateApplicationAsync(client, ScenarioConstants.FieldOpsApp);
        var envId = await CreateEnvironmentAsync(client, appId, "Production");
        var apiKey = await CreateApiKeyAsync(client, appId);

        var schedulingModuleId = await CreateModuleAsync(client, appId, ScenarioConstants.SchedulingModule);
        var schedulerApiId = await CreateScreenServiceAsync(client, schedulingModuleId, ScenarioConstants.SchedulerApiScreenService);
        var assignTechnicianProcessId = await CreateProcessAsync(client, schedulerApiId, ScenarioConstants.AssignTechnicianProcess);
        await CreateOperationAsync(client, assignTechnicianProcessId, ScenarioConstants.MatchAvailabilityOperation);

        var reportingModuleId = await CreateModuleAsync(client, appId, ScenarioConstants.ReportingModule);
        var dailyReportId = await CreateScreenServiceAsync(client, reportingModuleId, ScenarioConstants.DailyReportScreenService);
        var generateReportProcessId = await CreateProcessAsync(client, dailyReportId, ScenarioConstants.GenerateReportProcess);
        await CreateOperationAsync(client, generateReportProcessId, ScenarioConstants.AggregateJobsOperation);

        return new AppFixture(appId, envId, apiKey);
    }

    public static async Task<IReadOnlyList<string>> SeedCustomersAsync(HttpClient client, int applicationId, int count)
    {
        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var externalId = $"cust-{i}";
            await client.PostAsJsonAsync($"/api/v1/admin/applications/{applicationId}/customers",
                new { ExternalCustomerId = externalId, Name = $"Customer {i}" });
            ids.Add(externalId);
        }
        return ids;
    }

    private static async Task<int> CreateApplicationAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/admin/applications", new { Name = name, Description = (string?)null });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    private static async Task<int> CreateEnvironmentAsync(HttpClient client, int appId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/environments", new { Name = name, IsProduction = true });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    private static async Task<string> CreateApiKeyAsync(HttpClient client, int appId)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/api-keys", new { Label = "SyntheticDataGenerator" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiKeyResponse>();
        return body!.ApiKey;
    }

    private static async Task<int> CreateModuleAsync(HttpClient client, int appId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/modules", new { Name = name, Description = (string?)null });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    private static async Task<int> CreateScreenServiceAsync(HttpClient client, int moduleId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/admin/modules/{moduleId}/screen-services", new { Name = name, Type = "Service", Description = (string?)null });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    private static async Task<int> CreateProcessAsync(HttpClient client, int screenServiceId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes", new { Name = name, Description = (string?)null });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    private static async Task<int> CreateOperationAsync(HttpClient client, int processId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/admin/processes/{processId}/operations", new { Name = name, Description = (string?)null });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    private record IdResponse(int Id);
    private record ApiKeyResponse(int Id, int ApplicationId, string Label, DateTime CreatedAt, string ApiKey);
}
