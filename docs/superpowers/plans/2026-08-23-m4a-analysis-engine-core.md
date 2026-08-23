# M4a: Analysis Engine Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Baseline learning, 3 anomaly detectors, 2 correlators, dedup, and an orchestrating `BackgroundService` — the full detection pipeline from `08-Analysis-ו-Anomaly-Detection.md`, minus the Findings API/UI (M4b).

**Architecture:** Four new entities (`Baseline`, `Finding`, `FindingStatement`, `Evidence`) under a new `src/LogsPlatform.Web/Services/Analysis/` folder, isolated from the existing CRUD-shaped `Services/` files since this is genuinely different code — statistical, batch-oriented, driven by a timer rather than an HTTP request.

**Tech Stack:** EF Core 10 (existing), `BackgroundService`/`PeriodicTimer` (new to this project). No new packages.

## Global Constraints

- **Corrected `Finding.Type` enum**: `ErrorSpike | MissingActivity | PerformanceDegradation | NewException | CustomerAnomaly` — per the design doc, `05-מודל-נתונים.md`'s listing is stale and contradicts `08`'s actual detector/correlator logic. `DeploymentCorrelation`/`DownstreamFailure` are never Finding types — correlators augment an *existing* Finding with a `Hypothesis`, never create their own.
- **`EnvironmentId` scoping** (this plan's correction to `08`, which never mentions Environment): `Baseline` and `Finding` both carry `EnvironmentId`. Every detector/correlator/repository method scopes by `(ApplicationId, EnvironmentId, ...)`, never just `(ApplicationId, ...)`. Without this, a Staging traffic blip would corrupt the same-named Operation's Production baseline.
- **FK cascade behavior** — follow `Deployment`'s exact precedent (`src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs:143-158`): for any entity whose FKs all transitively trace back to `Application` (which is true of `Baseline`/`Finding`, since `AppEnvironment.ApplicationId` already cascades), exactly ONE path may be `Cascade` — the entity's own direct `ApplicationId` — and every other FK that also traces back to `Application` (here, `EnvironmentId`, via `AppEnvironment`) must be `Restrict`. Getting this wrong produces SQL Server's "multiple cascade paths" migration-time error (this project has hit this exact class of bug twice before — B3's `Deployment`, M2a's `Event`).
- **Severity computation** (a gap in `08` this plan resolves — §6 defines `ConfidenceLevel`'s algorithm but never `Severity`'s): derived from the same z-score magnitude the detector already computes — `|z| > 5 → High`, `3 < |z| <= 5 → Medium`, otherwise `Low`. `NewExceptionDetector` always sets `Severity=High`.
- **The engine must never be able to write `FindingStatement.Kind = Conclusion`** — enforced at compile time, not by convention or a runtime check. `FindingWriter`'s statement-adding method takes a `DetectorStatementKind` enum (`Fact`|`Observation`|`Hypothesis` — deliberately 3 values, no `Conclusion`), not the full `FindingStatementKind` enum. No detector or correlator in this plan can pass a value that doesn't exist.
- **Parameters table** (from `08`'s §7 — one place, not scattered thresholds): `SPIKE_THRESHOLD=3`, `MIN_STDDEV_FLOOR=0.5`, `MIN_MEANINGFUL_ACTIVITY=5`, `BASELINE_LOOKBACK_DAYS=28`, `MIN_SAMPLES=14`, `DEPLOYMENT_CORRELATION_WINDOW=60` (minutes), `MIN_PEER_CUSTOMERS=5`, `CUSTOMER_OUTLIER_THRESHOLD=3`.
- **No mocking, no fakes** — every test hits real SQL Server LocalDB, per the project's standing convention. Repository/algorithm tests seed `Event`/`Deployment`/etc. rows directly via `DbContext` (matching `EventRepositoryQueryTests.cs`'s established M3 pattern), not through the ingestion HTTP endpoint — these tests are about aggregate-query and algorithm correctness, not the ingestion pipeline.
- **Verification `DbContext`s in tests** MUST be built via `new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options` directly, or via `TestDatabase.CreateContext()` when the test owns the whole database lifecycle for that test (both are already-established, correct patterns in this codebase — the pitfall is only ever calling `CreateContext()` a *second* time mid-test to read back what an earlier `CreateContext()`-based write produced, since that wipes the DB).
- Keep test volume to core-behavior-plus-one-edge-case per the project's standing test-volume instruction — this plan is inherently test-heavy given the algorithmic risk, but stay disciplined about not adding exhaustive threshold-boundary permutation tests (that's `11-Test-Strategy.md`'s job for M5's `SyntheticDataGenerator`, not this plan's unit tests).

---

### Task 1: Entities, enums, and migration

**Suggested model tier:** standard (the FK cascade-behavior reasoning is real judgment, not mechanical scaffolding, even though the entity shapes themselves are simple).

**Files:**
- Create: `src/LogsPlatform.Domain/Entities/Baseline.cs`
- Create: `src/LogsPlatform.Domain/Entities/Finding.cs`
- Create: `src/LogsPlatform.Domain/Entities/FindingStatement.cs`
- Create: `src/LogsPlatform.Domain/Entities/Evidence.cs`
- Modify: `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs`
- Test: `tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs` (append, matching its existing per-entity round-trip test pattern from M1-M3)

**Interfaces:**
- Consumes: `Application`, `AppEnvironment` (existing, unchanged).
- Produces (used by every later task): the 4 entities below, plus `AnalysisScopeType`, `AnalysisMetricType`, `FindingType`, `FindingSeverity`, `ConfidenceLevel`, `FindingStatus`, `FindingStatementKind`, `DetectorStatementKind`, `EvidenceType`.

- [ ] **Step 1: Write the failing test**

`tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs` — append this test class member (the file already exists with per-entity round-trip tests from prior milestones; add this as a new `[Fact]` inside the existing test class):

```csharp
    [Fact]
    public async Task Baseline_Finding_FindingStatement_Evidence_RoundTrip()
    {
        using var context = TestDatabase.CreateContext();

        var app = new Application { Name = "AnalysisEntitiesTestApp", CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();

        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();

        var baseline = new Baseline
        {
            ApplicationId = app.Id, EnvironmentId = env.Id,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = 14,
            MeanValue = 6.7, StdDevValue = 2.1, SampleCount = 21, LastUpdatedAt = DateTime.UtcNow
        };
        context.Baselines.Add(baseline);
        await context.SaveChangesAsync();

        var finding = new Finding
        {
            ApplicationId = app.Id, EnvironmentId = env.Id,
            Type = FindingType.ErrorSpike, ScopeType = AnalysisScopeType.Operation, ScopeId = 1,
            Title = "Error spike on ChargePayment", DetectedAt = DateTime.UtcNow,
            Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        };
        context.Findings.Add(finding);
        await context.SaveChangesAsync();

        context.FindingStatements.Add(new FindingStatement
        {
            FindingId = finding.Id, Kind = FindingStatementKind.Fact,
            Text = "Operation ChargePayment recorded 42 errors between 02:00-03:00.", OrderIndex = 0
        });
        context.Evidence.Add(new Evidence
        {
            FindingId = finding.Id, EvidenceType = EvidenceType.Baseline,
            ReferenceId = baseline.Id, Description = "Baseline row used for detection"
        });
        await context.SaveChangesAsync();

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);

        Assert.Equal(1, await verifyContext.Baselines.CountAsync(b => b.Id == baseline.Id));
        Assert.Equal(1, await verifyContext.Findings.CountAsync(f => f.Id == finding.Id));
        Assert.Equal(1, await verifyContext.FindingStatements.CountAsync(s => s.FindingId == finding.Id));
        Assert.Equal(1, await verifyContext.Evidence.CountAsync(e => e.FindingId == finding.Id));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter Baseline_Finding_FindingStatement_Evidence_RoundTrip`
Expected: FAIL — compile error, `Baseline`/`Finding`/`FindingStatement`/`Evidence`/the enums don't exist.

- [ ] **Step 3: Implement `Baseline.cs`**

`src/LogsPlatform.Domain/Entities/Baseline.cs`:

```csharp
namespace LogsPlatform.Domain.Entities;

public enum AnalysisScopeType { Operation, ExceptionGroup }

public enum AnalysisMetricType { EventCount, ExceptionCount, DurationMs }

public class Baseline
{
    public long Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public int EnvironmentId { get; set; }
    public AppEnvironment Environment { get; set; } = null!;
    public AnalysisScopeType ScopeType { get; set; }
    public long ScopeId { get; set; }
    public AnalysisMetricType MetricType { get; set; }
    public byte BucketHourOfDay { get; set; }
    public double MeanValue { get; set; }
    public double StdDevValue { get; set; }
    public int SampleCount { get; set; }
    public DateTime LastUpdatedAt { get; set; }
}
```

- [ ] **Step 4: Implement `Finding.cs`**

`src/LogsPlatform.Domain/Entities/Finding.cs`:

```csharp
namespace LogsPlatform.Domain.Entities;

public enum FindingType { ErrorSpike, MissingActivity, PerformanceDegradation, NewException, CustomerAnomaly }

public enum FindingSeverity { Low, Medium, High }

public enum ConfidenceLevel { Low, Medium, High }

public enum FindingStatus { New, Acknowledged, Resolved, Dismissed }

public class Finding
{
    public long Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public int EnvironmentId { get; set; }
    public AppEnvironment Environment { get; set; } = null!;
    public FindingType Type { get; set; }
    public AnalysisScopeType ScopeType { get; set; }
    public long ScopeId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; }
    public FindingSeverity Severity { get; set; }
    public ConfidenceLevel ConfidenceLevel { get; set; }
    public FindingStatus Status { get; set; }
}
```

- [ ] **Step 5: Implement `FindingStatement.cs`**

`src/LogsPlatform.Domain/Entities/FindingStatement.cs`:

```csharp
namespace LogsPlatform.Domain.Entities;

public enum FindingStatementKind { Fact, Observation, Hypothesis, Conclusion }

public enum DetectorStatementKind { Fact, Observation, Hypothesis }

public class FindingStatement
{
    public long Id { get; set; }
    public long FindingId { get; set; }
    public Finding Finding { get; set; } = null!;
    public FindingStatementKind Kind { get; set; }
    public string Text { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
}
```

- [ ] **Step 6: Implement `Evidence.cs`**

`src/LogsPlatform.Domain/Entities/Evidence.cs`:

```csharp
namespace LogsPlatform.Domain.Entities;

public enum EvidenceType { Event, ExceptionGroup, Deployment, Baseline, Operation }

public class Evidence
{
    public long Id { get; set; }
    public long FindingId { get; set; }
    public Finding Finding { get; set; } = null!;
    public EvidenceType EvidenceType { get; set; }
    public long ReferenceId { get; set; }
    public string Description { get; set; } = string.Empty;
}
```

- [ ] **Step 7: Register the entities in `LogsPlatformDbContext`**

In `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs`, add after the existing `ExceptionGroups` DbSet (line 25):

```csharp
    public DbSet<Baseline> Baselines => Set<Baseline>();
    public DbSet<Finding> Findings => Set<Finding>();
    public DbSet<FindingStatement> FindingStatements => Set<FindingStatement>();
    public DbSet<Evidence> Evidence => Set<Evidence>();
```

Add inside `OnModelCreating`, after the `Event` entity block (after line 199, before the closing `}` of `OnModelCreating`):

```csharp
        modelBuilder.Entity<Baseline>(entity =>
        {
            entity.HasOne(b => b.Application).WithMany().HasForeignKey(b => b.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(b => b.Environment).WithMany().HasForeignKey(b => b.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(b => new { b.ApplicationId, b.EnvironmentId, b.ScopeType, b.ScopeId, b.MetricType, b.BucketHourOfDay }).IsUnique();
        });

        modelBuilder.Entity<Finding>(entity =>
        {
            entity.Property(f => f.Title).HasMaxLength(500).IsRequired();
            entity.HasOne(f => f.Application).WithMany().HasForeignKey(f => f.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(f => f.Environment).WithMany().HasForeignKey(f => f.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(f => new { f.ApplicationId, f.EnvironmentId, f.ScopeType, f.ScopeId, f.Type, f.Status });
        });

        modelBuilder.Entity<FindingStatement>(entity =>
        {
            entity.Property(s => s.Text).HasMaxLength(2000).IsRequired();
            entity.Property(s => s.ApprovedBy).HasMaxLength(200);
            entity.HasOne(s => s.Finding).WithMany().HasForeignKey(s => s.FindingId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(s => new { s.FindingId, s.OrderIndex });
        });

        modelBuilder.Entity<Evidence>(entity =>
        {
            entity.Property(e => e.Description).HasMaxLength(1000).IsRequired();
            entity.HasOne(e => e.Finding).WithMany().HasForeignKey(e => e.FindingId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.FindingId);
        });
```

- [ ] **Step 8: Create the migration**

Run: `dotnet ef migrations add AddAnalysisEngineEntities --project src/LogsPlatform.Infrastructure --startup-project src/LogsPlatform.Web`
Expected: succeeds with no "multiple cascade paths" error (this is the specific failure mode Global Constraints warns about — if it occurs, the `EnvironmentId` FKs need `DeleteBehavior.Restrict`, not `Cascade`, which is exactly what Step 7's code already specifies, so this should succeed on the first attempt).

- [ ] **Step 9: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter Baseline_Finding_FindingStatement_Evidence_RoundTrip`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/LogsPlatform.Domain/Entities/Baseline.cs src/LogsPlatform.Domain/Entities/Finding.cs src/LogsPlatform.Domain/Entities/FindingStatement.cs src/LogsPlatform.Domain/Entities/Evidence.cs src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs src/LogsPlatform.Infrastructure/Migrations/ tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs
git commit -m "Add Baseline/Finding/FindingStatement/Evidence entities and migration"
```

---

### Task 2: `IMetricsRepository`

**Suggested model tier:** standard (EF Core aggregate query composition).

**Files:**
- Create: `src/LogsPlatform.Domain/Repositories/IMetricsRepository.cs`
- Create: `src/LogsPlatform.Infrastructure/Repositories/MetricsRepository.cs`
- Modify: `src/LogsPlatform.Web/Program.cs` (DI registration)
- Test: `tests/LogsPlatform.Tests/Infrastructure/MetricsRepositoryTests.cs`

**Interfaces:**
- Consumes: `Event` (existing, unchanged).
- Produces (used by Tasks 4, 6, 7, 8): `IMetricsRepository` with the 5 methods below.

- [ ] **Step 1: Write the failing tests**

`tests/LogsPlatform.Tests/Infrastructure/MetricsRepositoryTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class MetricsRepositoryTests
{
    private static async Task<(int ApplicationId, int EnvironmentId, int OperationId)> SeedAppEnvOperationAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();

        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        var module = new AppModule { ApplicationId = app.Id, Name = "Billing" };
        context.AppEnvironments.Add(env);
        context.Modules.Add(module);
        await context.SaveChangesAsync();

        var screenService = new ScreenService { ModuleId = module.Id, Name = "Invoicing", Type = ScreenServiceType.Service };
        context.ScreenServices.Add(screenService);
        await context.SaveChangesAsync();

        var process = new ProcessNode { ScreenServiceId = screenService.Id, Name = "ChargeCard" };
        context.Processes.Add(process);
        await context.SaveChangesAsync();

        var operation = new Operation { ProcessId = process.Id, Name = "Authorize" };
        context.Operations.Add(operation);
        await context.SaveChangesAsync();

        return (app.Id, env.Id, operation.Id);
    }

    [Fact]
    public async Task GetHourlyEventCountAsync_CountsOnlyEventsInTheHourWindow()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "MetricsCountTestApp");

        var hourStart = new DateTime(2026, 8, 23, 14, 0, 0, DateTimeKind.Utc);
        context.Events.AddRange(
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, Timestamp = hourStart.AddMinutes(10), Severity = 17, Message = "e1" },
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, Timestamp = hourStart.AddMinutes(50), Severity = 17, Message = "e2" },
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, Timestamp = hourStart.AddHours(-1), Severity = 17, Message = "outside" });
        await context.SaveChangesAsync();

        var repository = new MetricsRepository(context);
        var count = await repository.GetHourlyEventCountAsync(appId, envId, opId, hourStart);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GetHourlyAverageDurationAsync_AveragesOnlyEventsWithDuration()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "MetricsDurationTestApp");

        var hourStart = new DateTime(2026, 8, 23, 14, 0, 0, DateTimeKind.Utc);
        context.Events.AddRange(
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, Timestamp = hourStart.AddMinutes(10), Severity = 17, Message = "e1", DurationMs = 100 },
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, Timestamp = hourStart.AddMinutes(20), Severity = 17, Message = "e2", DurationMs = 200 },
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, Timestamp = hourStart.AddMinutes(30), Severity = 17, Message = "e3", DurationMs = null });
        await context.SaveChangesAsync();

        var repository = new MetricsRepository(context);
        var average = await repository.GetHourlyAverageDurationAsync(appId, envId, opId, hourStart);

        Assert.Equal(150, average);
    }

    [Fact]
    public async Task GetActiveOperationIdsAsync_ReturnsDistinctOperationsWithRecentEvents()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "MetricsActiveOpsTestApp");

        context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, Timestamp = DateTime.UtcNow, Severity = 17, Message = "recent" });
        await context.SaveChangesAsync();

        var repository = new MetricsRepository(context);
        var activeOps = await repository.GetActiveOperationIdsAsync(appId, envId);

        Assert.Contains(opId, activeOps);
    }

    [Fact]
    public async Task GetCustomerRatesAsync_GroupsCountsByCustomer()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "MetricsCustomerRatesTestApp");
        var customerA = new Customer { ApplicationId = appId, ExternalCustomerId = "cust-a", Name = "A" };
        var customerB = new Customer { ApplicationId = appId, ExternalCustomerId = "cust-b", Name = "B" };
        context.Customers.AddRange(customerA, customerB);
        await context.SaveChangesAsync();

        var windowStart = DateTime.UtcNow.AddHours(-1);
        context.Events.AddRange(
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customerA.Id, Timestamp = DateTime.UtcNow, Severity = 17, Message = "a1" },
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customerA.Id, Timestamp = DateTime.UtcNow, Severity = 17, Message = "a2" },
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customerB.Id, Timestamp = DateTime.UtcNow, Severity = 17, Message = "b1" });
        await context.SaveChangesAsync();

        var repository = new MetricsRepository(context);
        var rates = await repository.GetCustomerRatesAsync(appId, envId, opId, null, windowStart);

        Assert.Equal(2, rates[customerA.Id]);
        Assert.Equal(1, rates[customerB.Id]);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter MetricsRepositoryTests`
Expected: FAIL — compile error, `IMetricsRepository`/`MetricsRepository` don't exist.

- [ ] **Step 3: Implement `IMetricsRepository`**

`src/LogsPlatform.Domain/Repositories/IMetricsRepository.cs`:

```csharp
namespace LogsPlatform.Domain.Repositories;

public interface IMetricsRepository
{
    Task<int> GetHourlyEventCountAsync(int applicationId, int environmentId, int operationId, DateTime hourStart);
    Task<double?> GetHourlyAverageDurationAsync(int applicationId, int environmentId, int operationId, DateTime hourStart);
    Task<int> GetHourlyExceptionCountAsync(int applicationId, int environmentId, long exceptionGroupId, DateTime hourStart);
    Task<IReadOnlyList<int>> GetActiveOperationIdsAsync(int applicationId, int environmentId);
    Task<IReadOnlyList<long>> GetActiveExceptionGroupIdsAsync(int applicationId, int environmentId);
    Task<IReadOnlyDictionary<int, double>> GetCustomerRatesAsync(int applicationId, int environmentId, int? operationId, long? exceptionGroupId, DateTime windowStart);
}
```

- [ ] **Step 4: Implement `MetricsRepository`**

`src/LogsPlatform.Infrastructure/Repositories/MetricsRepository.cs`:

```csharp
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class MetricsRepository : IMetricsRepository
{
    private const int ActiveLookbackDays = 28;

    private readonly LogsPlatformDbContext _context;

    public MetricsRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<int> GetHourlyEventCountAsync(int applicationId, int environmentId, int operationId, DateTime hourStart)
    {
        var hourEnd = hourStart.AddHours(1);
        return await _context.Events.AsNoTracking()
            .CountAsync(e => e.ApplicationId == applicationId && e.EnvironmentId == environmentId
                && e.OperationId == operationId && e.Timestamp >= hourStart && e.Timestamp < hourEnd);
    }

    public async Task<double?> GetHourlyAverageDurationAsync(int applicationId, int environmentId, int operationId, DateTime hourStart)
    {
        var hourEnd = hourStart.AddHours(1);
        var durations = await _context.Events.AsNoTracking()
            .Where(e => e.ApplicationId == applicationId && e.EnvironmentId == environmentId
                && e.OperationId == operationId && e.Timestamp >= hourStart && e.Timestamp < hourEnd
                && e.DurationMs != null)
            .Select(e => e.DurationMs!.Value)
            .ToListAsync();

        return durations.Count == 0 ? null : durations.Average();
    }

    public async Task<int> GetHourlyExceptionCountAsync(int applicationId, int environmentId, long exceptionGroupId, DateTime hourStart)
    {
        var hourEnd = hourStart.AddHours(1);
        return await _context.Events.AsNoTracking()
            .CountAsync(e => e.ApplicationId == applicationId && e.EnvironmentId == environmentId
                && e.ExceptionGroupId == exceptionGroupId && e.Timestamp >= hourStart && e.Timestamp < hourEnd);
    }

    public async Task<IReadOnlyList<int>> GetActiveOperationIdsAsync(int applicationId, int environmentId)
    {
        var since = DateTime.UtcNow.AddDays(-ActiveLookbackDays);
        return await _context.Events.AsNoTracking()
            .Where(e => e.ApplicationId == applicationId && e.EnvironmentId == environmentId
                && e.OperationId != null && e.Timestamp >= since)
            .Select(e => e.OperationId!.Value)
            .Distinct()
            .ToListAsync();
    }

    public async Task<IReadOnlyList<long>> GetActiveExceptionGroupIdsAsync(int applicationId, int environmentId)
    {
        var since = DateTime.UtcNow.AddDays(-ActiveLookbackDays);
        return await _context.Events.AsNoTracking()
            .Where(e => e.ApplicationId == applicationId && e.EnvironmentId == environmentId
                && e.ExceptionGroupId != null && e.Timestamp >= since)
            .Select(e => e.ExceptionGroupId!.Value)
            .Distinct()
            .ToListAsync();
    }

    public async Task<IReadOnlyDictionary<int, double>> GetCustomerRatesAsync(
        int applicationId, int environmentId, int? operationId, long? exceptionGroupId, DateTime windowStart)
    {
        var query = _context.Events.AsNoTracking()
            .Where(e => e.ApplicationId == applicationId && e.EnvironmentId == environmentId
                && e.CustomerId != null && e.Timestamp >= windowStart);

        if (operationId is not null) query = query.Where(e => e.OperationId == operationId);
        if (exceptionGroupId is not null) query = query.Where(e => e.ExceptionGroupId == exceptionGroupId);

        var counts = await query
            .GroupBy(e => e.CustomerId!.Value)
            .Select(g => new { CustomerId = g.Key, Count = g.Count() })
            .ToListAsync();

        return counts.ToDictionary(c => c.CustomerId, c => (double)c.Count);
    }
}
```

- [ ] **Step 5: Register in `Program.cs`**

Add after the existing `IExceptionGroupRepository` registration in `src/LogsPlatform.Web/Program.cs`:

```csharp
builder.Services.AddScoped<IMetricsRepository, MetricsRepository>();
```

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter MetricsRepositoryTests`
Expected: PASS — 4/4 tests.

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Domain/Repositories/IMetricsRepository.cs src/LogsPlatform.Infrastructure/Repositories/MetricsRepository.cs src/LogsPlatform.Web/Program.cs tests/LogsPlatform.Tests/Infrastructure/MetricsRepositoryTests.cs
git commit -m "Add IMetricsRepository for analysis aggregate queries"
```

---

### Task 3: `IBaselineRepository` + `BaselineCalculator`

**Suggested model tier:** standard (the statistical algorithm is real judgment).

**Files:**
- Create: `src/LogsPlatform.Domain/Repositories/IBaselineRepository.cs`
- Create: `src/LogsPlatform.Infrastructure/Repositories/BaselineRepository.cs`
- Create: `src/LogsPlatform.Web/Services/Analysis/BaselineCalculator.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Test: `tests/LogsPlatform.Tests/Infrastructure/BaselineRepositoryTests.cs`
- Test: `tests/LogsPlatform.Tests/Web/BaselineCalculatorTests.cs`

**Interfaces:**
- Consumes: `Baseline`, `AnalysisScopeType`, `AnalysisMetricType` (Task 1); `IMetricsRepository` (Task 2).
- Produces (used by Tasks 5, 6, 7, 9, 10, 11): `IBaselineRepository` with `UpsertAsync(Baseline)`, `GetAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, AnalysisMetricType metricType, byte hourOfDay)`; `BaselineCalculator` with `RunAsync(int applicationId, int environmentId)`.

- [ ] **Step 1: Write the failing tests**

`tests/LogsPlatform.Tests/Infrastructure/BaselineRepositoryTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class BaselineRepositoryTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> SeedAppEnvAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();
        return (app.Id, env.Id);
    }

    [Fact]
    public async Task UpsertAsync_NoExistingRow_Inserts()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "BaselineUpsertInsertTestApp");
        var repository = new BaselineRepository(context);

        await repository.UpsertAsync(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = 1,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = 10, MeanValue = 5, StdDevValue = 1, SampleCount = 20, LastUpdatedAt = DateTime.UtcNow
        });

        var result = await repository.GetAsync(appId, envId, AnalysisScopeType.Operation, 1, AnalysisMetricType.EventCount, 10);
        Assert.NotNull(result);
        Assert.Equal(5, result!.MeanValue);
    }

    [Fact]
    public async Task UpsertAsync_ExistingRow_Updates()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "BaselineUpsertUpdateTestApp");
        var repository = new BaselineRepository(context);

        await repository.UpsertAsync(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = 1,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = 10, MeanValue = 5, StdDevValue = 1, SampleCount = 20, LastUpdatedAt = DateTime.UtcNow
        });
        await repository.UpsertAsync(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = 1,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = 10, MeanValue = 8, StdDevValue = 2, SampleCount = 21, LastUpdatedAt = DateTime.UtcNow
        });

        var result = await repository.GetAsync(appId, envId, AnalysisScopeType.Operation, 1, AnalysisMetricType.EventCount, 10);
        Assert.Equal(8, result!.MeanValue);

        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var rowCount = verifyContext.Baselines.Count(b => b.ApplicationId == appId && b.EnvironmentId == envId && b.ScopeId == 1 && b.BucketHourOfDay == 10);
        Assert.Equal(1, rowCount);
    }
}
```

`tests/LogsPlatform.Tests/Web/BaselineCalculatorTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class BaselineCalculatorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId, int OperationId)> SeedAppEnvOperationAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        var module = new AppModule { ApplicationId = app.Id, Name = "Billing" };
        context.AppEnvironments.Add(env);
        context.Modules.Add(module);
        await context.SaveChangesAsync();
        var screenService = new ScreenService { ModuleId = module.Id, Name = "Invoicing", Type = ScreenServiceType.Service };
        context.ScreenServices.Add(screenService);
        await context.SaveChangesAsync();
        var process = new ProcessNode { ScreenServiceId = screenService.Id, Name = "ChargeCard" };
        context.Processes.Add(process);
        await context.SaveChangesAsync();
        var operation = new Operation { ProcessId = process.Id, Name = "Authorize" };
        context.Operations.Add(operation);
        await context.SaveChangesAsync();
        return (app.Id, env.Id, operation.Id);
    }

    [Fact]
    public async Task RunAsync_KnownDistribution_ComputesExpectedMeanAndStdDev()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "BaselineCalcKnownDistTestApp");

        // Seed exactly 20 daily samples at hour 14: 10 events each day.
        var today = DateTime.UtcNow.Date;
        for (var day = 1; day <= 20; day++)
        {
            var hourStart = today.AddDays(-day).AddHours(14);
            for (var i = 0; i < 10; i++)
            {
                context.Events.Add(new Event
                {
                    ApplicationId = appId, EnvironmentId = envId, OperationId = opId,
                    Timestamp = hourStart.AddMinutes(i), Severity = 17, Message = $"evt-{day}-{i}"
                });
            }
        }
        await context.SaveChangesAsync();

        var metricsRepository = new MetricsRepository(context);
        var baselineRepository = new BaselineRepository(context);
        var calculator = new BaselineCalculator(metricsRepository, baselineRepository);

        await calculator.RunAsync(appId, envId);

        var baseline = await baselineRepository.GetAsync(appId, envId, AnalysisScopeType.Operation, opId, AnalysisMetricType.EventCount, 14);
        Assert.NotNull(baseline);
        Assert.Equal(10, baseline!.MeanValue, precision: 5);
        Assert.Equal(0, baseline.StdDevValue, precision: 5);
        Assert.Equal(20, baseline.SampleCount);
    }

    [Fact]
    public async Task RunAsync_FewerThanMinSamples_StillSavesRow()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "BaselineCalcFewSamplesTestApp");

        var today = DateTime.UtcNow.Date;
        for (var day = 1; day <= 3; day++)
        {
            context.Events.Add(new Event
            {
                ApplicationId = appId, EnvironmentId = envId, OperationId = opId,
                Timestamp = today.AddDays(-day).AddHours(9), Severity = 17, Message = $"evt-{day}"
            });
        }
        await context.SaveChangesAsync();

        var metricsRepository = new MetricsRepository(context);
        var baselineRepository = new BaselineRepository(context);
        var calculator = new BaselineCalculator(metricsRepository, baselineRepository);

        await calculator.RunAsync(appId, envId);

        var baseline = await baselineRepository.GetAsync(appId, envId, AnalysisScopeType.Operation, opId, AnalysisMetricType.EventCount, 9);
        Assert.NotNull(baseline);
        Assert.Equal(3, baseline!.SampleCount);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter "BaselineRepositoryTests|BaselineCalculatorTests"`
Expected: FAIL — compile error, `IBaselineRepository`/`BaselineRepository`/`BaselineCalculator` don't exist.

- [ ] **Step 3: Implement `IBaselineRepository`**

`src/LogsPlatform.Domain/Repositories/IBaselineRepository.cs`:

```csharp
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IBaselineRepository
{
    Task UpsertAsync(Baseline baseline);
    Task<Baseline?> GetAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, AnalysisMetricType metricType, byte hourOfDay);
    Task<bool> HasUpdatedTodayAsync(int applicationId, int environmentId);
}
```

- [ ] **Step 4: Implement `BaselineRepository`**

`src/LogsPlatform.Infrastructure/Repositories/BaselineRepository.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class BaselineRepository : IBaselineRepository
{
    private readonly LogsPlatformDbContext _context;

    public BaselineRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task UpsertAsync(Baseline baseline)
    {
        var existing = await _context.Baselines.FirstOrDefaultAsync(b =>
            b.ApplicationId == baseline.ApplicationId && b.EnvironmentId == baseline.EnvironmentId &&
            b.ScopeType == baseline.ScopeType && b.ScopeId == baseline.ScopeId &&
            b.MetricType == baseline.MetricType && b.BucketHourOfDay == baseline.BucketHourOfDay);

        if (existing is null)
        {
            _context.Baselines.Add(baseline);
        }
        else
        {
            existing.MeanValue = baseline.MeanValue;
            existing.StdDevValue = baseline.StdDevValue;
            existing.SampleCount = baseline.SampleCount;
            existing.LastUpdatedAt = baseline.LastUpdatedAt;
        }

        await _context.SaveChangesAsync();
    }

    public async Task<Baseline?> GetAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, AnalysisMetricType metricType, byte hourOfDay) =>
        await _context.Baselines.AsNoTracking().FirstOrDefaultAsync(b =>
            b.ApplicationId == applicationId && b.EnvironmentId == environmentId &&
            b.ScopeType == scopeType && b.ScopeId == scopeId &&
            b.MetricType == metricType && b.BucketHourOfDay == hourOfDay);

    public async Task<bool> HasUpdatedTodayAsync(int applicationId, int environmentId)
    {
        var todayStart = DateTime.UtcNow.Date;
        return await _context.Baselines.AsNoTracking().AnyAsync(b =>
            b.ApplicationId == applicationId && b.EnvironmentId == environmentId && b.LastUpdatedAt >= todayStart);
    }
}
```

- [ ] **Step 5: Implement `BaselineCalculator`**

`src/LogsPlatform.Web/Services/Analysis/BaselineCalculator.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class BaselineCalculator
{
    private const int BaselineLookbackDays = 28;
    private const int MinSamples = 14;

    private readonly IMetricsRepository _metrics;
    private readonly IBaselineRepository _baselines;

    public BaselineCalculator(IMetricsRepository metrics, IBaselineRepository baselines)
    {
        _metrics = metrics;
        _baselines = baselines;
    }

    public async Task RunAsync(int applicationId, int environmentId)
    {
        var operationIds = await _metrics.GetActiveOperationIdsAsync(applicationId, environmentId);
        foreach (var operationId in operationIds)
        {
            await ComputeAndSaveAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, AnalysisMetricType.EventCount,
                hourStart => _metrics.GetHourlyEventCountAsync(applicationId, environmentId, operationId, hourStart).ContinueWith(t => (double?)t.Result));
            await ComputeAndSaveAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, AnalysisMetricType.DurationMs,
                hourStart => _metrics.GetHourlyAverageDurationAsync(applicationId, environmentId, operationId, hourStart));
        }

        var exceptionGroupIds = await _metrics.GetActiveExceptionGroupIdsAsync(applicationId, environmentId);
        foreach (var exceptionGroupId in exceptionGroupIds)
        {
            await ComputeAndSaveAsync(applicationId, environmentId, AnalysisScopeType.ExceptionGroup, exceptionGroupId, AnalysisMetricType.ExceptionCount,
                hourStart => _metrics.GetHourlyExceptionCountAsync(applicationId, environmentId, exceptionGroupId, hourStart).ContinueWith(t => (double?)t.Result));
        }
    }

    private async Task ComputeAndSaveAsync(
        int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, AnalysisMetricType metricType,
        Func<DateTime, Task<double?>> sampleAt)
    {
        var now = DateTime.UtcNow;

        for (byte hour = 0; hour < 24; hour++)
        {
            var samples = new List<double>();

            for (var dayOffset = 1; dayOffset <= BaselineLookbackDays; dayOffset++)
            {
                var hourStart = now.Date.AddDays(-dayOffset).AddHours(hour);
                var value = await sampleAt(hourStart);
                if (value is not null)
                {
                    samples.Add(value.Value);
                }
            }

            if (samples.Count == 0)
            {
                continue;
            }

            var mean = samples.Average();
            var variance = samples.Count > 1 ? samples.Sum(v => (v - mean) * (v - mean)) / samples.Count : 0;
            var stdDev = Math.Sqrt(variance);

            await _baselines.UpsertAsync(new Baseline
            {
                ApplicationId = applicationId,
                EnvironmentId = environmentId,
                ScopeType = scopeType,
                ScopeId = scopeId,
                MetricType = metricType,
                BucketHourOfDay = hour,
                MeanValue = mean,
                StdDevValue = stdDev,
                SampleCount = samples.Count,
                LastUpdatedAt = now
            });
        }
    }
}
```

Note: `MinSamples` is declared here for later tasks (`RateAnomalyDetector`, `CustomerOutlierDetector`) to reference via `ConfidenceLevel` computation — this constant is duplicated intentionally rather than shared, since `08`'s parameter table (Global Constraints) is the single source of truth for the *values*, and each consuming class declaring its own `private const` matching that table is simpler than introducing a shared constants class for 8 numbers used across ~5 files. If this becomes error-prone in practice, a shared `AnalysisParameters` static class would be the natural follow-up — not needed for this plan's scope.

- [ ] **Step 6: Register in `Program.cs`**

Add after the `IMetricsRepository` registration:

```csharp
builder.Services.AddScoped<IBaselineRepository, BaselineRepository>();
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.BaselineCalculator>();
```

- [ ] **Step 7: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter "BaselineRepositoryTests|BaselineCalculatorTests"`
Expected: PASS — 4/4 tests.

