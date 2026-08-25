using System.Net.Http.Json;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class OtlpRealClientTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public OtlpRealClientTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RealOtelSdkClient_SendsLogViaILogger_EventIsPersisted()
    {
        // The OTLP HTTP exporter's export client calls the synchronous HttpClient.Send(), which
        // ASP.NET Core's in-memory TestServer handler explicitly refuses (NotSupportedException)
        // to prevent threadpool exhaustion. UseKestrel() switches this factory to a real socket
        // listener on a dynamic port instead, which every other test in this suite doesn't need.
        _factory.UseKestrel();

        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("OtlpRealClientTestApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var envResponse = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));
        var env = await envResponse.Content.ReadFromJsonAsync<EnvironmentResponse>();
        var keyResponse = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("Real OTel client test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var serverAddress = _factory.ClientOptions.BaseAddress;

        using (var loggerFactory = LoggerFactory.Create(logging =>
        {
            logging.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = true;
                options.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddAttributes(new[]
                {
                    new KeyValuePair<string, object>("deployment.environment", "Production")
                }));
                options.AddOtlpExporter(otlp =>
                {
                    otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                    otlp.Endpoint = new Uri(serverAddress, "v1/logs");
                    otlp.Headers = $"X-Api-Key={key!.ApiKey}";
                    otlp.ExportProcessorType = OpenTelemetry.ExportProcessorType.Simple;
                });
            });
        }))
        {
            var logger = loggerFactory.CreateLogger("OtlpRealClientTests");
            logger.LogError("real otel sdk client test message");
        }

        var eventsResponse = await adminClient.GetAsync($"/api/v1/events?applicationId={app.Id}&environmentId={env!.Id}&page=1&pageSize=10");
        var events = await eventsResponse.Content.ReadFromJsonAsync<EventListResponse>();

        Assert.Equal(1, events!.TotalCount);
        Assert.Equal("real otel sdk client test message", events.Items[0].Message);
        Assert.Equal("Error", events.Items[0].Severity);
    }
}
