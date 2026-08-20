using System.Net.Http.Json;
using LogsPlatform.Client;
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Tests.Web;
using LogsPlatform.Web.Contracts;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Client;

[Collection("Database")]
public class LogsPlatformClientTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public LogsPlatformClientTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(int ApplicationId, string ApiKey)> CreateAppWithApiKeyAsync(string appName)
    {
        var setupClient = _factory.CreateClient();
        var appResponse = await setupClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        await setupClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));

        var keyResponse = await setupClient.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("Client test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        return (app.Id, key!.ApiKey);
    }

    private static async Task<int> CountEventsAsync(int applicationId)
    {
        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString)
            .Options;
        await using var context = new LogsPlatformDbContext(options);
        return await context.Events.CountAsync(e => e.ApplicationId == applicationId);
    }

    private static EventPayload BuildEvent(string eventKey) => new(
        EventKey: eventKey, Timestamp: DateTime.UtcNow, Severity: "Error", Environment: "Production",
        Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null,
        DurationMs: null, CustomerId: null, UserId: null, Message: "client test event", MessageTemplate: null,
        Exception: null, Metadata: null);

    [Fact]
    public async Task SendEventAsync_ReachesBatchSize_FlushesAndPersistsEvents()
    {
        var (appId, apiKey) = await CreateAppWithApiKeyAsync("ClientBatchSizeTestApp");
        await using var client = new LogsPlatformClient(
            baseUrl: "http://localhost/", apiKey: apiKey, httpClient: _factory.CreateClient(),
            batchSize: 2, period: TimeSpan.FromMinutes(10));

        await client.SendEventAsync(BuildEvent("batch-1"));
        await client.SendEventAsync(BuildEvent("batch-2"));

        var count = await TestPolling.WaitForCountAsync(() => CountEventsAsync(appId), expected: 2, timeout: TimeSpan.FromSeconds(3));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task SendEventAsync_TimerElapses_FlushesPartialBatchAndPersists()
    {
        var (appId, apiKey) = await CreateAppWithApiKeyAsync("ClientTimerTestApp");
        await using var client = new LogsPlatformClient(
            baseUrl: "http://localhost/", apiKey: apiKey, httpClient: _factory.CreateClient(),
            batchSize: 100, period: TimeSpan.FromMilliseconds(200));

        await client.SendEventAsync(BuildEvent("timer-1"));

        var count = await TestPolling.WaitForCountAsync(() => CountEventsAsync(appId), expected: 1, timeout: TimeSpan.FromSeconds(2));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SendEventAsync_ExceedsQueueLimit_DropsOldestBeforeFlush()
    {
        var (appId, apiKey) = await CreateAppWithApiKeyAsync("ClientOverflowTestApp");
        await using var client = new LogsPlatformClient(
            baseUrl: "http://localhost/", apiKey: apiKey, httpClient: _factory.CreateClient(),
            batchSize: 1000, period: TimeSpan.FromMinutes(10), queueLimit: 5);

        for (var i = 0; i < 8; i++)
        {
            await client.SendEventAsync(BuildEvent($"k{i}"));
        }
        await client.FlushAsync();

        var count = await TestPolling.WaitForCountAsync(() => CountEventsAsync(appId), expected: 5, timeout: TimeSpan.FromSeconds(3));
        Assert.Equal(5, count);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var context = new LogsPlatformDbContext(options);
        var keys = await context.Events.Where(e => e.ApplicationId == appId).Select(e => e.EventKey).ToListAsync();
        Assert.Equal(new[] { "k3", "k4", "k5", "k6", "k7" }, keys.OrderBy(k => k));
    }

    [Fact]
    public async Task FlushAsync_UnreachableServer_DoesNotThrow()
    {
        await using var client = new LogsPlatformClient(
            baseUrl: "http://127.0.0.1:1/", apiKey: "irrelevant",
            batchSize: 100, period: TimeSpan.FromMinutes(10));

        await client.SendEventAsync(BuildEvent("unreachable-1"));

        var exception = await Record.ExceptionAsync(() => client.FlushAsync());
        Assert.Null(exception);
    }

    [Fact]
    public async Task DisposeAsync_WithPendingEvents_FlushesBeforeDisposing()
    {
        var (appId, apiKey) = await CreateAppWithApiKeyAsync("ClientDisposeTestApp");
        var client = new LogsPlatformClient(
            baseUrl: "http://localhost/", apiKey: apiKey, httpClient: _factory.CreateClient(),
            batchSize: 100, period: TimeSpan.FromMinutes(10));

        await client.SendEventAsync(BuildEvent("dispose-1"));
        await client.DisposeAsync();

        var count = await CountEventsAsync(appId);
        Assert.Equal(1, count);
    }
}