- [ ] **Step 8: Commit**

```bash
git add src/LogsPlatform.Domain/Repositories/IBaselineRepository.cs src/LogsPlatform.Infrastructure/Repositories/BaselineRepository.cs src/LogsPlatform.Web/Services/Analysis/BaselineCalculator.cs src/LogsPlatform.Web/Program.cs tests/LogsPlatform.Tests/Infrastructure/BaselineRepositoryTests.cs tests/LogsPlatform.Tests/Web/BaselineCalculatorTests.cs
git commit -m "Add IBaselineRepository and BaselineCalculator (Phase 1: Baseline learning)"
```

---

### Task 4: `IFindingRepository` + `FindingWriter`

**Suggested model tier:** standard (dedup logic + the compile-time Conclusion-safety design is real judgment).

**Files:**
- Create: `src/LogsPlatform.Domain/Repositories/IFindingRepository.cs`
- Create: `src/LogsPlatform.Infrastructure/Repositories/FindingRepository.cs`
- Create: `src/LogsPlatform.Web/Services/Analysis/FindingWriter.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Test: `tests/LogsPlatform.Tests/Infrastructure/FindingRepositoryTests.cs`
- Test: `tests/LogsPlatform.Tests/Web/FindingWriterTests.cs`

**Interfaces:**
- Consumes: `Finding`, `FindingStatement`, `Evidence`, `FindingType`, `FindingSeverity`, `ConfidenceLevel`, `FindingStatus`, `DetectorStatementKind` (Task 1).
- Produces (used by Tasks 5, 6, 7, 9, 10): `IFindingRepository` with `FindOpenAsync(...)`, `AddAsync(Finding)`, `AddStatementAsync(long findingId, DetectorStatementKind kind, string text)`, `AddEvidenceAsync(long findingId, EvidenceType type, long referenceId, string description)`, `GetByIdAsync(long id)`. `FindingWriter` with `WriteAsync(FindingDraft draft)` returning the resulting `Finding` (new or reused), where `FindingDraft` bundles everything a detector needs to hand over.

- [ ] **Step 1: Write the failing tests**

`tests/LogsPlatform.Tests/Infrastructure/FindingRepositoryTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class FindingRepositoryTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> SeedAppEnvAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();
        return (app.Id, env.Id);
    }

    [Fact]
    public async Task FindOpenAsync_MatchingOpenFinding_ReturnsIt()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoOpenTestApp");
        var repository = new FindingRepository(context);

        var finding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        var found = await repository.FindOpenAsync(appId, envId, AnalysisScopeType.Operation, 1, FindingType.ErrorSpike, cooldownSince: DateTime.UtcNow.AddHours(-24));

        Assert.NotNull(found);
        Assert.Equal(finding.Id, found!.Id);
    }

    [Fact]
    public async Task FindOpenAsync_ResolvedFinding_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoResolvedTestApp");
        var repository = new FindingRepository(context);

        await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.Resolved
        });

        var found = await repository.FindOpenAsync(appId, envId, AnalysisScopeType.Operation, 1, FindingType.ErrorSpike, cooldownSince: DateTime.UtcNow.AddHours(-24));

        Assert.Null(found);
    }

    [Fact]
    public async Task AddStatementAsync_PersistsWithCorrectKind()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoStatementTestApp");
        var repository = new FindingRepository(context);
        var finding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.NewException,
            ScopeType = AnalysisScopeType.ExceptionGroup, ScopeId = 1, Title = "test",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        await repository.AddStatementAsync(finding.Id, DetectorStatementKind.Fact, "A fact.");

        var reloaded = await repository.GetByIdAsync(finding.Id);
        Assert.Single(reloaded!.Statements);
        Assert.Equal(FindingStatementKind.Fact, reloaded.Statements[0].Kind);
    }
}
```

`tests/LogsPlatform.Tests/Web/FindingWriterTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class FindingWriterTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> SeedAppEnvAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();
        return (app.Id, env.Id);
    }

    [Fact]
    public async Task WriteAsync_TwoCallsSameScope_ReusesFindingAndAppendsFact()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingWriterDedupTestApp");
        var repository = new FindingRepository(context);
        var writer = new FindingWriter(repository);

        var draft = new FindingDraft(appId, envId, FindingType.ErrorSpike, AnalysisScopeType.Operation, 1,
            "Error spike", FindingSeverity.High, ConfidenceLevel.High,
            new[] { (DetectorStatementKind.Fact, "First detection.") });

        var first = await writer.WriteAsync(draft);
        var second = await writer.WriteAsync(draft with { Statements = new[] { (DetectorStatementKind.Fact, "Second detection, still ongoing.") } });

        Assert.Equal(first.Id, second.Id);
        var reloaded = await repository.GetByIdAsync(first.Id);
        Assert.Equal(2, reloaded!.Statements.Count);
    }

    [Fact]
    public async Task WriteAsync_NoExistingFinding_CreatesNew()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingWriterCreateTestApp");
        var repository = new FindingRepository(context);
        var writer = new FindingWriter(repository);

        var draft = new FindingDraft(appId, envId, FindingType.NewException, AnalysisScopeType.ExceptionGroup, 1,
            "New exception", FindingSeverity.High, ConfidenceLevel.High,
            new[] { (DetectorStatementKind.Fact, "First-ever occurrence.") });

        var finding = await writer.WriteAsync(draft);

        Assert.True(finding.Id > 0);
        Assert.Equal(FindingStatus.New, finding.Status);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter "FindingRepositoryTests|FindingWriterTests"`
Expected: FAIL — compile error, `IFindingRepository`/`FindingRepository`/`FindingWriter`/`FindingDraft` don't exist.

- [ ] **Step 3: Implement `IFindingRepository`**

`src/LogsPlatform.Domain/Repositories/IFindingRepository.cs`:

```csharp
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IFindingRepository
{
    Task<Finding?> FindOpenAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type, DateTime cooldownSince);
    Task<Finding> AddAsync(Finding finding);
    Task AddStatementAsync(long findingId, DetectorStatementKind kind, string text);
    Task AddEvidenceAsync(long findingId, EvidenceType evidenceType, long referenceId, string description);
    Task<FindingWithDetails?> GetByIdAsync(long id);
}

