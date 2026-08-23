using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Tests.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LogsPlatform.Tests.Scenario;

/// <summary>
/// Same real-DB setup as <see cref="TestWebApplicationFactory"/>, but additionally removes
/// AnalysisEngineBackgroundService so detection only ever runs when a test explicitly calls
/// AnalysisEngineTickRunner.RunOneTickAsync() — the scenario tests need full manual control
/// over exactly when the Analysis Engine runs, not the real 5-minute timer racing their own
/// multi-thousand-event generation phase.
/// </summary>
public class ScenarioTestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // The ingestion rate limit (1000 events/minute per API key) is a real anti-abuse guardrail
        // against a runaway client, not a business rule this milestone's tests exercise — but the
        // scenario tests legitimately ingest tens of thousands of events per run, far faster than any
        // real client would (in-process TestServer, no network latency), so the default limit would
        // reject most of a quiet-day history within seconds. Raised here, for scenario tests only.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ingestion:RateLimitPerMinute"] = "1000000"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<LogsPlatformDbContext>>();
            services.AddDbContext<LogsPlatformDbContext>(options => options.UseSqlServer(TestDatabase.ConnectionString));
            services.RemoveAll<IHostedService>();

            using var scope = services.BuildServiceProvider().CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
            context.Database.EnsureDeleted();
            context.Database.Migrate();
        });
    }
}
