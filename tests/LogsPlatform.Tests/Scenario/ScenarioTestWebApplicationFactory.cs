using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Tests.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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