public record FindingWithDetails(Finding Finding, IReadOnlyList<FindingStatement> Statements, IReadOnlyList<Evidence> Evidence)
{
    public long Id => Finding.Id;
    public FindingStatus Status => Finding.Status;
}
```

Note: `FindingWithDetails` wraps the entity with its statements/evidence loaded, rather than relying on lazy-loading (this project doesn't use EF Core lazy-loading proxies anywhere) or exposing navigation collections directly on `Finding` itself (kept off `Finding` to avoid a circular/heavy entity graph — `Finding` stays a plain flat row, matching every other entity in this project).

- [ ] **Step 4: Implement `FindingRepository`**

`src/LogsPlatform.Infrastructure/Repositories/FindingRepository.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class FindingRepository : IFindingRepository
{
    private readonly LogsPlatformDbContext _context;

    public FindingRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<Finding?> FindOpenAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type, DateTime cooldownSince) =>
        await _context.Findings.AsNoTracking().FirstOrDefaultAsync(f =>
            f.ApplicationId == applicationId && f.EnvironmentId == environmentId &&
            f.ScopeType == scopeType && f.ScopeId == scopeId && f.Type == type &&
            (f.Status == FindingStatus.New || f.Status == FindingStatus.Acknowledged) &&
            f.DetectedAt >= cooldownSince);

    public async Task<Finding> AddAsync(Finding finding)
    {
        _context.Findings.Add(finding);
        await _context.SaveChangesAsync();
        return finding;
    }

    public async Task AddStatementAsync(long findingId, DetectorStatementKind kind, string text)
    {
        var maxOrderIndex = await _context.FindingStatements
            .Where(s => s.FindingId == findingId)
            .Select(s => (int?)s.OrderIndex)
            .MaxAsync() ?? -1;

        _context.FindingStatements.Add(new FindingStatement
        {
            FindingId = findingId,
            Kind = (FindingStatementKind)kind,
            Text = text,
            OrderIndex = maxOrderIndex + 1
        });
        await _context.SaveChangesAsync();
    }

    public async Task AddEvidenceAsync(long findingId, EvidenceType evidenceType, long referenceId, string description)
    {
        _context.Evidence.Add(new Evidence
        {
            FindingId = findingId,
            EvidenceType = evidenceType,
            ReferenceId = referenceId,
            Description = description
        });
        await _context.SaveChangesAsync();
    }

    public async Task<FindingWithDetails?> GetByIdAsync(long id)
    {
        var finding = await _context.Findings.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
        if (finding is null)
        {
            return null;
        }

        var statements = await _context.FindingStatements.AsNoTracking()
            .Where(s => s.FindingId == id).OrderBy(s => s.OrderIndex).ToListAsync();
        var evidence = await _context.Evidence.AsNoTracking()
            .Where(e => e.FindingId == id).ToListAsync();

        return new FindingWithDetails(finding, statements, evidence);
    }
}
```

Note: `(FindingStatementKind)kind` casts `DetectorStatementKind` to `FindingStatementKind` — safe because `DetectorStatementKind`'s 3 values (`Fact=0, Observation=1, Hypothesis=2`) are declared in the same order as `FindingStatementKind`'s first 3 values (`Fact=0, Observation=1, Hypothesis=2, Conclusion=3`), so the underlying `int` values line up exactly. This is the one place in the codebase where the "restricted enum" and "full enum" meet — everywhere else, code only ever sees one or the other.

- [ ] **Step 5: Implement `FindingWriter`**

`src/LogsPlatform.Web/Services/Analysis/FindingWriter.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public record FindingDraft(
    int ApplicationId,
    int EnvironmentId,
    FindingType Type,
    AnalysisScopeType ScopeType,
    long ScopeId,
    string Title,
    FindingSeverity Severity,
    ConfidenceLevel ConfidenceLevel,
    IReadOnlyList<(DetectorStatementKind Kind, string Text)> Statements);

