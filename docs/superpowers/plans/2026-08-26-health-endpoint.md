# Health-Check Endpoint Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `GET /api/v1/health`, reporting SQL Server connectivity and whether the Analysis Engine's background tick loop is still alive.

**Architecture:** A new singleton `AnalysisEngineHealthStatus` records the UTC timestamp of every successful `AnalysisEngineBackgroundService` tick; a new `HealthController` reads it plus a live `CanConnectAsync()` DB check, classifies both, and returns a JSON body with an overall status and the matching HTTP code (`200` Healthy / `503` Unhealthy).

**Tech Stack:** .NET 10 / EF Core 10 (existing `CanConnectAsync`). No new packages.

## Global Constraints

- **Design doc:** `docs/superpowers/specs/2026-08-26-health-endpoint-design.md` — read it before starting.
- **No `[AllowAnonymous]`.** The endpoint stays behind the app's existing fallback policy (`RequireAuthenticatedUser()`) — a deliberate, non-default choice confirmed during brainstorming. Do not add anonymous access.
- **Staleness threshold: 15 minutes** (3× the Analysis Engine's 5-minute tick period, `AnalysisEngineBackgroundService.TickPeriod`). Exact value, not configurable in this plan.
- **`Unknown` (no tick has ever completed) counts as overall Healthy, not Unhealthy** — a fresh app start is not a failure state. Only `Stale` (a tick completed once, then went silent past the threshold) or a failed DB check make the overall status Unhealthy.
- **HTTP status codes:** `200` when overall Healthy, `503 Service Unavailable` when Unhealthy. Never `500` for a normal Unhealthy report — that's an expected, well-formed response, not a server error.
- **Test convention for the DB-down case:** this project never mocks the database (always real SQL Server) and has no way to safely take the shared test DB offline for one test. The DB-down path is therefore NOT covered by an automated test in this plan — only by code inspection (the `try`/`catch` around `CanConnectAsync()`). This is a deliberate, documented gap, not an oversight.
- **Test convention for `AnalysisEngineHealthStatus`'s "no tick yet" state via HTTP:** `TestWebApplicationFactory` does not disable the real `AnalysisEngineBackgroundService` — it starts for real and fires its first tick immediately, which races with any HTTP-level test trying to observe the pre-tick "Unknown" state. The "Unknown" behavior is therefore covered only by a pure unit test on `AnalysisEngineHealthStatus` itself (no DB, no factory). HTTP-level integration tests instead force a known state by resolving the real `AnalysisEngineHealthStatus` singleton from the running factory's DI container and calling `RecordTickCompleted` with a specific timestamp before asserting — this overrides whatever the real background service already wrote, making the test deterministic regardless of timing.
- **Frequent commits:** one commit per task.

---

## Task 1: AnalysisEngineHealthStatus + wiring into the background service

**Files:**
- Create: `src/LogsPlatform.Web/Services/Analysis/AnalysisEngineHealthStatus.cs`
- Modify: `src/LogsPlatform.Web/Services/Analysis/AnalysisEngineBackgroundService.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Create: `tests/LogsPlatform.Tests/Web/AnalysisEngineHealthStatusTests.cs`
- Modify: `tests/LogsPlatform.Tests/Web/AnalysisEngineBackgroundServiceTests.cs`

**Interfaces:**
- Produces: `AnalysisEngineHealthStatus.RecordTickCompleted(DateTime completedAtUtc) : void`, `AnalysisEngineHealthStatus.LastTickCompletedAt : DateTime?` (get-only property) — consumed by Task 2's `HealthController`.

- [ ] **Step 1: Write the failing unit test**

Create `tests/LogsPlatform.Tests/Web/AnalysisEngineHealthStatusTests.cs`:

```csharp
using LogsPlatform.Web.Services.Analysis;
using Xunit;

namespace LogsPlatform.Tests.Web;

public class AnalysisEngineHealthStatusTests
{
    [Fact]
    public void LastTickCompletedAt_BeforeAnyRecord_IsNull()
    {
        var status = new AnalysisEngineHealthStatus();

        Assert.Null(status.LastTickCompletedAt);
    }

    [Fact]
    public void RecordTickCompleted_ThenLastTickCompletedAt_ReturnsRecordedValue()
    {
        var status = new AnalysisEngineHealthStatus();
        var timestamp = new DateTime(2026, 8, 26, 14, 5, 0, DateTimeKind.Utc);

        status.RecordTickCompleted(timestamp);

        Assert.Equal(timestamp, status.LastTickCompletedAt);
    }

    [Fact]
    public void RecordTickCompleted_CalledTwice_ReturnsMostRecentValue()
    {
        var status = new AnalysisEngineHealthStatus();
        var older = new DateTime(2026, 8, 26, 14, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 8, 26, 14, 5, 0, DateTimeKind.Utc);

        status.RecordTickCompleted(older);
        status.RecordTickCompleted(newer);

        Assert.Equal(newer, status.LastTickCompletedAt);
    }
}
```

This test class has no `[Collection("Database")]` attribute and doesn't touch `TestDatabase` — `AnalysisEngineHealthStatus` is a plain in-memory class with no DB dependency, so it runs like any other fast unit test.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build`
Expected: build error — `AnalysisEngineHealthStatus` does not exist yet.

- [ ] **Step 3: Implement AnalysisEngineHealthStatus**

Create `src/LogsPlatform.Web/Services/Analysis/AnalysisEngineHealthStatus.cs`:

```csharp
namespace LogsPlatform.Web.Services.Analysis;

public class AnalysisEngineHealthStatus
{
    private readonly object _lock = new();
    private DateTime? _lastTickCompletedAt;

    public void RecordTickCompleted(DateTime completedAtUtc)
    {
        lock (_lock)
        {
            _lastTickCompletedAt = completedAtUtc;
        }
    }

    public DateTime? LastTickCompletedAt
    {
        get
        {
            lock (_lock)
            {
                return _lastTickCompletedAt;
            }
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~AnalysisEngineHealthStatusTests"`
Expected: 3/3 passing.

- [ ] **Step 5: Wire into AnalysisEngineBackgroundService**

Replace the full contents of `src/LogsPlatform.Web/Services/Analysis/AnalysisEngineBackgroundService.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LogsPlatform.Web.Services.Analysis;

public class AnalysisEngineBackgroundService : BackgroundService
{
    private static readonly TimeSpan TickPeriod = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AnalysisEngineHealthStatus _healthStatus;
    private readonly ILogger<AnalysisEngineBackgroundService> _logger;

    private int _isRunning;

    public AnalysisEngineBackgroundService(IServiceScopeFactory scopeFactory, AnalysisEngineHealthStatus healthStatus, ILogger<AnalysisEngineBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _healthStatus = healthStatus;
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
            _healthStatus.RecordTickCompleted(DateTime.UtcNow);
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }
}
```

(Only 3 lines actually changed from the current file: the new `_healthStatus` field, the new constructor parameter, and the `_healthStatus.RecordTickCompleted(DateTime.UtcNow);` line right after `await runner.RunOneTickAsync();` — a failed tick, one that throws before that line, does not record completion, which is correct: a tick that fails should count toward eventual staleness, not reset the clock.)

- [ ] **Step 6: Register in DI**

In `src/LogsPlatform.Web/Program.cs`, add this line immediately before `builder.Services.AddHostedService<LogsPlatform.Web.Services.Analysis.AnalysisEngineBackgroundService>();`:

```csharp
builder.Services.AddSingleton<LogsPlatform.Web.Services.Analysis.AnalysisEngineHealthStatus>();
```

- [ ] **Step 7: Fix the now-broken direct-construction test call site**

In `tests/LogsPlatform.Tests/Web/AnalysisEngineBackgroundServiceTests.cs`, the `BuildService` method currently ends with:

```csharp
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new AnalysisEngineBackgroundService(scopeFactory, NullLogger<AnalysisEngineBackgroundService>.Instance);
```

Change it to:

```csharp
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var healthStatus = new AnalysisEngineHealthStatus();

        return new AnalysisEngineBackgroundService(scopeFactory, healthStatus, NullLogger<AnalysisEngineBackgroundService>.Instance);
```

- [ ] **Step 8: Run the full affected test set**

Run: `dotnet test --filter "FullyQualifiedName~AnalysisEngineHealthStatusTests|FullyQualifiedName~AnalysisEngineBackgroundServiceTests"`
Expected: all passing (3 + 1 = 4 tests).

- [ ] **Step 9: Commit**

```bash
git add src/LogsPlatform.Web/Services/Analysis/AnalysisEngineHealthStatus.cs \
        src/LogsPlatform.Web/Services/Analysis/AnalysisEngineBackgroundService.cs \
        src/LogsPlatform.Web/Program.cs \
        tests/LogsPlatform.Tests/Web/AnalysisEngineHealthStatusTests.cs \
        tests/LogsPlatform.Tests/Web/AnalysisEngineBackgroundServiceTests.cs
git commit -m "feat: track last successful Analysis Engine tick for health reporting"
```

---

## Task 2: HealthController

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/HealthContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/HealthController.cs`
- Create: `tests/LogsPlatform.Tests/Web/HealthControllerTests.cs`

**Interfaces:**
- Consumes: `AnalysisEngineHealthStatus.LastTickCompletedAt : DateTime?`, `AnalysisEngineHealthStatus.RecordTickCompleted(DateTime) : void` (Task 1). `LogsPlatformDbContext.Database.CanConnectAsync() : Task<bool>` (existing EF Core API).
- Produces: nothing — this is the last task.

- [ ] **Step 1: Write the failing integration tests**

Create `tests/LogsPlatform.Tests/Web/HealthControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class HealthControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public HealthControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetHealth_NoCookie_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetHealth_DatabaseUpAndTickRecent_ReturnsHealthyWith200()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        using (var scope = _factory.Services.CreateScope())
        {
            var healthStatus = scope.ServiceProvider.GetRequiredService<AnalysisEngineHealthStatus>();
            healthStatus.RecordTickCompleted(DateTime.UtcNow);
        }

        var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("Healthy", body!.Status);
        Assert.Equal("Healthy", body.Database.Status);
        Assert.Equal("Healthy", body.AnalysisEngine.Status);
    }

    [Fact]
    public async Task GetHealth_TickIsStale_ReturnsUnhealthyWith503()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        using (var scope = _factory.Services.CreateScope())
        {
            var healthStatus = scope.ServiceProvider.GetRequiredService<AnalysisEngineHealthStatus>();
            healthStatus.RecordTickCompleted(DateTime.UtcNow.AddMinutes(-20));
        }

        var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("Unhealthy", body!.Status);
        Assert.Equal("Healthy", body.Database.Status);
        Assert.Equal("Stale", body.AnalysisEngine.Status);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build`
Expected: build errors — `HealthResponse`/`HealthController`/`AnalysisEngineHealthStatus` DI registration exist from Task 1, but `HealthResponse` (the contract) and the `/api/v1/health` route don't exist yet.

- [ ] **Step 3: Add the response contracts**

Create `src/LogsPlatform.Web/Contracts/HealthContracts.cs`:

```csharp
namespace LogsPlatform.Web.Contracts;

