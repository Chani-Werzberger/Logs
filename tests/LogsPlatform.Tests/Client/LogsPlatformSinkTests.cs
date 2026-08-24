using System.Net.Http.Json;
using LogsPlatform.Client.Serilog;
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Tests.Web;
using LogsPlatform.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Context;

namespace LogsPlatform.Tests.Client;

[Collection("Database")]
public class LogsPlatformSinkTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public LogsPlatformSinkTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(int ApplicationId, string ApiKey)> CreateAppWithApiKeyAsync(string appName)
    {
        var setupClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await setupClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        await setupClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));

        var keyResponse = await setupClient.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("Sink test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        return (app.Id, key!.ApiKey);
    }

    private static async Task<T> QueryAsync<T>(Func<LogsPlatformDbContext, Task<T>> query)
    {
        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString)
            .Options;
        await using var context = new LogsPlatformDbContext(options);
        return await query(context);
    }

    [Fact]
    public async Task LogsPlatform_InformationLevelLog_StoresSeverityAsInfo()
    {
        var (appId, apiKey) = await CreateAppWithApiKeyAsync("SinkSeverityTestApp");
        using var logger = new LoggerConfiguration()
            .WriteTo.LogsPlatform(apiKey, "http://localhost/", "Production", httpClient: _factory.CreateClient())
            .CreateLogger();

        logger.Information("sink severity test message");
        logger.Dispose();

        await TestPolling.WaitForCountAsync(
            () => QueryAsync(context => context.Events.CountAsync(e => e.ApplicationId == appId)),
            expected: 1, timeout: TimeSpan.FromSeconds(3));

        var stored = await QueryAsync(context => context.Events.SingleOrDefaultAsync(e => e.ApplicationId == appId));
        Assert.Equal(9, stored!.Severity);
    }

    [Fact]
    public async Task LogsPlatform_LogContextCorrelationId_IsSentThrough()
    {
        var (appId, apiKey) = await CreateAppWithApiKeyAsync("SinkPropertyTestApp");
        using var logger = new LoggerConfiguration()
            .WriteTo.LogsPlatform(apiKey, "http://localhost/", "Production", httpClient: _factory.CreateClient())
            .CreateLogger();

        using (LogContext.PushProperty("CorrelationId", "corr-abc-123"))
        {
            logger.Error("correlated failure");
        }
        logger.Dispose();

        await TestPolling.WaitForCountAsync(
            () => QueryAsync(context => context.Events.CountAsync(e => e.ApplicationId == appId)),
            expected: 1, timeout: TimeSpan.FromSeconds(3));

        var stored = await QueryAsync(context => context.Events.SingleOrDefaultAsync(e => e.ApplicationId == appId));
        Assert.Equal("corr-abc-123", stored!.CorrelationId);
    }

    [Fact]
    public async Task LogsPlatform_LoggedException_StoresRawStackTraceNotToString()
    {
        var (appId, apiKey) = await CreateAppWithApiKeyAsync("SinkExceptionTestApp");
        using var logger = new LoggerConfiguration()
            .WriteTo.LogsPlatform(apiKey, "http://localhost/", "Production", httpClient: _factory.CreateClient())
            .CreateLogger();

        Exception caught;
        try
        {
            throw new TimeoutException("operation timed out");
        }
        catch (Exception ex)
        {
            caught = ex;
        }
        logger.Error(caught, "operation failed");
        logger.Dispose();

        await TestPolling.WaitForCountAsync(
            () => QueryAsync(context => context.Events.CountAsync(e => e.ApplicationId == appId)),
            expected: 1, timeout: TimeSpan.FromSeconds(3));

        var stored = await QueryAsync(context => context.Events.SingleOrDefaultAsync(e => e.ApplicationId == appId));
        Assert.Equal(caught.StackTrace, stored!.StackTrace);
        Assert.DoesNotContain("operation timed out", stored.StackTrace);
    }
}