public class FindingWriter
{
    private static readonly TimeSpan CooldownWindow = TimeSpan.FromHours(24);

    private readonly IFindingRepository _findings;

    public FindingWriter(IFindingRepository findings)
    {
        _findings = findings;
    }

    public async Task<Finding> WriteAsync(FindingDraft draft)
    {
        var existing = await _findings.FindOpenAsync(
            draft.ApplicationId, draft.EnvironmentId, draft.ScopeType, draft.ScopeId, draft.Type,
            cooldownSince: DateTime.UtcNow - CooldownWindow);

        Finding finding;
        if (existing is not null)
        {
            finding = existing;
        }
        else
        {
            finding = await _findings.AddAsync(new Finding
            {
                ApplicationId = draft.ApplicationId,
                EnvironmentId = draft.EnvironmentId,
                Type = draft.Type,
                ScopeType = draft.ScopeType,
                ScopeId = draft.ScopeId,
                Title = draft.Title,
                DetectedAt = DateTime.UtcNow,
                Severity = draft.Severity,
                ConfidenceLevel = draft.ConfidenceLevel,
                Status = FindingStatus.New
            });
        }

        foreach (var (kind, text) in draft.Statements)
        {
            await _findings.AddStatementAsync(finding.Id, kind, text);
        }

        return finding;
    }
}
```

- [ ] **Step 6: Register in `Program.cs`**

Add after the `BaselineCalculator` registration:

```csharp
builder.Services.AddScoped<IFindingRepository, FindingRepository>();
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.FindingWriter>();
```

- [ ] **Step 7: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter "FindingRepositoryTests|FindingWriterTests"`
Expected: PASS — 5/5 tests.

- [ ] **Step 8: Commit**

```bash
git add src/LogsPlatform.Domain/Repositories/IFindingRepository.cs src/LogsPlatform.Infrastructure/Repositories/FindingRepository.cs src/LogsPlatform.Web/Services/Analysis/FindingWriter.cs src/LogsPlatform.Web/Program.cs tests/LogsPlatform.Tests/Infrastructure/FindingRepositoryTests.cs tests/LogsPlatform.Tests/Web/FindingWriterTests.cs
git commit -m "Add IFindingRepository and FindingWriter (dedup, Conclusion-safe by construction)"
```

---

### Task 5: `RateAnomalyDetector`

**Suggested model tier:** standard-to-high (the core z-score algorithm, exercised across 3 Finding types via one detector).