public record HealthResponse(string Status, DatabaseHealth Database, AnalysisEngineHealth AnalysisEngine);

public record DatabaseHealth(string Status, double ResponseTimeMs);

public record AnalysisEngineHealth(string Status, DateTime? LastTickCompletedAt, double? SecondsSinceLastTick);
```

- [ ] **Step 4: Implement HealthController**

Create `src/LogsPlatform.Web/Controllers/HealthController.cs`:

```csharp
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
```

No `[Authorize]` attribute is added — the app's fallback policy (`RequireAuthenticatedUser()`, set in `Program.cs`) already applies to every controller with no explicit `[Authorize]`/`[AllowAnonymous]`, which is exactly the "require login like everything else" behavior this plan calls for.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~HealthControllerTests"`
Expected: 3/3 passing.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: all tests green — this closes out both tasks.

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/HealthContracts.cs \
        src/LogsPlatform.Web/Controllers/HealthController.cs \
        tests/LogsPlatform.Tests/Web/HealthControllerTests.cs
git commit -m "feat: add GET /api/v1/health reporting DB connectivity and Analysis Engine liveness"
```

---

## Final Verification

- [ ] Run `dotnet build` — 0 errors.
- [ ] Run `dotnet test` — full suite green, including the 3 new `AnalysisEngineHealthStatusTests`, the updated `AnalysisEngineBackgroundServiceTests`, and the 3 new `HealthControllerTests`.
- [ ] Manually confirm via a live request (curl with a valid session cookie, or a browser after logging in) that `GET /api/v1/health` returns the expected JSON shape and a `200` while the app is freshly started (`analysisEngine.status` will read `"Unknown"` until the real background service's first tick completes, which is expected and correct per the Global Constraints).
- [ ] Invoke `superpowers:finishing-a-development-branch`.
