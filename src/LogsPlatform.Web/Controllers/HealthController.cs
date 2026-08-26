using System.Diagnostics;
using LogsPlatform.Infrastructure;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/health")]
public class HealthController : ControllerBase
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(15);

    private readonly LogsPlatformDbContext _context;
    private readonly AnalysisEngineHealthStatus _analysisEngineHealth;

    public HealthController(LogsPlatformDbContext context, AnalysisEngineHealthStatus analysisEngineHealth)
    {
        _context = context;
        _analysisEngineHealth = analysisEngineHealth;
    }

    [HttpGet]
    public async Task<ActionResult<HealthResponse>> Get()
    {
        var stopwatch = Stopwatch.StartNew();
        bool databaseHealthy;
        try
        {
            databaseHealthy = await _context.Database.CanConnectAsync();
        }
        catch
        {
            databaseHealthy = false;
        }
        stopwatch.Stop();

        var lastTick = _analysisEngineHealth.LastTickCompletedAt;
        string analysisEngineStatus;
        double? secondsSinceLastTick = null;
        if (lastTick is null)
        {
            analysisEngineStatus = "Unknown";
        }
        else
        {
            secondsSinceLastTick = (DateTime.UtcNow - lastTick.Value).TotalSeconds;
            analysisEngineStatus = secondsSinceLastTick.Value <= StaleThreshold.TotalSeconds ? "Healthy" : "Stale";
        }

        var databaseHealth = new DatabaseHealth(databaseHealthy ? "Healthy" : "Unhealthy", stopwatch.Elapsed.TotalMilliseconds);
        var analysisEngineHealth = new AnalysisEngineHealth(analysisEngineStatus, lastTick, secondsSinceLastTick);
        var overallHealthy = databaseHealthy && analysisEngineStatus != "Stale";

        var response = new HealthResponse(overallHealthy ? "Healthy" : "Unhealthy", databaseHealth, analysisEngineHealth);
        return overallHealthy ? Ok(response) : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }
}