**Files:**
- Create: `src/LogsPlatform.Web/Services/Analysis/RateAnomalyDetector.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Test: `tests/LogsPlatform.Tests/Web/RateAnomalyDetectorTests.cs`

**Interfaces:**
- Consumes: `IMetricsRepository` (Task 2), `IBaselineRepository` (Task 3), `FindingWriter`/`FindingDraft` (Task 4).
- Produces (used by Task 11): `RateAnomalyDetector` with `RunAsync(int applicationId, int environmentId)`.

- [ ] **Step 1: Write the failing tests**

`tests/LogsPlatform.Tests/Web/RateAnomalyDetectorTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class RateAnomalyDetectorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId, int OperationId)> SeedAppEnvOperationAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        var module = new AppModule { ApplicationId = app.Id, Name = "Billing" };
        context.AppEnvironments.Add(env);
        context.Modules.Add(module);
        await context.SaveChangesAsync();
        var screenService = new ScreenService { ModuleId = module.Id, Name = "Invoicing", Type = ScreenServiceType.Service };
        context.ScreenServices.Add(screenService);
        await context.SaveChangesAsync();
        var process = new ProcessNode { ScreenServiceId = screenService.Id, Name = "ChargeCard" };
        context.Processes.Add(process);
        await context.SaveChangesAsync();
        var operation = new Operation { ProcessId = process.Id, Name = "Authorize" };
        context.Operations.Add(operation);
        await context.SaveChangesAsync();
        return (app.Id, env.Id, operation.Id);
    }

    [Fact]
    public async Task RunAsync_CurrentHourFarAboveBaseline_CreatesErrorSpikeFinding()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "RateAnomalySpikeTestApp");

        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        context.Baselines.Add(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = opId,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = (byte)currentHourStart.Hour,
            MeanValue = 5, StdDevValue = 1, SampleCount = 20, LastUpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        for (var i = 0; i < 40; i++)
        {
            context.Events.Add(new Event
            {
                ApplicationId = appId, EnvironmentId = envId, OperationId = opId,
                Timestamp = currentHourStart.AddMinutes(i % 59), Severity = 17, Message = $"spike-{i}"
            });
        }
        await context.SaveChangesAsync();

        var metrics = new MetricsRepository(context);
        var baselines = new BaselineRepository(context);
        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var detector = new RateAnomalyDetector(metrics, baselines, writer);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.ErrorSpike);

        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.High, finding!.Severity);
    }

    [Fact]
    public async Task RunAsync_CurrentHourWithinNormalRange_NoFindingCreated()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "RateAnomalyNormalTestApp");

        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        context.Baselines.Add(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = opId,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = (byte)currentHourStart.Hour,
            MeanValue = 5, StdDevValue = 1, SampleCount = 20, LastUpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        for (var i = 0; i < 5; i++)
        {
            context.Events.Add(new Event
            {
                ApplicationId = appId, EnvironmentId = envId, OperationId = opId,
                Timestamp = currentHourStart.AddMinutes(i), Severity = 17, Message = $"normal-{i}"
            });
        }
        await context.SaveChangesAsync();

        var metrics = new MetricsRepository(context);
        var baselines = new BaselineRepository(context);
        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var detector = new RateAnomalyDetector(metrics, baselines, writer);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var findingCount = await verifyContext.Findings.CountAsync(f => f.ApplicationId == appId);

        Assert.Equal(0, findingCount);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter RateAnomalyDetectorTests`
Expected: FAIL — compile error, `RateAnomalyDetector` doesn't exist.

- [ ] **Step 3: Implement `RateAnomalyDetector`**

`src/LogsPlatform.Web/Services/Analysis/RateAnomalyDetector.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class RateAnomalyDetector
{
    private const double SpikeThreshold = 3;
    private const double MinStdDevFloor = 0.5;
    private const double MinMeaningfulActivity = 5;

    private readonly IMetricsRepository _metrics;
    private readonly IBaselineRepository _baselines;
    private readonly FindingWriter _writer;

    public RateAnomalyDetector(IMetricsRepository metrics, IBaselineRepository baselines, FindingWriter writer)
    {
        _metrics = metrics;
        _baselines = baselines;
        _writer = writer;
    }

    public async Task RunAsync(int applicationId, int environmentId)
    {
        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        var hour = (byte)currentHourStart.Hour;

        var operationIds = await _metrics.GetActiveOperationIdsAsync(applicationId, environmentId);
        foreach (var operationId in operationIds)
        {
            var eventCount = await _metrics.GetHourlyEventCountAsync(applicationId, environmentId, operationId, currentHourStart);
            await EvaluateAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, AnalysisMetricType.EventCount, hour,
                current: eventCount, positiveType: FindingType.ErrorSpike, negativeType: FindingType.MissingActivity,
                titlePrefix: $"Operation {operationId}");

            var averageDuration = await _metrics.GetHourlyAverageDurationAsync(applicationId, environmentId, operationId, currentHourStart);
            if (averageDuration is not null)
            {
                await EvaluateAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, AnalysisMetricType.DurationMs, hour,
                    current: averageDuration.Value, positiveType: FindingType.PerformanceDegradation, negativeType: null,
                    titlePrefix: $"Operation {operationId}");
            }
        }

        var exceptionGroupIds = await _metrics.GetActiveExceptionGroupIdsAsync(applicationId, environmentId);
        foreach (var exceptionGroupId in exceptionGroupIds)
        {
            var exceptionCount = await _metrics.GetHourlyExceptionCountAsync(applicationId, environmentId, exceptionGroupId, currentHourStart);
            await EvaluateAsync(applicationId, environmentId, AnalysisScopeType.ExceptionGroup, exceptionGroupId, AnalysisMetricType.ExceptionCount, hour,
                current: exceptionCount, positiveType: FindingType.ErrorSpike, negativeType: null,
                titlePrefix: $"ExceptionGroup {exceptionGroupId}");
        }
    }

    private async Task EvaluateAsync(
        int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, AnalysisMetricType metricType, byte hour,
        double current, FindingType positiveType, FindingType? negativeType, string titlePrefix)
    {
        var baseline = await _baselines.GetAsync(applicationId, environmentId, scopeType, scopeId, metricType, hour);
        if (baseline is null)
        {
            return;
        }

        var stdDev = Math.Max(baseline.StdDevValue, MinStdDevFloor);
        var z = (current - baseline.MeanValue) / stdDev;

        if (z > SpikeThreshold)
        {
            await WriteRateFindingAsync(applicationId, environmentId, scopeType, scopeId, positiveType, z, current, baseline, titlePrefix, "above");
        }
        else if (negativeType is not null && z < -SpikeThreshold && baseline.MeanValue > MinMeaningfulActivity)
        {
            await WriteRateFindingAsync(applicationId, environmentId, scopeType, scopeId, negativeType.Value, z, current, baseline, titlePrefix, "below");
        }
    }

    private async Task WriteRateFindingAsync(
        int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type,
        double z, double current, Baseline baseline, string titlePrefix, string direction)
    {
        var absZ = Math.Abs(z);
        var severity = absZ > 5 ? FindingSeverity.High : FindingSeverity.Medium;
        var confidence = absZ > 5 && baseline.SampleCount >= 14 ? ConfidenceLevel.High
            : baseline.SampleCount < 14 ? ConfidenceLevel.Low
            : ConfidenceLevel.Medium;

        var fact = $"{titlePrefix} recorded {current:F1} in the current hour.";
        var observation = $"That is {direction} the normal rate for this hour (baseline: {baseline.MeanValue:F1}±{baseline.StdDevValue:F1}, based on {baseline.SampleCount} days).";

        var draft = new FindingDraft(
            applicationId, environmentId, type, scopeType, scopeId,
            $"{titlePrefix}: {type}", severity, confidence,
            new[] { (DetectorStatementKind.Fact, fact), (DetectorStatementKind.Observation, observation) });

        var finding = await _writer.WriteAsync(draft);
    }
}
```

- [ ] **Step 4: Register in `Program.cs`**

Add after the `FindingWriter` registration:

```csharp
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.RateAnomalyDetector>();
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter RateAnomalyDetectorTests`
Expected: PASS — 2/2 tests.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Services/Analysis/RateAnomalyDetector.cs src/LogsPlatform.Web/Program.cs tests/LogsPlatform.Tests/Web/RateAnomalyDetectorTests.cs
git commit -m "Add RateAnomalyDetector (ErrorSpike/MissingActivity/PerformanceDegradation)"
```

---

### Task 6: `NewExceptionDetector`

**Suggested model tier:** standard.

**Files:**
- Create: `src/LogsPlatform.Web/Services/Analysis/NewExceptionDetector.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Test: `tests/LogsPlatform.Tests/Web/NewExceptionDetectorTests.cs`

**Interfaces:**
- Consumes: `FindingWriter`/`FindingDraft` (Task 4). Reads `ExceptionGroup`/`Event` directly via `LogsPlatformDbContext` (this detector's scan shape — "groups first-seen in the last 5 minutes" plus "which environments saw them" — doesn't fit `IMetricsRepository`'s existing methods and isn't reused elsewhere, so it queries the context directly rather than growing `IMetricsRepository` for a single caller).
- Produces (used by Task 11): `NewExceptionDetector` with `RunAsync(int applicationId, int environmentId)`.

- [ ] **Step 1: Write the failing tests**

`tests/LogsPlatform.Tests/Web/NewExceptionDetectorTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class NewExceptionDetectorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> SeedAppEnvAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();
        return (app.Id, env.Id);
    }

    [Fact]
    public async Task RunAsync_RecentlyFirstSeenGroup_CreatesNewExceptionFinding()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "NewExceptionDetectorTestApp");

        var group = new ExceptionGroup
        {
            ApplicationId = appId, Fingerprint = "fp-new", ExceptionType = "System.TimeoutException",
            MessageTemplate = "timed out", RepresentativeStackTrace = "at Foo.Bar()",
            FirstSeenAt = DateTime.UtcNow.AddMinutes(-1), LastSeenAt = DateTime.UtcNow, OccurrenceCount = 1
        };
        context.ExceptionGroups.Add(group);
        await context.SaveChangesAsync();

        context.Events.Add(new Event
        {
            ApplicationId = appId, EnvironmentId = envId, Timestamp = DateTime.UtcNow,
            Severity = 17, Message = "boom", ExceptionGroupId = group.Id
        });
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var detector = new NewExceptionDetector(context, writer);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.NewException);

        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.High, finding!.Severity);
        Assert.Equal(ConfidenceLevel.High, finding.ConfidenceLevel);
    }

    [Fact]
    public async Task RunAsync_OldExistingGroup_NoFindingCreated()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "NewExceptionDetectorOldGroupTestApp");

        var group = new ExceptionGroup
        {
            ApplicationId = appId, Fingerprint = "fp-old", ExceptionType = "System.Exception",
            MessageTemplate = "old", RepresentativeStackTrace = "at Foo.Bar()",
            FirstSeenAt = DateTime.UtcNow.AddDays(-10), LastSeenAt = DateTime.UtcNow, OccurrenceCount = 5
        };
        context.ExceptionGroups.Add(group);
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var detector = new NewExceptionDetector(context, writer);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var findingCount = await verifyContext.Findings.CountAsync(f => f.ApplicationId == appId);

        Assert.Equal(0, findingCount);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter NewExceptionDetectorTests`
Expected: FAIL — compile error, `NewExceptionDetector` doesn't exist.

- [ ] **Step 3: Implement `NewExceptionDetector`**

`src/LogsPlatform.Web/Services/Analysis/NewExceptionDetector.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Services.Analysis;

public class NewExceptionDetector
{
    private static readonly TimeSpan DetectionWindow = TimeSpan.FromMinutes(5);

    private readonly LogsPlatformDbContext _context;
    private readonly FindingWriter _writer;

    public NewExceptionDetector(LogsPlatformDbContext context, FindingWriter writer)
    {
        _context = context;
        _writer = writer;
    }

    public async Task RunAsync(int applicationId, int environmentId)
    {
        var windowStart = DateTime.UtcNow - DetectionWindow;

        var newGroups = await _context.ExceptionGroups.AsNoTracking()
            .Where(g => g.ApplicationId == applicationId && g.FirstSeenAt >= windowStart)
            .ToListAsync();

        foreach (var group in newGroups)
        {
            var environmentIds = await _context.Events.AsNoTracking()
                .Where(e => e.ExceptionGroupId == group.Id && e.EnvironmentId == environmentId)
                .Select(e => e.EnvironmentId)
                .Distinct()
                .ToListAsync();

            foreach (var envId in environmentIds)
            {
                var draft = new FindingDraft(
                    applicationId, envId, FindingType.NewException, AnalysisScopeType.ExceptionGroup, group.Id,
                    $"New exception: {group.ExceptionType}", FindingSeverity.High, ConfidenceLevel.High,
                    new[] { (DetectorStatementKind.Fact, $"This exception type ({group.ExceptionType}) has never been seen before. First occurrence at {group.FirstSeenAt:u}.") });

                await _writer.WriteAsync(draft);
            }
        }
    }
}
```

Note: this detector takes `LogsPlatformDbContext` directly rather than a repository interface, per the "Interfaces" note above. The `environmentId` parameter narrows which environment's events count as confirming a group is "new *here*" (matching the caller's per-`(Application, Environment)` iteration from Task 11), while the inner `environmentIds` query still exists to correctly split a group's Finding per-environment if it turns out to span more than one — consistent with the design doc's stated handling.

- [ ] **Step 4: Register in `Program.cs`**

Add after the `RateAnomalyDetector` registration:

```csharp
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.NewExceptionDetector>();
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter NewExceptionDetectorTests`
Expected: PASS — 2/2 tests.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Services/Analysis/NewExceptionDetector.cs src/LogsPlatform.Web/Program.cs tests/LogsPlatform.Tests/Web/NewExceptionDetectorTests.cs
git commit -m "Add NewExceptionDetector"
```

