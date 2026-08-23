using LogsPlatform.Web.Services.Analysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

[Collection("Database")]
public class ScenarioTestWebApplicationFactoryTests
{
    [Fact]
    public void HostedServices_DoesNotIncludeAnalysisEngineBackgroundService()
    {
        using var factory = new ScenarioTestWebApplicationFactory();
        factory.CreateClient(); // triggers host startup

        using var scope = factory.Services.CreateScope();
        var hostedServices = scope.ServiceProvider.GetServices<IHostedService>();

        // The web host itself always registers a GenericWebHostService (ASP.NET Core's own internal
        // hosted service, added after ConfigureServices runs) — the collection can't be empty. What
        // matters is that AnalysisEngineBackgroundService specifically is absent.
        Assert.DoesNotContain(hostedServices, service => service is AnalysisEngineBackgroundService);
    }
}
