using LogsPlatform.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LogsPlatform.Web.Services.Retention;

public class RetentionCleanupService : BackgroundService
{
    private static readonly TimeSpan TickPeriod = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetentionCleanupService> _logger;

    private int _isRunning;

    public RetentionCleanupService(IServiceScopeFactory scopeFactory, ILogger<RetentionCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickPeriod);
        do
        {
            await TryRunOneCleanupAsync();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Attempts one cleanup pass in a fresh DI scope; returns false without running if one is already in progress.</summary>
    public async Task<bool> TryRunOneCleanupAsync()
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            _logger.LogWarning("Retention cleanup skipped: a previous cleanup is still running.");
            return false;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var applications = scope.ServiceProvider.GetRequiredService<IApplicationRepository>();
            var events = scope.ServiceProvider.GetRequiredService<IEventRepository>();

            var allApplications = await applications.GetAllAsync();
            foreach (var application in allApplications)
            {
                if (application.RetentionDays is null)
                {
                    continue;
                }

                var cutoff = DateTime.UtcNow.AddDays(-application.RetentionDays.Value);
                var deletedCount = await events.DeleteOlderThanAsync(application.Id, cutoff);

                if (deletedCount > 0)
                {
                    _logger.LogInformation(
                        "Retention cleanup deleted {Count} Event(s) for Application {ApplicationId} older than {Cutoff:u}.",
                        deletedCount, application.Id, cutoff);
                }
            }

            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }
}