---

### Task 7: `CustomerOutlierDetector`

**Suggested model tier:** standard-to-high (peer-comparison statistics, the design's most subtle detector).

**Files:**
- Create: `src/LogsPlatform.Web/Services/Analysis/CustomerOutlierDetector.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Test: `tests/LogsPlatform.Tests/Web/CustomerOutlierDetectorTests.cs`

**Interfaces:**
- Consumes: `IMetricsRepository` (Task 2), `FindingWriter`/`FindingDraft` (Task 4).
- Produces (used by Task 11): `CustomerOutlierDetector` with `RunAsync(int applicationId, int environmentId)`.

- [ ] **Step 1: Write the failing tests**

`tests/LogsPlatform.Tests/Web/CustomerOutlierDetectorTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class CustomerOutlierDetectorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId, int OperationId)> SeedAppEnvOperationAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        var module = new AppModule { ApplicationId = app.Id, Name = "Billing" };
        context.AppEnvironments.Add(env);
        context.Modules.Add(module);
        await context.SaveChangesAsync();
        var screenService = new ScreenService { ModuleId = module.Id, Name = "Invoicing", Type = ScreenServiceType.Service };
        context.ScreenServices.Add(screenService);
        await context.SaveChangesAsync();
        var process = new ProcessNode { ScreenServiceId = screenService.Id, Name = "ChargeCard" };
        context.Processes.Add(process);
        await context.SaveChangesAsync();
        var operation = new Operation { ProcessId = process.Id, Name = "Authorize" };
        context.Operations.Add(operation);
        await context.SaveChangesAsync();
        return (app.Id, env.Id, operation.Id);
    }

    [Fact]
    public async Task RunAsync_OneCustomerFarAbovePeers_CreatesCustomerAnomalyFinding()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "CustomerOutlierSpikeTestApp");

        var customers = new List<Customer>();
        for (var i = 0; i < 6; i++)
        {
            var customer = new Customer { ApplicationId = appId, ExternalCustomerId = $"cust-{i}", Name = $"Customer {i}" };
            customers.Add(customer);
        }
        context.Customers.AddRange(customers);
        await context.SaveChangesAsync();

        var now = DateTime.UtcNow;
        // 5 peers with 2 events each; the 6th customer has 50.
        for (var i = 0; i < 5; i++)
        {
            context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customers[i].Id, Timestamp = now, Severity = 17, Message = $"peer-{i}-a" });
            context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customers[i].Id, Timestamp = now, Severity = 17, Message = $"peer-{i}-b" });
        }
        for (var i = 0; i < 50; i++)
        {
            context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customers[5].Id, Timestamp = now, Severity = 17, Message = $"outlier-{i}" });
        }
        await context.SaveChangesAsync();

        var metrics = new MetricsRepository(context);
        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var detector = new CustomerOutlierDetector(metrics, writer);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.CustomerAnomaly);

        Assert.NotNull(finding);
    }

    [Fact]
    public async Task RunAsync_FewerThanMinPeerCustomers_NoFindingCreated()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "CustomerOutlierFewPeersTestApp");

        var customers = new List<Customer>();
        for (var i = 0; i < 3; i++)
        {
            customers.Add(new Customer { ApplicationId = appId, ExternalCustomerId = $"cust-{i}", Name = $"Customer {i}" });
        }
        context.Customers.AddRange(customers);
        await context.SaveChangesAsync();

        var now = DateTime.UtcNow;
        context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customers[0].Id, Timestamp = now, Severity = 17, Message = "a" });
        context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customers[1].Id, Timestamp = now, Severity = 17, Message = "b" });
        context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customers[2].Id, Timestamp = now, Severity = 17, Message = "c" });
        await context.SaveChangesAsync();

        var metrics = new MetricsRepository(context);
        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var detector = new CustomerOutlierDetector(metrics, writer);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var findingCount = await verifyContext.Findings.CountAsync(f => f.ApplicationId == appId);

        Assert.Equal(0, findingCount);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter CustomerOutlierDetectorTests`
Expected: FAIL — compile error, `CustomerOutlierDetector` doesn't exist.

- [ ] **Step 3: Implement `CustomerOutlierDetector`**

`src/LogsPlatform.Web/Services/Analysis/CustomerOutlierDetector.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class CustomerOutlierDetector
{
    private const int MinPeerCustomers = 5;
    private const double CustomerOutlierThreshold = 3;
    private const double MinStdDevFloor = 0.5;
    private static readonly TimeSpan Window = TimeSpan.FromDays(1);

    private readonly IMetricsRepository _metrics;
    private readonly FindingWriter _writer;

    public CustomerOutlierDetector(IMetricsRepository metrics, FindingWriter writer)
    {
        _metrics = metrics;
        _writer = writer;
    }

    public async Task RunAsync(int applicationId, int environmentId)
    {
        var windowStart = DateTime.UtcNow - Window;

        var operationIds = await _metrics.GetActiveOperationIdsAsync(applicationId, environmentId);
        foreach (var operationId in operationIds)
        {
            var rates = await _metrics.GetCustomerRatesAsync(applicationId, environmentId, operationId, null, windowStart);
            await EvaluatePeersAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, rates);
        }

        var exceptionGroupIds = await _metrics.GetActiveExceptionGroupIdsAsync(applicationId, environmentId);
        foreach (var exceptionGroupId in exceptionGroupIds)
        {
            var rates = await _metrics.GetCustomerRatesAsync(applicationId, environmentId, null, exceptionGroupId, windowStart);
            await EvaluatePeersAsync(applicationId, environmentId, AnalysisScopeType.ExceptionGroup, exceptionGroupId, rates);
        }
    }

    private async Task EvaluatePeersAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, IReadOnlyDictionary<int, double> rates)
    {
        if (rates.Count < MinPeerCustomers)
        {
            return;
        }

        foreach (var (customerId, rate) in rates)
        {
            var peerRates = rates.Where(r => r.Key != customerId).Select(r => r.Value).ToList();
            var populationMean = peerRates.Average();
            var variance = peerRates.Count > 1
                ? peerRates.Sum(v => (v - populationMean) * (v - populationMean)) / peerRates.Count
                : 0;
            var populationStdDev = Math.Sqrt(variance);
            var stdDev = Math.Max(populationStdDev, MinStdDevFloor);
            var z = (rate - populationMean) / stdDev;

            if (Math.Abs(z) > CustomerOutlierThreshold)
            {
                var severity = Math.Abs(z) > 5 ? FindingSeverity.High : FindingSeverity.Medium;
                var fact = $"Customer {customerId} recorded a rate of {rate:F1} in the last 24 hours.";
                var observation = $"That is {Math.Abs(z):F1} standard deviations from its {peerRates.Count} peers (peer average: {populationMean:F1}±{populationStdDev:F1}).";

                var draft = new FindingDraft(
                    applicationId, environmentId, FindingType.CustomerAnomaly, scopeType, scopeId,
                    $"Customer {customerId}: unusual activity", severity, ConfidenceLevel.Medium,
                    new[] { (DetectorStatementKind.Fact, fact), (DetectorStatementKind.Observation, observation) });

                await _writer.WriteAsync(draft);
            }
        }
    }
}
```

- [ ] **Step 4: Register in `Program.cs`**

Add after the `NewExceptionDetector` registration:

```csharp
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.CustomerOutlierDetector>();
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter CustomerOutlierDetectorTests`
Expected: PASS — 2/2 tests.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Services/Analysis/CustomerOutlierDetector.cs src/LogsPlatform.Web/Program.cs tests/LogsPlatform.Tests/Web/CustomerOutlierDetectorTests.cs
git commit -m "Add CustomerOutlierDetector"
```

---

### Task 8: `DeploymentCorrelator` + `DownstreamFailureCorrelator`

**Suggested model tier:** standard.

