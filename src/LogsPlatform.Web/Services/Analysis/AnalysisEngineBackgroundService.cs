using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LogsPlatform.Web.Services.Analysis;

public class AnalysisEngineBackgroundService : BackgroundService
{
    private static readonly TimeSpan TickPeriod = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AnalysisEngineBackgroundService> _logger;

    private int _isRunning;

    public AnalysisEngineBackgroundService(IServiceScopeFactory scopeFactory, ILogger<AnalysisEngineBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickPeriod);
        do
        {
            await TryRunOneTickAsync();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Attempts one tick in a fresh DI scope; returns false without running if a tick is already in progress.</summary>
    public async Task<bool> TryRunOneTickAsync()
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            _logger.LogWarning("Analysis Engine tick skipped: a previous tick is still running.");
            return false;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<AnalysisEngineTickRunner>();
            await runner.RunOneTickAsync();
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }
}