**Files:**
- Modify: `src/LogsPlatform.Domain/Repositories/IDeploymentRepository.cs` (add one method)
- Modify: `src/LogsPlatform.Infrastructure/Repositories/DeploymentRepository.cs`
- Create: `src/LogsPlatform.Web/Services/Analysis/DeploymentCorrelator.cs`
- Create: `src/LogsPlatform.Web/Services/Analysis/DownstreamFailureCorrelator.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Test: `tests/LogsPlatform.Tests/Web/DeploymentCorrelatorTests.cs`
- Test: `tests/LogsPlatform.Tests/Web/DownstreamFailureCorrelatorTests.cs`

**Interfaces:**
- Consumes: `IFindingRepository` (Task 4, for reading a Finding's details and appending Hypothesis/Evidence directly — correlators bypass `FindingWriter` since they augment an *existing* Finding rather than creating/deduping one), `IDeploymentRepository` (existing + this task's addition).
- Produces (used by Task 11): `DeploymentCorrelator`/`DownstreamFailureCorrelator`, both with `RunAsync(Finding finding)`.

- [ ] **Step 1: Write the failing tests**

`tests/LogsPlatform.Tests/Web/DeploymentCorrelatorTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class DeploymentCorrelatorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId, int VersionId)> SeedAppEnvVersionAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();
        var version = new AppVersion { ApplicationId = app.Id, VersionNumber = "2.3.1", CreatedAt = DateTime.UtcNow };
        context.Versions.Add(version);
        await context.SaveChangesAsync();
        return (app.Id, env.Id, version.Id);
    }

    [Fact]
    public async Task RunAsync_DeploymentWithinWindow_AddsHypothesisAndEvidence()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, versionId) = await SeedAppEnvVersionAsync(context, "DeploymentCorrelatorTestApp");

        var detectedAt = DateTime.UtcNow;
        context.Deployments.Add(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = versionId, DeployedAt = detectedAt.AddMinutes(-13) });
        await context.SaveChangesAsync();

        var finding = new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = detectedAt, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        };
        context.Findings.Add(finding);
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        var deploymentRepository = new DeploymentRepository(context);
        var correlator = new DeploymentCorrelator(findingRepository, deploymentRepository);

        await correlator.RunAsync(finding);

        var details = await findingRepository.GetByIdAsync(finding.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
        Assert.Contains(details.Evidence, e => e.EvidenceType == EvidenceType.Deployment);
    }

    [Fact]
    public async Task RunAsync_NoDeploymentInWindow_NoHypothesisAdded()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, _) = await SeedAppEnvVersionAsync(context, "DeploymentCorrelatorNoMatchTestApp");

        var finding = new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        };
        context.Findings.Add(finding);
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        var deploymentRepository = new DeploymentRepository(context);
        var correlator = new DeploymentCorrelator(findingRepository, deploymentRepository);

        await correlator.RunAsync(finding);

        var details = await findingRepository.GetByIdAsync(finding.Id);
        Assert.Empty(details!.Statements);
    }
}
```

`tests/LogsPlatform.Tests/Web/DownstreamFailureCorrelatorTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class DownstreamFailureCorrelatorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> SeedAppEnvAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();
        return (app.Id, env.Id);
    }

    [Fact]
    public async Task RunAsync_LaterErrorOnDifferentOperationSameCorrelationId_AddsHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "DownstreamCorrelatorTestApp");

        var triggerTime = DateTime.UtcNow;
        var triggerEvent = new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = 1, CorrelationId = "order-1",
            Timestamp = triggerTime, Severity = 17, Message = "initial failure"
        };
        var downstreamEvent = new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = 2, CorrelationId = "order-1",
            Timestamp = triggerTime.AddSeconds(5), Severity = 17, Message = "downstream failure"
        };
        context.Events.AddRange(triggerEvent, downstreamEvent);
        await context.SaveChangesAsync();

        var finding = new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = triggerTime, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        };
        context.Findings.Add(finding);
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        var correlator = new DownstreamFailureCorrelator(findingRepository, context);

        await correlator.RunAsync(finding, triggerEvent.CorrelationId!, triggerEvent.OperationId!.Value, triggerTime);

        var details = await findingRepository.GetByIdAsync(finding.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
        Assert.Contains(details.Evidence, e => e.EvidenceType == EvidenceType.Event);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter "DeploymentCorrelatorTests|DownstreamFailureCorrelatorTests"`
Expected: FAIL — compile error, `DeploymentCorrelator`/`DownstreamFailureCorrelator` don't exist; `IDeploymentRepository` is missing the new method the correlator needs.

- [ ] **Step 3: Add the windowed lookup to `IDeploymentRepository`**

In `src/LogsPlatform.Domain/Repositories/IDeploymentRepository.cs`, add:

```csharp
    Task<IReadOnlyList<Deployment>> GetInWindowAsync(int applicationId, int environmentId, DateTime windowStart, DateTime windowEnd);
```

(full file, for context — add the line above inside the existing interface body):

```csharp
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IDeploymentRepository
{
    Task<Deployment?> GetByIdAsync(int id);
    Task<IReadOnlyList<Deployment>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<IReadOnlyList<Deployment>> GetInWindowAsync(int applicationId, int environmentId, DateTime windowStart, DateTime windowEnd);
    Task<Deployment> AddAsync(Deployment deployment);
    Task<Deployment> RenameAsync(int id, string? notes);
    Task DeactivateAsync(int id);
}
```

- [ ] **Step 4: Implement the new method in `DeploymentRepository`**

Add to `src/LogsPlatform.Infrastructure/Repositories/DeploymentRepository.cs` (alongside the existing methods):

```csharp
    public async Task<IReadOnlyList<Deployment>> GetInWindowAsync(int applicationId, int environmentId, DateTime windowStart, DateTime windowEnd) =>
        await _context.Deployments.AsNoTracking()
            .Where(d => d.ApplicationId == applicationId && d.EnvironmentId == environmentId
                && d.DeployedAt >= windowStart && d.DeployedAt <= windowEnd)
            .OrderByDescending(d => d.DeployedAt)
            .ToListAsync();
```

- [ ] **Step 5: Implement `DeploymentCorrelator`**

`src/LogsPlatform.Web/Services/Analysis/DeploymentCorrelator.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class DeploymentCorrelator
{
    private static readonly TimeSpan CorrelationWindow = TimeSpan.FromMinutes(60);

    private readonly IFindingRepository _findings;
    private readonly IDeploymentRepository _deployments;

    public DeploymentCorrelator(IFindingRepository findings, IDeploymentRepository deployments)
    {
        _findings = findings;
        _deployments = deployments;
    }

    public async Task RunAsync(Finding finding)
    {
        if (finding.Type is not (FindingType.ErrorSpike or FindingType.PerformanceDegradation or FindingType.NewException))
        {
            return;
        }

        var windowStart = finding.DetectedAt - CorrelationWindow;
        var deployments = await _deployments.GetInWindowAsync(finding.ApplicationId, finding.EnvironmentId, windowStart, finding.DetectedAt);
        var deployment = deployments.FirstOrDefault();
        if (deployment is null)
        {
            return;
        }

        var minutesBefore = (finding.DetectedAt - deployment.DeployedAt).TotalMinutes;
        var hypothesis = $"A deployment was installed at {deployment.DeployedAt:u}, {minutesBefore:F0} minutes before this anomaly started. There may be a connection, but it has not been confirmed.";

        await _findings.AddEvidenceAsync(finding.Id, EvidenceType.Deployment, deployment.Id, $"Deployment #{deployment.Id} at {deployment.DeployedAt:u}");
        await _findings.AddStatementAsync(finding.Id, DetectorStatementKind.Hypothesis, hypothesis);
    }
}
```

- [ ] **Step 6: Implement `DownstreamFailureCorrelator`**

`src/LogsPlatform.Web/Services/Analysis/DownstreamFailureCorrelator.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Services.Analysis;

public class DownstreamFailureCorrelator
{
    private const int ErrorSeverityFloor = 17; // matches SeverityLevels.ByName["Error"]

    private readonly IFindingRepository _findings;
    private readonly LogsPlatformDbContext _context;

    public DownstreamFailureCorrelator(IFindingRepository findings, LogsPlatformDbContext context)
    {
        _findings = findings;
        _context = context;
    }

    public async Task RunAsync(Finding finding, string correlationId, int triggeringOperationId, DateTime triggerTimestamp)
    {
        if (finding.Type is not (FindingType.NewException or FindingType.ErrorSpike))
        {
            return;
        }

        var relatedEvents = await _context.Events.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId && e.Timestamp > triggerTimestamp
                && e.OperationId != triggeringOperationId && e.Severity >= ErrorSeverityFloor)
            .ToListAsync();

        if (relatedEvents.Count == 0)
        {
            return;
        }

        foreach (var relatedEvent in relatedEvents)
        {
            await _findings.AddEvidenceAsync(finding.Id, EvidenceType.Event, relatedEvent.Id, $"Event #{relatedEvent.Id} at {relatedEvent.Timestamp:u}");
        }

        var operationIds = relatedEvents.Select(e => e.OperationId).Distinct().Count();
        var hypothesis = $"After this event, {relatedEvents.Count} additional error(s) were recorded in the same correlation chain, across {operationIds} other operation(s). This may be a downstream failure caused by this event, but it has not been confirmed.";

        await _findings.AddStatementAsync(finding.Id, DetectorStatementKind.Hypothesis, hypothesis);
    }
}
```

- [ ] **Step 7: Register in `Program.cs`**

Add after the `CustomerOutlierDetector` registration:

```csharp
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.DeploymentCorrelator>();
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.DownstreamFailureCorrelator>();
```

- [ ] **Step 8: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter "DeploymentCorrelatorTests|DownstreamFailureCorrelatorTests"`
Expected: PASS — 3/3 tests.

- [ ] **Step 9: Commit**

```bash
git add src/LogsPlatform.Domain/Repositories/IDeploymentRepository.cs src/LogsPlatform.Infrastructure/Repositories/DeploymentRepository.cs src/LogsPlatform.Web/Services/Analysis/DeploymentCorrelator.cs src/LogsPlatform.Web/Services/Analysis/DownstreamFailureCorrelator.cs src/LogsPlatform.Web/Program.cs tests/LogsPlatform.Tests/Web/DeploymentCorrelatorTests.cs tests/LogsPlatform.Tests/Web/DownstreamFailureCorrelatorTests.cs
git commit -m "Add DeploymentCorrelator and DownstreamFailureCorrelator"
```

---

### Task 9: `AnalysisEngineTickRunner` + `AnalysisEngineBackgroundService`

**Suggested model tier:** standard-to-high (this project's first `BackgroundService`; both the concurrent-tick guard and the Scoped-vs-Singleton DI lifetime split below are real concurrency/architecture correctness requirements, not boilerplate).

**A real DI lifetime problem this task must not reproduce:** `AddHostedService<T>()` registers `T` as a **singleton** — it's constructed once and lives for the app's entire lifetime. But `IApplicationRepository`, `IBaselineRepository`, `IFindingRepository`, and every other repository in this project (including `LogsPlatformDbContext` itself, via `AddDbContext`) are registered `Scoped`. A singleton cannot directly constructor-inject a scoped service — ASP.NET Core's DI container throws `InvalidOperationException: Cannot consume scoped service 'X' from singleton 'Y'` the moment it tries to construct it, which would crash the whole app at startup, not just the Analysis Engine. This task splits the work into two classes specifically to avoid that: `AnalysisEngineTickRunner` (registered `Scoped`, plain constructor injection of the scoped dependencies, holds all the actual orchestration logic and is what gets unit-tested directly) and `AnalysisEngineBackgroundService` (the actual `BackgroundService`, singleton, injects only `IServiceScopeFactory` + `ILogger` — both safe for a singleton to hold — and creates a fresh DI scope per tick to resolve `AnalysisEngineTickRunner` from).

**Files:**
- Create: `src/LogsPlatform.Web/Services/Analysis/AnalysisEngineTickRunner.cs`
- Create: `src/LogsPlatform.Web/Services/Analysis/AnalysisEngineBackgroundService.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Test: `tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs`
- Test: `tests/LogsPlatform.Tests/Web/AnalysisEngineBackgroundServiceTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 3, 5, 6, 7, 8 (`BaselineCalculator`, `RateAnomalyDetector`, `NewExceptionDetector`, `CustomerOutlierDetector`, `DeploymentCorrelator`), `IBaselineRepository.HasUpdatedTodayAsync` (Task 3), `IApplicationRepository`/`IAppEnvironmentRepository` (existing, for iterating every `(Application, Environment)` pair), `IFindingRepository` (Task 4, to fetch this tick's newly-created Findings so the correlator can run over them).
- Produces: `AnalysisEngineTickRunner` (Scoped) with `RunOneTickAsync()` — the fully-testable orchestration logic, no DI-container concerns. `AnalysisEngineBackgroundService : BackgroundService` (registered via `AddHostedService`) with `TryRunOneTickAsync()` — the process-wide-guarded, scope-owning entry point the timer loop and tests both use.

`AnalysisEngineTickRunnerTests.cs` uses plain constructor injection exactly like every other test in this plan (real repositories built on one shared `DbContext`, no DI container needed) — it's the class that actually contains the logic worth testing in detail. `AnalysisEngineBackgroundServiceTests.cs` only needs to prove the scope-per-tick wiring and the concurrency guard work, so it builds a small real `ServiceCollection` (not a mock) wired to the same test database.

**A known, deliberate gap this task does not resolve:** `DownstreamFailureCorrelator` (Task 8) is fully implemented and independently tested, but is **not** called from `AnalysisEngineTickRunner` below. Its `RunAsync` signature needs `correlationId`/`triggeringOperationId`/`triggerTimestamp` — context that lives inside whichever detector found the anomaly, not on the `Finding` row fetched back from the database afterward. Wiring it in cleanly needs either extending `Finding`/`FindingDraft` to carry the triggering event's identity through, or having detectors call it inline before handing off to `FindingWriter` — a real design decision, not a mechanical gap, and out of scope for an already-large task. `DeploymentCorrelator` **is** fully wired, since it only needs `Finding.ApplicationId`/`EnvironmentId`/`DetectedAt`, already on the row. Resolve `DownstreamFailureCorrelator`'s wiring in M4b or a small dedicated follow-up — not silently.

- [ ] **Step 1: Write the failing tests**

`tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AnalysisEngineTickRunnerTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> SeedAppEnvAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();
        return (app.Id, env.Id);
    }

    private static AnalysisEngineTickRunner BuildRunner(LogsPlatformDbContext context)
    {
        var applicationRepository = new ApplicationRepository(context);
        var environmentRepository = new AppEnvironmentRepository(context);
        var metricsRepository = new MetricsRepository(context);
        var baselineRepository = new BaselineRepository(context);
        var findingRepository = new FindingRepository(context);
        var deploymentRepository = new DeploymentRepository(context);
        var writer = new FindingWriter(findingRepository);
        var baselineCalculator = new BaselineCalculator(metricsRepository, baselineRepository);
        var rateAnomalyDetector = new RateAnomalyDetector(metricsRepository, baselineRepository, writer);
        var newExceptionDetector = new NewExceptionDetector(context, writer);
        var customerOutlierDetector = new CustomerOutlierDetector(metricsRepository, writer);
        var deploymentCorrelator = new DeploymentCorrelator(findingRepository, deploymentRepository);

        return new AnalysisEngineTickRunner(
            applicationRepository, environmentRepository, baselineRepository, findingRepository,
            baselineCalculator, rateAnomalyDetector, newExceptionDetector, customerOutlierDetector, deploymentCorrelator);
    }

    [Fact]
    public async Task RunOneTickAsync_NewApplicationWithNoData_CompletesWithoutThrowing()
    {
        using var context = TestDatabase.CreateContext();
        await SeedAppEnvAsync(context, "TickRunnerEmptyTestApp");

        var runner = BuildRunner(context);

        // No active Operations/ExceptionGroups exist yet, so no Baseline rows or Findings are
        // expected — the real assertion is that a tick over a completely empty (Application,
        // Environment) pair completes cleanly rather than throwing.
        var exception = await Record.ExceptionAsync(() => runner.RunOneTickAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task RunOneTickAsync_ErrorSpikeWithRecentDeployment_CorrelatorAttachesHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "TickRunnerCorrelationTestApp");

        var module = new AppModule { ApplicationId = appId, Name = "Billing" };
        context.Modules.Add(module);
        await context.SaveChangesAsync();
        var screenService = new ScreenService { ModuleId = module.Id, Name = "Invoicing", Type = ScreenServiceType.Service };
        context.ScreenServices.Add(screenService);
        await context.SaveChangesAsync();
        var process = new ProcessNode { ScreenServiceId = screenService.Id, Name = "ChargeCard" };
        context.Processes.Add(process);
        await context.SaveChangesAsync();
        var operation = new Operation { ProcessId = process.Id, Name = "Authorize" };
        context.Operations.Add(operation);
        await context.SaveChangesAsync();
        var version = new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0", CreatedAt = DateTime.UtcNow };
        context.Versions.Add(version);
        await context.SaveChangesAsync();

        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        context.Baselines.Add(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = operation.Id,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = (byte)currentHourStart.Hour,
            MeanValue = 5, StdDevValue = 1, SampleCount = 20, LastUpdatedAt = DateTime.UtcNow
        });
        context.Deployments.Add(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = version.Id, DeployedAt = DateTime.UtcNow.AddMinutes(-10) });
        for (var i = 0; i < 40; i++)
        {
            context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = operation.Id, Timestamp = currentHourStart.AddMinutes(i % 59), Severity = 17, Message = $"spike-{i}" });
        }
        await context.SaveChangesAsync();

        var runner = BuildRunner(context);
        await runner.RunOneTickAsync();

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.ErrorSpike);
        Assert.NotNull(finding);

        var findingRepository = new FindingRepository(verifyContext);
        var details = await findingRepository.GetByIdAsync(finding!.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
    }
}
```

`tests/LogsPlatform.Tests/Web/AnalysisEngineBackgroundServiceTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AnalysisEngineBackgroundServiceTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> SeedAppEnvAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();
        return (app.Id, env.Id);
    }

    private static AnalysisEngineBackgroundService BuildService(LogsPlatformDbContext context)
    {
        // A small real DI container (not a mock) proving the actual scope-per-tick wiring works —
        // every registered type is the real implementation Program.cs uses. Registered Singleton
        // here (unlike Program.cs's real Scoped AddDbContext) so every resolution within the test's
        // one DI scope sees the same in-flight data as the context the test seeded directly.
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton<IApplicationRepository, ApplicationRepository>();
        services.AddSingleton<IAppEnvironmentRepository, AppEnvironmentRepository>();
        services.AddSingleton<IMetricsRepository, MetricsRepository>();
        services.AddSingleton<IBaselineRepository, BaselineRepository>();
        services.AddSingleton<IFindingRepository, FindingRepository>();
        services.AddSingleton<IDeploymentRepository, DeploymentRepository>();
        services.AddSingleton<FindingWriter>();
        services.AddSingleton<BaselineCalculator>();
        services.AddSingleton<RateAnomalyDetector>();
        services.AddSingleton<NewExceptionDetector>();
        services.AddSingleton<CustomerOutlierDetector>();
        services.AddSingleton<DeploymentCorrelator>();
        services.AddScoped<AnalysisEngineTickRunner>();

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new AnalysisEngineBackgroundService(scopeFactory, NullLogger<AnalysisEngineBackgroundService>.Instance);
    }

    [Fact]
    public async Task TryRunOneTickAsync_CalledWhileAlreadyRunning_SecondCallIsSkipped()
    {
        using var context = TestDatabase.CreateContext();
        await SeedAppEnvAsync(context, "BackgroundServiceConcurrentTickTestApp");

        var service = BuildService(context);

        // Both calls go through the guarded entry point. The guard's Interlocked.CompareExchange
        // runs synchronously before the scope-creation/tick-running code's first real await (a DB
        // call), so by the time this line returns control here, _isRunning is already set — the
        // second call's own CompareExchange deterministically sees it and skips.
        var firstTick = service.TryRunOneTickAsync();
        var secondTickRan = await service.TryRunOneTickAsync();
        var firstTickRan = await firstTick;

        Assert.True(firstTickRan);
        Assert.False(secondTickRan);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter "AnalysisEngineTickRunnerTests|AnalysisEngineBackgroundServiceTests"`
Expected: FAIL — compile error, `AnalysisEngineTickRunner`/`AnalysisEngineBackgroundService` don't exist; `IFindingRepository` is missing the method the runner needs.

- [ ] **Step 3: Add `GetDetectedSinceAsync` to `IFindingRepository`**

In `src/LogsPlatform.Domain/Repositories/IFindingRepository.cs`, add:

```csharp
    Task<IReadOnlyList<Finding>> GetDetectedSinceAsync(int applicationId, int environmentId, DateTime since);
```

In `src/LogsPlatform.Infrastructure/Repositories/FindingRepository.cs`, add:

```csharp
    public async Task<IReadOnlyList<Finding>> GetDetectedSinceAsync(int applicationId, int environmentId, DateTime since) =>
        await _context.Findings.AsNoTracking()
            .Where(f => f.ApplicationId == applicationId && f.EnvironmentId == environmentId && f.DetectedAt >= since)
            .ToListAsync();
```

- [ ] **Step 4: Implement `AnalysisEngineTickRunner`**

`src/LogsPlatform.Web/Services/Analysis/AnalysisEngineTickRunner.cs`:

```csharp
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class AnalysisEngineTickRunner
{
    private static readonly TimeSpan TickLookback = TimeSpan.FromMinutes(5);

    private readonly IApplicationRepository _applications;
    private readonly IAppEnvironmentRepository _environments;
    private readonly IBaselineRepository _baselines;
    private readonly IFindingRepository _findings;
    private readonly BaselineCalculator _baselineCalculator;
    private readonly RateAnomalyDetector _rateAnomalyDetector;
    private readonly NewExceptionDetector _newExceptionDetector;
    private readonly CustomerOutlierDetector _customerOutlierDetector;
    private readonly DeploymentCorrelator _deploymentCorrelator;

    public AnalysisEngineTickRunner(
        IApplicationRepository applications,
        IAppEnvironmentRepository environments,
        IBaselineRepository baselines,
        IFindingRepository findings,
        BaselineCalculator baselineCalculator,
        RateAnomalyDetector rateAnomalyDetector,
        NewExceptionDetector newExceptionDetector,
        CustomerOutlierDetector customerOutlierDetector,
        DeploymentCorrelator deploymentCorrelator)
    {
        _applications = applications;
        _environments = environments;
        _baselines = baselines;
        _findings = findings;
        _baselineCalculator = baselineCalculator;
        _rateAnomalyDetector = rateAnomalyDetector;
        _newExceptionDetector = newExceptionDetector;
        _customerOutlierDetector = customerOutlierDetector;
        _deploymentCorrelator = deploymentCorrelator;
    }

    public async Task RunOneTickAsync()
    {
        var applications = await _applications.GetAllAsync();
        foreach (var application in applications)
        {
            var environments = await _environments.GetByApplicationIdAsync(application.Id);
            foreach (var environment in environments)
            {
                await RunForApplicationEnvironmentAsync(application.Id, environment.Id);
            }
        }
    }

    private async Task RunForApplicationEnvironmentAsync(int applicationId, int environmentId)
    {
        if (!await _baselines.HasUpdatedTodayAsync(applicationId, environmentId))
        {
            await _baselineCalculator.RunAsync(applicationId, environmentId);
        }

        var tickStart = DateTime.UtcNow;

        await _rateAnomalyDetector.RunAsync(applicationId, environmentId);
        await _newExceptionDetector.RunAsync(applicationId, environmentId);
        await _customerOutlierDetector.RunAsync(applicationId, environmentId);

        var newFindings = await _findings.GetDetectedSinceAsync(applicationId, environmentId, tickStart - TickLookback);
        foreach (var finding in newFindings)
        {
            await _deploymentCorrelator.RunAsync(finding);
        }
    }
}
```

- [ ] **Step 5: Implement `AnalysisEngineBackgroundService`**

`src/LogsPlatform.Web/Services/Analysis/AnalysisEngineBackgroundService.cs`:

```csharp
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
```

- [ ] **Step 6: Register in `Program.cs`**

Add after the `DeploymentCorrelator`/`DownstreamFailureCorrelator` registrations from Task 8:

```csharp
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.AnalysisEngineTickRunner>();
builder.Services.AddHostedService<LogsPlatform.Web.Services.Analysis.AnalysisEngineBackgroundService>();
```

- [ ] **Step 7: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter "AnalysisEngineTickRunnerTests|AnalysisEngineBackgroundServiceTests"`
Expected: PASS — 3/3 tests.

- [ ] **Step 8: Run the full test suite**

Run: `dotnet test`
Expected: all tests pass (241 pre-existing + this plan's new tests).

- [ ] **Step 9: Commit**

```bash
git add src/LogsPlatform.Web/Services/Analysis/AnalysisEngineTickRunner.cs src/LogsPlatform.Web/Services/Analysis/AnalysisEngineBackgroundService.cs src/LogsPlatform.Domain/Repositories/IFindingRepository.cs src/LogsPlatform.Infrastructure/Repositories/FindingRepository.cs src/LogsPlatform.Web/Program.cs tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs tests/LogsPlatform.Tests/Web/AnalysisEngineBackgroundServiceTests.cs
git commit -m "Add AnalysisEngineTickRunner and AnalysisEngineBackgroundService (Phase 1 + Phase 2 orchestration, concurrent-tick guard, scope-per-tick DI)"
```

---

## Self-Review Notes

**Spec coverage:** Baseline calculation (Task 3), all 3 detectors (Tasks 5-7), both correlators (Task 8), dedup (Task 4), the orchestrating `BackgroundService` (Task 9) are all covered. The corrected `Finding.Type` enum, `EnvironmentId` scoping, the independently-derived Severity algorithm, and the compile-time-enforced `DetectorStatementKind` are all implemented exactly as the design doc specifies.

**A real bug caught and fixed during self-review, not present in the original draft:** the first version of Task 9 gave `AnalysisEngineBackgroundService` (registered via `AddHostedService`, which ASP.NET Core always treats as a singleton) direct constructor-injected dependencies on `IApplicationRepository`, `IBaselineRepository`, `IFindingRepository`, and every detector/correlator — all of which are `Scoped` (as is `LogsPlatformDbContext` itself, via `AddDbContext`). A singleton cannot directly consume a scoped service; the DI container throws `InvalidOperationException: Cannot consume scoped service from singleton` the moment it tries to construct the hosted service, which would crash the **entire application** at startup, not just the Analysis Engine — this is exactly the kind of defect that only surfaces the moment someone actually runs the app, not from reading the code in isolation. Fixed by splitting into two classes: `AnalysisEngineTickRunner` (Scoped, holds all the real orchestration logic, directly and simply testable with plain constructor injection) and the actual `AnalysisEngineBackgroundService` (singleton, injects only `IServiceScopeFactory`+`ILogger` — both singleton-safe — and creates a fresh DI scope per tick to resolve the runner from). The concurrency guard (`_isRunning`) correctly lives on the singleton, since it must be process-wide, not per-scope.

**Known, deliberate gap — not silently dropped:** `DownstreamFailureCorrelator` is fully implemented and independently tested (Task 8) but **not called from `AnalysisEngineTickRunner`** (Task 9) — its signature needs trigger-event context (`correlationId`, `triggeringOperationId`, `triggerTimestamp`) that isn't available on a `Finding` row fetched back from the database after the fact. Threading this through cleanly (either by widening `Finding`/`FindingDraft` to carry the triggering event's identity, or by having detectors call the correlator inline before handing off to `FindingWriter`) is a real design decision, not a mechanical fix, and is deferred to M4b or a small dedicated follow-up. `DeploymentCorrelator` **is** fully wired and running, since it only needs `Finding.ApplicationId`/`EnvironmentId`/`DetectedAt`, all already on the row.

**Placeholder scan:** No TBD/TODO markers. Both open items above are explicitly named and reasoned through, not vague placeholders.

**Type consistency:** `FindingDraft`, `DetectorStatementKind` are used identically across Tasks 5-9 — verified by re-reading each call site. `IMetricsRepository`'s 6 methods (Task 2) are consumed with matching signatures by `BaselineCalculator` (Task 3), `RateAnomalyDetector` (Task 5), `CustomerOutlierDetector` (Task 7). `AnalysisEngineTickRunner`'s 9-parameter constructor matches its call site in both `AnalysisEngineTickRunnerTests.BuildRunner` (all-positional) and `Program.cs`'s DI registrations (resolved by type, order-independent) exactly. All record/constructor calls in test files use fully-named or fully-positional arguments — checked for the illegal mixed named/positional pattern that M3's self-review caught (none found in this plan; every multi-argument construction here is either all-positional in declared order or, where named arguments appear, no positional argument follows them).

**FK cascade behavior:** `Baseline`/`Finding` both use `ApplicationId=Cascade, EnvironmentId=Restrict`, mirroring `Deployment`'s already-proven-correct precedent exactly (same shape: two FKs both transitively rooted at `Application`). `FindingStatement`/`Evidence` each have exactly one FK (`FindingId=Cascade`), no competing path exists, so no ambiguity.

**DI lifetime audit (prompted by the singleton/scoped bug above — checked every other new registration for the same class of mistake):** `MetricsRepository`, `BaselineRepository`, `FindingRepository` (Tasks 2-4) are all registered `Scoped` and only ever consumed by other `Scoped` registrations (`BaselineCalculator`, `RateAnomalyDetector`, etc., Tasks 3, 5-8) or resolved from within a manually-created scope (Task 9) — no other singleton in this plan holds a direct reference to any of them.
