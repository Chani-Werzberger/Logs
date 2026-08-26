# Advanced Correlators Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add three new Hypothesis-producing correlators to the Analysis Engine — Upstream-Cause, Concurrent-Finding, Recurrence — extending V1's 2 existing correlators (Deployment, Downstream Failure) without touching the "never auto-write Conclusion" principle.

**Architecture:** `UpstreamCauseCorrelator` mirrors the existing `DownstreamFailureCorrelator` exactly (same constructor shape, same `RunAsync` signature, opposite time direction) and is wired the same way — injected into `RateAnomalyDetector`/`NewExceptionDetector`, called right alongside the existing downstream call. `ConcurrentFindingCorrelator` and `RecurrenceCorrelator` mirror `DeploymentCorrelator` (take just a `Finding`) and are wired into `AnalysisEngineTickRunner`'s existing per-new-finding loop.

**Tech Stack:** .NET 10 / EF Core 10 / SQL Server. No new packages.

## Global Constraints

- **Design doc:** `docs/superpowers/specs/2026-08-26-analysis-correlators-design.md` — read it before starting.
- **No new Operation dependency-graph model.** Upstream-Cause uses the exact same chronological-`CorrelationId`-chain approach as the existing Downstream correlator — confirmed, non-default-recommendation-but-explicitly-approved decision.
- **Concurrent-Finding scope (exact, approved, NOT the recommended default):** same `ApplicationId`, **any** `EnvironmentId` (not scoped to one environment), **no time window** (every currently-open Finding counts, regardless of when it was detected), threshold = **1 or more** other open Findings.
- **Recurrence scope (exact, approved):** matches only the **single most recent** `Resolved`/`Dismissed` Finding with the same `(ApplicationId, EnvironmentId, ScopeType, ScopeId, Type)` — the same dedup key `FindingWriter`/`FindOpenAsync` already use. Not all prior occurrences, not a fuzzier match.
- **Every correlator's Hypothesis statement uses hedged, unconfirmed language** ("may be", "has not been confirmed") — matches the two existing correlators' phrasing exactly; never assert causation.
- **`DetectorStatementKind.Hypothesis`** (not `FindingStatementKind`) is the enum value passed to `AddStatementAsync` — `FindingRepository.AddStatementAsync` casts it internally to the broader `FindingStatementKind` (which also has `Conclusion`, settable only via `PromoteToConclusionAsync`). Never construct a statement with `FindingStatementKind.Conclusion` directly from a correlator.
- **Changing `RateAnomalyDetector`'s and `NewExceptionDetector`'s constructors is a breaking change for every test file that constructs them directly** (no DI container in these unit tests). Every task that adds a constructor parameter must update every direct-construction call site in the same task, or later tasks won't compile. The exact call sites are enumerated per-task below.
- **Test convention:** this project's Analysis tests always use `TestDatabase.CreateContext()` directly (not `IDbContextFactory`) since `FindingRepository`, `RateAnomalyDetector`, `NewExceptionDetector`, `AnalysisEngineTickRunner` and all correlators take a plain `LogsPlatformDbContext` in their constructors, never a factory. Match this exactly — do not introduce `IDbContextFactory` usage here.
- **Frequent commits:** one commit per task.

---

## Task 1: `EvidenceType.Finding` + two new `IFindingRepository` query methods

**Files:**
- Modify: `src/LogsPlatform.Domain/Entities/Evidence.cs`
- Modify: `src/LogsPlatform.Domain/Repositories/IFindingRepository.cs`
- Modify: `src/LogsPlatform.Infrastructure/Repositories/FindingRepository.cs`
- Modify: `tests/LogsPlatform.Tests/Infrastructure/FindingRepositoryTests.cs`

**Interfaces:**
- Produces: `EvidenceType.Finding` (new enum value). `IFindingRepository.GetOtherOpenFindingsForApplicationAsync(int applicationId, long excludeFindingId) : Task<IReadOnlyList<Finding>>`. `IFindingRepository.FindMostRecentClosedAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type, long excludeFindingId) : Task<Finding?>` — both consumed by Task 3.

- [ ] **Step 1: Add the new EvidenceType value**

In `src/LogsPlatform.Domain/Entities/Evidence.cs`, change:

```csharp
public enum EvidenceType { Event, ExceptionGroup, Deployment, Baseline, Operation }
```

to:

```csharp
public enum EvidenceType { Event, ExceptionGroup, Deployment, Baseline, Operation, Finding }
```

- [ ] **Step 2: Write the failing repository tests**

Add these two test methods to `tests/LogsPlatform.Tests/Infrastructure/FindingRepositoryTests.cs`, inside the existing `FindingRepositoryTests` class (after the last existing test method, before the closing `}`):

```csharp
    [Fact]
    public async Task GetOtherOpenFindingsForApplicationAsync_OtherOpenFindingExists_ReturnsIt()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoConcurrentTestApp");
        var repository = new FindingRepository(context);

        var thisFinding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "this one",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });
        var otherFinding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.PerformanceDegradation,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 2, Title = "other one",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.Medium, ConfidenceLevel = ConfidenceLevel.Medium, Status = FindingStatus.Acknowledged
        });

        var others = await repository.GetOtherOpenFindingsForApplicationAsync(appId, thisFinding.Id);

        Assert.Single(others);
        Assert.Equal(otherFinding.Id, others[0].Id);
    }

    [Fact]
    public async Task GetOtherOpenFindingsForApplicationAsync_OnlyResolvedFindingsExist_ReturnsEmpty()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoConcurrentResolvedTestApp");
        var repository = new FindingRepository(context);

        var thisFinding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "this one",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });
        await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.PerformanceDegradation,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 2, Title = "resolved one",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.Medium, ConfidenceLevel = ConfidenceLevel.Medium, Status = FindingStatus.Resolved
        });

        var others = await repository.GetOtherOpenFindingsForApplicationAsync(appId, thisFinding.Id);

        Assert.Empty(others);
    }

    [Fact]
    public async Task FindMostRecentClosedAsync_MatchingResolvedFindingExists_ReturnsIt()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoRecurrenceTestApp");
        var repository = new FindingRepository(context);

        var older = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "older resolved",
            DetectedAt = DateTime.UtcNow.AddDays(-2), Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.Resolved
        });
        var newer = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "newer resolved",
            DetectedAt = DateTime.UtcNow.AddDays(-1), Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.Dismissed
        });
        var current = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "current",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        var found = await repository.FindMostRecentClosedAsync(appId, envId, AnalysisScopeType.Operation, 1, FindingType.ErrorSpike, current.Id);

        Assert.NotNull(found);
        Assert.Equal(newer.Id, found!.Id);
        _ = older; // only used to prove "most recent" ordering, not the older one
    }

    [Fact]
    public async Task FindMostRecentClosedAsync_NoClosedMatch_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoRecurrenceNoneTestApp");
        var repository = new FindingRepository(context);

        var current = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "current",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        var found = await repository.FindMostRecentClosedAsync(appId, envId, AnalysisScopeType.Operation, 1, FindingType.ErrorSpike, current.Id);

        Assert.Null(found);
    }
```

- [ ] **Step 2b: Run the tests to verify they fail to compile**

Run: `dotnet build`
Expected: build errors — `GetOtherOpenFindingsForApplicationAsync`/`FindMostRecentClosedAsync` don't exist yet on `IFindingRepository`/`FindingRepository`.

- [ ] **Step 3: Add the two methods to the interface**

In `src/LogsPlatform.Domain/Repositories/IFindingRepository.cs`, add these two lines inside the `IFindingRepository` interface, after `PromoteToConclusionAsync`:

```csharp
    Task<IReadOnlyList<Finding>> GetOtherOpenFindingsForApplicationAsync(int applicationId, long excludeFindingId);
    Task<Finding?> FindMostRecentClosedAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type, long excludeFindingId);
```

- [ ] **Step 4: Implement them in FindingRepository**

In `src/LogsPlatform.Infrastructure/Repositories/FindingRepository.cs`, add these two methods, after `PromoteToConclusionAsync` and before the closing `}` of the class:

```csharp
    public async Task<IReadOnlyList<Finding>> GetOtherOpenFindingsForApplicationAsync(int applicationId, long excludeFindingId) =>
        await _context.Findings.AsNoTracking()
            .Where(f => f.ApplicationId == applicationId && f.Id != excludeFindingId &&
                (f.Status == FindingStatus.New || f.Status == FindingStatus.Acknowledged))
            .ToListAsync();

    public async Task<Finding?> FindMostRecentClosedAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type, long excludeFindingId) =>
        await _context.Findings.AsNoTracking()
            .Where(f => f.ApplicationId == applicationId && f.EnvironmentId == environmentId &&
                f.ScopeType == scopeType && f.ScopeId == scopeId && f.Type == type && f.Id != excludeFindingId &&
                (f.Status == FindingStatus.Resolved || f.Status == FindingStatus.Dismissed))
            .OrderByDescending(f => f.DetectedAt)
            .FirstOrDefaultAsync();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~FindingRepositoryTests"`
Expected: all tests pass, including the 4 new ones (total should be the pre-existing 8 + 4 = 12).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Domain/Entities/Evidence.cs \
        src/LogsPlatform.Domain/Repositories/IFindingRepository.cs \
        src/LogsPlatform.Infrastructure/Repositories/FindingRepository.cs \
        tests/LogsPlatform.Tests/Infrastructure/FindingRepositoryTests.cs
git commit -m "feat: add Finding evidence type and two new Finding query methods"
```

---

## Task 2: UpstreamCauseCorrelator

**Files:**
- Create: `src/LogsPlatform.Web/Services/Analysis/UpstreamCauseCorrelator.cs`
- Modify: `src/LogsPlatform.Web/Services/Analysis/RateAnomalyDetector.cs`
- Modify: `src/LogsPlatform.Web/Services/Analysis/NewExceptionDetector.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Create: `tests/LogsPlatform.Tests/Web/UpstreamCauseCorrelatorTests.cs`
- Modify: `tests/LogsPlatform.Tests/Web/RateAnomalyDetectorTests.cs`
- Modify: `tests/LogsPlatform.Tests/Web/NewExceptionDetectorTests.cs`
- Modify: `tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs`

**Interfaces:**
- Consumes: `IFindingRepository.AddEvidenceAsync`/`AddStatementAsync` (existing).
- Produces: `UpstreamCauseCorrelator.RunAsync(Finding finding, string correlationId, int triggeringOperationId, DateTime triggerTimestamp) : Task` — not consumed by any later task, but its constructor-parameter addition to `RateAnomalyDetector`/`NewExceptionDetector` IS consumed by Task 3 (which touches `AnalysisEngineTickRunnerTests.cs`'s `BuildRunner` again).

- [ ] **Step 1: Write the failing unit test**

Create `tests/LogsPlatform.Tests/Web/UpstreamCauseCorrelatorTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class UpstreamCauseCorrelatorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId, int OperationId1, int OperationId2)> SeedAppEnvAsync(LogsPlatformDbContext context, string appName)
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
        var operation1 = new Operation { ProcessId = process.Id, Name = "Authorize" };
        var operation2 = new Operation { ProcessId = process.Id, Name = "ValidateInput" };
        context.Operations.AddRange(operation1, operation2);
        await context.SaveChangesAsync();
        return (app.Id, env.Id, operation1.Id, operation2.Id);
    }

    [Fact]
    public async Task RunAsync_EarlierErrorOnDifferentOperationSameCorrelationId_AddsHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, operationId1, operationId2) = await SeedAppEnvAsync(context, "UpstreamCorrelatorTestApp");

        var triggerTime = DateTime.UtcNow;
        var upstreamEvent = new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = operationId2, CorrelationId = "order-1",
            Timestamp = triggerTime.AddSeconds(-5), Severity = 17, Message = "earlier failure"
        };
        var triggerEvent = new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = operationId1, CorrelationId = "order-1",
            Timestamp = triggerTime, Severity = 17, Message = "the trigger"
        };
        context.Events.AddRange(upstreamEvent, triggerEvent);
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
        var correlator = new UpstreamCauseCorrelator(findingRepository, context);

        await correlator.RunAsync(finding, triggerEvent.CorrelationId!, triggerEvent.OperationId!.Value, triggerTime);

        var details = await findingRepository.GetByIdAsync(finding.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
        Assert.Contains(details.Evidence, e => e.EvidenceType == EvidenceType.Event);
    }

    [Fact]
    public async Task RunAsync_NoEarlierEvents_DoesNotAddHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, operationId1, _) = await SeedAppEnvAsync(context, "UpstreamCorrelatorNoneTestApp");

        var triggerTime = DateTime.UtcNow;
        var triggerEvent = new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = operationId1, CorrelationId = "order-2",
            Timestamp = triggerTime, Severity = 17, Message = "the trigger"
        };
        context.Events.Add(triggerEvent);
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
        var correlator = new UpstreamCauseCorrelator(findingRepository, context);

        await correlator.RunAsync(finding, triggerEvent.CorrelationId!, triggerEvent.OperationId!.Value, triggerTime);

        var details = await findingRepository.GetByIdAsync(finding.Id);
        Assert.Empty(details!.Statements);
        Assert.Empty(details.Evidence);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet build`
Expected: build error — `UpstreamCauseCorrelator` does not exist yet.

- [ ] **Step 3: Implement UpstreamCauseCorrelator**

Create `src/LogsPlatform.Web/Services/Analysis/UpstreamCauseCorrelator.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Services.Analysis;

public class UpstreamCauseCorrelator
{
    private const int ErrorSeverityFloor = 17; // matches SeverityLevels.ByName["Error"]

    private readonly IFindingRepository _findings;
    private readonly LogsPlatformDbContext _context;

    public UpstreamCauseCorrelator(IFindingRepository findings, LogsPlatformDbContext context)
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
            .Where(e => e.CorrelationId == correlationId && e.Timestamp < triggerTimestamp
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
        var hypothesis = $"Before this event, {relatedEvents.Count} earlier error(s) were recorded in the same correlation chain, across {operationIds} other operation(s). This may be an upstream cause of this event, but it has not been confirmed.";

        await _findings.AddStatementAsync(finding.Id, DetectorStatementKind.Hypothesis, hypothesis);
    }
}
```

- [ ] **Step 4: Run the new test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~UpstreamCauseCorrelatorTests"`
Expected: 2/2 passing.

- [ ] **Step 5: Wire into RateAnomalyDetector**

In `src/LogsPlatform.Web/Services/Analysis/RateAnomalyDetector.cs`, change the field/constructor block from:

```csharp
    private readonly IMetricsRepository _metrics;
    private readonly IBaselineRepository _baselines;
    private readonly FindingWriter _writer;
    private readonly DownstreamFailureCorrelator _downstreamCorrelator;
    private readonly LogsPlatformDbContext _context;

    public RateAnomalyDetector(IMetricsRepository metrics, IBaselineRepository baselines, FindingWriter writer, DownstreamFailureCorrelator downstreamCorrelator, LogsPlatformDbContext context)
    {
        _metrics = metrics;
        _baselines = baselines;
        _writer = writer;
        _downstreamCorrelator = downstreamCorrelator;
        _context = context;
    }
```

to:

```csharp
    private readonly IMetricsRepository _metrics;
    private readonly IBaselineRepository _baselines;
    private readonly FindingWriter _writer;
    private readonly DownstreamFailureCorrelator _downstreamCorrelator;
    private readonly UpstreamCauseCorrelator _upstreamCorrelator;
    private readonly LogsPlatformDbContext _context;

    public RateAnomalyDetector(IMetricsRepository metrics, IBaselineRepository baselines, FindingWriter writer, DownstreamFailureCorrelator downstreamCorrelator, UpstreamCauseCorrelator upstreamCorrelator, LogsPlatformDbContext context)
    {
        _metrics = metrics;
        _baselines = baselines;
        _writer = writer;
        _downstreamCorrelator = downstreamCorrelator;
        _upstreamCorrelator = upstreamCorrelator;
        _context = context;
    }
```

Then, in the same file's `WriteRateFindingAsync` method, change:

```csharp
            if (triggerEvent is not null)
            {
                await _downstreamCorrelator.RunAsync(finding, triggerEvent.CorrelationId!, operationId, triggerEvent.Timestamp);
            }
```

to:

```csharp
            if (triggerEvent is not null)
            {
                await _downstreamCorrelator.RunAsync(finding, triggerEvent.CorrelationId!, operationId, triggerEvent.Timestamp);
                await _upstreamCorrelator.RunAsync(finding, triggerEvent.CorrelationId!, operationId, triggerEvent.Timestamp);
            }
```

- [ ] **Step 6: Wire into NewExceptionDetector**

In `src/LogsPlatform.Web/Services/Analysis/NewExceptionDetector.cs`, change:

```csharp
    private readonly LogsPlatformDbContext _context;
    private readonly FindingWriter _writer;
    private readonly DownstreamFailureCorrelator _downstreamCorrelator;

    public NewExceptionDetector(LogsPlatformDbContext context, FindingWriter writer, DownstreamFailureCorrelator downstreamCorrelator)
    {
        _context = context;
        _writer = writer;
        _downstreamCorrelator = downstreamCorrelator;
    }
```

to:

```csharp
    private readonly LogsPlatformDbContext _context;
    private readonly FindingWriter _writer;
    private readonly DownstreamFailureCorrelator _downstreamCorrelator;
    private readonly UpstreamCauseCorrelator _upstreamCorrelator;

    public NewExceptionDetector(LogsPlatformDbContext context, FindingWriter writer, DownstreamFailureCorrelator downstreamCorrelator, UpstreamCauseCorrelator upstreamCorrelator)
    {
        _context = context;
        _writer = writer;
        _downstreamCorrelator = downstreamCorrelator;
        _upstreamCorrelator = upstreamCorrelator;
    }
```

Then change:

```csharp
                var triggerEvent = events.First(e => e.EnvironmentId == envId);
                if (triggerEvent.CorrelationId is not null && triggerEvent.OperationId is not null)
                {
                    await _downstreamCorrelator.RunAsync(finding, triggerEvent.CorrelationId, triggerEvent.OperationId.Value, triggerEvent.Timestamp);
                }
```

to:

```csharp
                var triggerEvent = events.First(e => e.EnvironmentId == envId);
                if (triggerEvent.CorrelationId is not null && triggerEvent.OperationId is not null)
                {
                    await _downstreamCorrelator.RunAsync(finding, triggerEvent.CorrelationId, triggerEvent.OperationId.Value, triggerEvent.Timestamp);
                    await _upstreamCorrelator.RunAsync(finding, triggerEvent.CorrelationId, triggerEvent.OperationId.Value, triggerEvent.Timestamp);
                }
```

- [ ] **Step 7: Register in DI**

In `src/LogsPlatform.Web/Program.cs`, change:

```csharp
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.DownstreamFailureCorrelator>();
```

to:

```csharp
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.DownstreamFailureCorrelator>();
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.UpstreamCauseCorrelator>();
```

- [ ] **Step 8: Fix the now-broken direct-construction test call sites**

In `tests/LogsPlatform.Tests/Web/RateAnomalyDetectorTests.cs`, there are 3 identical lines:

```csharp
        var downstreamCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var detector = new RateAnomalyDetector(metrics, baselines, writer, downstreamCorrelator, context);
```

Replace **each of the 3 occurrences** with:

```csharp
        var downstreamCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var upstreamCorrelator = new UpstreamCauseCorrelator(findingRepository, context);
        var detector = new RateAnomalyDetector(metrics, baselines, writer, downstreamCorrelator, upstreamCorrelator, context);
```

In `tests/LogsPlatform.Tests/Web/NewExceptionDetectorTests.cs`, there are 3 identical lines:

```csharp
        var downstreamCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var detector = new NewExceptionDetector(context, writer, downstreamCorrelator);
```

Replace **each of the 3 occurrences** with:

```csharp
        var downstreamCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var upstreamCorrelator = new UpstreamCauseCorrelator(findingRepository, context);
        var detector = new NewExceptionDetector(context, writer, downstreamCorrelator, upstreamCorrelator);
```

In `tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs`, in the `BuildRunner` method, change:

```csharp
        var downstreamFailureCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var baselineCalculator = new BaselineCalculator(metricsRepository, baselineRepository);
        var rateAnomalyDetector = new RateAnomalyDetector(metricsRepository, baselineRepository, writer, downstreamFailureCorrelator, context);
        var newExceptionDetector = new NewExceptionDetector(context, writer, downstreamFailureCorrelator);
```

to:

```csharp
        var downstreamFailureCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var upstreamCauseCorrelator = new UpstreamCauseCorrelator(findingRepository, context);
        var baselineCalculator = new BaselineCalculator(metricsRepository, baselineRepository);
        var rateAnomalyDetector = new RateAnomalyDetector(metricsRepository, baselineRepository, writer, downstreamFailureCorrelator, upstreamCauseCorrelator, context);
        var newExceptionDetector = new NewExceptionDetector(context, writer, downstreamFailureCorrelator, upstreamCauseCorrelator);
```

- [ ] **Step 9: Run the full affected test set**

Run: `dotnet test --filter "FullyQualifiedName~UpstreamCauseCorrelatorTests|FullyQualifiedName~RateAnomalyDetectorTests|FullyQualifiedName~NewExceptionDetectorTests|FullyQualifiedName~AnalysisEngineTickRunnerTests"`
Expected: all passing (2 + 3 + 3 + 2 = 10 tests).

- [ ] **Step 10: Commit**

```bash
git add src/LogsPlatform.Web/Services/Analysis/UpstreamCauseCorrelator.cs \
        src/LogsPlatform.Web/Services/Analysis/RateAnomalyDetector.cs \
        src/LogsPlatform.Web/Services/Analysis/NewExceptionDetector.cs \
        src/LogsPlatform.Web/Program.cs \
        tests/LogsPlatform.Tests/Web/UpstreamCauseCorrelatorTests.cs \
        tests/LogsPlatform.Tests/Web/RateAnomalyDetectorTests.cs \
        tests/LogsPlatform.Tests/Web/NewExceptionDetectorTests.cs \
        tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs
git commit -m "feat: add UpstreamCauseCorrelator, mirroring DownstreamFailureCorrelator"
```

---

## Task 3: ConcurrentFindingCorrelator + RecurrenceCorrelator

Both are small, share the same wiring point (`AnalysisEngineTickRunner`'s per-new-finding loop), and neither depends on the other — combined into one task since splitting them would just duplicate the same `AnalysisEngineTickRunner`/`Program.cs`/`AnalysisEngineTickRunnerTests.cs` edits twice. Each still gets its own class, its own test file, and is independently reviewable within this task's diff.

**Files:**
- Create: `src/LogsPlatform.Web/Services/Analysis/ConcurrentFindingCorrelator.cs`
- Create: `src/LogsPlatform.Web/Services/Analysis/RecurrenceCorrelator.cs`
- Modify: `src/LogsPlatform.Web/Services/Analysis/AnalysisEngineTickRunner.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Create: `tests/LogsPlatform.Tests/Web/ConcurrentFindingCorrelatorTests.cs`
- Create: `tests/LogsPlatform.Tests/Web/RecurrenceCorrelatorTests.cs`
- Modify: `tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs`

**Interfaces:**
- Consumes: `IFindingRepository.GetOtherOpenFindingsForApplicationAsync`/`FindMostRecentClosedAsync` (Task 1).
- Produces: `ConcurrentFindingCorrelator.RunAsync(Finding finding) : Task`, `RecurrenceCorrelator.RunAsync(Finding finding) : Task` — not consumed by any later task.

- [ ] **Step 1: Write the failing unit tests for ConcurrentFindingCorrelator**

Create `tests/LogsPlatform.Tests/Web/ConcurrentFindingCorrelatorTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ConcurrentFindingCorrelatorTests
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
    public async Task RunAsync_OtherOpenFindingExistsOnSameApplication_AddsHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "ConcurrentCorrelatorTestApp");
        var findingRepository = new FindingRepository(context);

        var thisFinding = await findingRepository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "this one",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });
        await findingRepository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.PerformanceDegradation,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 2, Title = "other one",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.Medium, ConfidenceLevel = ConfidenceLevel.Medium, Status = FindingStatus.Acknowledged
        });

        var correlator = new ConcurrentFindingCorrelator(findingRepository);
        await correlator.RunAsync(thisFinding);

        var details = await findingRepository.GetByIdAsync(thisFinding.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
        Assert.Contains(details.Evidence, e => e.EvidenceType == EvidenceType.Finding);
    }

    [Fact]
    public async Task RunAsync_NoOtherOpenFindings_DoesNotAddHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "ConcurrentCorrelatorNoneTestApp");
        var findingRepository = new FindingRepository(context);

        var thisFinding = await findingRepository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "this one",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        var correlator = new ConcurrentFindingCorrelator(findingRepository);
        await correlator.RunAsync(thisFinding);

        var details = await findingRepository.GetByIdAsync(thisFinding.Id);
        Assert.Empty(details!.Statements);
        Assert.Empty(details.Evidence);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build`
Expected: build error — `ConcurrentFindingCorrelator` does not exist yet.

- [ ] **Step 3: Implement ConcurrentFindingCorrelator**

Create `src/LogsPlatform.Web/Services/Analysis/ConcurrentFindingCorrelator.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class ConcurrentFindingCorrelator
{
    private readonly IFindingRepository _findings;

    public ConcurrentFindingCorrelator(IFindingRepository findings)
    {
        _findings = findings;
    }

    public async Task RunAsync(Finding finding)
    {
        var others = await _findings.GetOtherOpenFindingsForApplicationAsync(finding.ApplicationId, finding.Id);
        if (others.Count == 0)
        {
            return;
        }

        foreach (var other in others)
        {
            await _findings.AddEvidenceAsync(finding.Id, EvidenceType.Finding, other.Id, $"Finding #{other.Id} ({other.Type}) detected at {other.DetectedAt:u}");
        }

        var hypothesis = $"{others.Count} other Finding(s) are currently open on this Application. There may be a shared root cause, but this has not been confirmed.";

        await _findings.AddStatementAsync(finding.Id, DetectorStatementKind.Hypothesis, hypothesis);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ConcurrentFindingCorrelatorTests"`
Expected: 2/2 passing.

- [ ] **Step 5: Write the failing unit tests for RecurrenceCorrelator**

Create `tests/LogsPlatform.Tests/Web/RecurrenceCorrelatorTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class RecurrenceCorrelatorTests
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
    public async Task RunAsync_MatchingResolvedFindingExists_AddsHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "RecurrenceCorrelatorTestApp");
        var findingRepository = new FindingRepository(context);

        await findingRepository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "prior resolved",
            DetectedAt = DateTime.UtcNow.AddDays(-3), Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.Resolved
        });
        var current = await findingRepository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "current",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        var correlator = new RecurrenceCorrelator(findingRepository);
        await correlator.RunAsync(current);

        var details = await findingRepository.GetByIdAsync(current.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
        Assert.Contains(details.Evidence, e => e.EvidenceType == EvidenceType.Finding);
    }

    [Fact]
    public async Task RunAsync_NoPriorClosedFinding_DoesNotAddHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "RecurrenceCorrelatorNoneTestApp");
        var findingRepository = new FindingRepository(context);

        var current = await findingRepository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "current",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        var correlator = new RecurrenceCorrelator(findingRepository);
        await correlator.RunAsync(current);

        var details = await findingRepository.GetByIdAsync(current.Id);
        Assert.Empty(details!.Statements);
        Assert.Empty(details.Evidence);
    }
}
```

- [ ] **Step 6: Run to verify it fails**

Run: `dotnet build`
Expected: build error — `RecurrenceCorrelator` does not exist yet.

- [ ] **Step 7: Implement RecurrenceCorrelator**

Create `src/LogsPlatform.Web/Services/Analysis/RecurrenceCorrelator.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class RecurrenceCorrelator
{
    private readonly IFindingRepository _findings;

    public RecurrenceCorrelator(IFindingRepository findings)
    {
        _findings = findings;
    }

    public async Task RunAsync(Finding finding)
    {
        var prior = await _findings.FindMostRecentClosedAsync(
            finding.ApplicationId, finding.EnvironmentId, finding.ScopeType, finding.ScopeId, finding.Type, finding.Id);
        if (prior is null)
        {
            return;
        }

        await _findings.AddEvidenceAsync(finding.Id, EvidenceType.Finding, prior.Id, $"Finding #{prior.Id} ({prior.Status}) detected at {prior.DetectedAt:u}");

        var hypothesis = $"This appears to be a recurrence of a previously {prior.Status.ToString().ToLowerInvariant()} issue detected at {prior.DetectedAt:u}. It has not been confirmed to be the same root cause.";
        await _findings.AddStatementAsync(finding.Id, DetectorStatementKind.Hypothesis, hypothesis);
    }
}
```

- [ ] **Step 8: Run the test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RecurrenceCorrelatorTests"`
Expected: 2/2 passing.

- [ ] **Step 9: Wire both into AnalysisEngineTickRunner**

In `src/LogsPlatform.Web/Services/Analysis/AnalysisEngineTickRunner.cs`, change:

```csharp
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
```

to:

```csharp
    private readonly IApplicationRepository _applications;
    private readonly IAppEnvironmentRepository _environments;
    private readonly IBaselineRepository _baselines;
    private readonly IFindingRepository _findings;
    private readonly BaselineCalculator _baselineCalculator;
    private readonly RateAnomalyDetector _rateAnomalyDetector;
    private readonly NewExceptionDetector _newExceptionDetector;
    private readonly CustomerOutlierDetector _customerOutlierDetector;
    private readonly DeploymentCorrelator _deploymentCorrelator;
    private readonly ConcurrentFindingCorrelator _concurrentFindingCorrelator;
    private readonly RecurrenceCorrelator _recurrenceCorrelator;

    public AnalysisEngineTickRunner(
        IApplicationRepository applications,
        IAppEnvironmentRepository environments,
        IBaselineRepository baselines,
        IFindingRepository findings,
        BaselineCalculator baselineCalculator,
        RateAnomalyDetector rateAnomalyDetector,
        NewExceptionDetector newExceptionDetector,
        CustomerOutlierDetector customerOutlierDetector,
        DeploymentCorrelator deploymentCorrelator,
        ConcurrentFindingCorrelator concurrentFindingCorrelator,
        RecurrenceCorrelator recurrenceCorrelator)
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
        _concurrentFindingCorrelator = concurrentFindingCorrelator;
        _recurrenceCorrelator = recurrenceCorrelator;
    }
```

Then change:

```csharp
        var newFindings = await _findings.GetDetectedSinceAsync(applicationId, environmentId, tickStart - TickLookback);
        foreach (var finding in newFindings)
        {
            await _deploymentCorrelator.RunAsync(finding);
        }
```

to:

```csharp
        var newFindings = await _findings.GetDetectedSinceAsync(applicationId, environmentId, tickStart - TickLookback);
        foreach (var finding in newFindings)
        {
            await _deploymentCorrelator.RunAsync(finding);
            await _concurrentFindingCorrelator.RunAsync(finding);
            await _recurrenceCorrelator.RunAsync(finding);
        }
```

- [ ] **Step 10: Register both in DI**

In `src/LogsPlatform.Web/Program.cs`, change:

```csharp
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.DeploymentCorrelator>();
```

to:

```csharp
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.DeploymentCorrelator>();
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.ConcurrentFindingCorrelator>();
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.RecurrenceCorrelator>();
```

- [ ] **Step 11: Fix the now-broken AnalysisEngineTickRunner construction in tests**

In `tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs`, in `BuildRunner`, change:

```csharp
        var deploymentCorrelator = new DeploymentCorrelator(findingRepository, deploymentRepository);

        return new AnalysisEngineTickRunner(
            applicationRepository, environmentRepository, baselineRepository, findingRepository,
            baselineCalculator, rateAnomalyDetector, newExceptionDetector, customerOutlierDetector, deploymentCorrelator);
```

to:

```csharp
        var deploymentCorrelator = new DeploymentCorrelator(findingRepository, deploymentRepository);
        var concurrentFindingCorrelator = new ConcurrentFindingCorrelator(findingRepository);
        var recurrenceCorrelator = new RecurrenceCorrelator(findingRepository);

        return new AnalysisEngineTickRunner(
            applicationRepository, environmentRepository, baselineRepository, findingRepository,
            baselineCalculator, rateAnomalyDetector, newExceptionDetector, customerOutlierDetector,
            deploymentCorrelator, concurrentFindingCorrelator, recurrenceCorrelator);
```

- [ ] **Step 12: Add one integration test proving the new correlators fire through the real tick runner**

In `tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs`, add this test after the existing `RunOneTickAsync_ErrorSpikeWithRecentDeployment_CorrelatorAttachesHypothesis` test, before the closing `}` of the class:

```csharp
    [Fact]
    public async Task RunOneTickAsync_ErrorSpikeWithOtherOpenFinding_ConcurrentCorrelatorAttachesHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "TickRunnerConcurrentTestApp");

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

        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        context.Baselines.Add(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = operation.Id,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = (byte)currentHourStart.Hour,
            MeanValue = 5, StdDevValue = 1, SampleCount = 20, LastUpdatedAt = DateTime.UtcNow
        });
        for (var i = 0; i < 40; i++)
        {
            context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = operation.Id, Timestamp = currentHourStart.AddMinutes(i % 59), Severity = 17, Message = $"spike-{i}" });
        }
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        await findingRepository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.PerformanceDegradation,
            ScopeType = AnalysisScopeType.Operation, ScopeId = operation.Id, Title = "already open",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.Medium, ConfidenceLevel = ConfidenceLevel.Medium, Status = FindingStatus.New
        });

        var runner = BuildRunner(context);
        await runner.RunOneTickAsync();

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.ErrorSpike);
        Assert.NotNull(finding);

        var verifyRepository = new FindingRepository(verifyContext);
        var details = await verifyRepository.GetByIdAsync(finding!.Id);
        Assert.Contains(details!.Evidence, e => e.EvidenceType == EvidenceType.Finding);
    }
```

- [ ] **Step 13: Run the full affected test set**

Run: `dotnet test --filter "FullyQualifiedName~ConcurrentFindingCorrelatorTests|FullyQualifiedName~RecurrenceCorrelatorTests|FullyQualifiedName~AnalysisEngineTickRunnerTests"`
Expected: all passing (2 + 2 + 3 = 7 tests).

- [ ] **Step 14: Commit**

```bash
git add src/LogsPlatform.Web/Services/Analysis/ConcurrentFindingCorrelator.cs \
        src/LogsPlatform.Web/Services/Analysis/RecurrenceCorrelator.cs \
        src/LogsPlatform.Web/Services/Analysis/AnalysisEngineTickRunner.cs \
        src/LogsPlatform.Web/Program.cs \
        tests/LogsPlatform.Tests/Web/ConcurrentFindingCorrelatorTests.cs \
        tests/LogsPlatform.Tests/Web/RecurrenceCorrelatorTests.cs \
        tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs
git commit -m "feat: add ConcurrentFindingCorrelator and RecurrenceCorrelator"
```

---

## Task 4: FindingDetail.razor Evidence link for the new Finding evidence type

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Pages/FindingDetail.razor`

**Interfaces:**
- Consumes: `EvidenceType.Finding` (Task 1). The page's own route, `/findings/{Id:long}` (existing, unchanged).
- Produces: nothing — this is the last task.

- [ ] **Step 1: Add the new Evidence-rendering branch**

In `src/LogsPlatform.Web/Components/Pages/FindingDetail.razor`, change:

```razor
                @if (item.EvidenceType.ToString() == "Event")
                {
                    <a href="@SearchLink()">@item.Description</a>
                }
                else if (item.EvidenceType.ToString() == "ExceptionGroup")
                {
                    <a href="/exceptions/@item.ReferenceId">@item.Description</a>
                }
                else if (item.EvidenceType.ToString() == "Deployment")
                {
                    <a href="/admin/applications">@item.Description</a>
                }
                else
                {
                    @item.Description
                }
```

to:

```razor
                @if (item.EvidenceType.ToString() == "Event")
                {
                    <a href="@SearchLink()">@item.Description</a>
                }
                else if (item.EvidenceType.ToString() == "ExceptionGroup")
                {
                    <a href="/exceptions/@item.ReferenceId">@item.Description</a>
                }
                else if (item.EvidenceType.ToString() == "Deployment")
                {
                    <a href="/admin/applications">@item.Description</a>
                }
                else if (item.EvidenceType.ToString() == "Finding")
                {
                    <a href="/findings/@item.ReferenceId">@item.Description</a>
                }
                else
                {
                    @item.Description
                }
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test`
Expected: all tests green — this closes out all 3 new correlators plus the UI link, on top of the pre-existing suite.

- [ ] **Step 4: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/FindingDetail.razor
git commit -m "feat: link Finding-type evidence to the correlated Finding's detail page"
```

---

## Final Verification

- [ ] Run `dotnet build` — 0 errors.
- [ ] Run `dotnet test` — full suite green, including the 3 new correlator test files, the 4 new `FindingRepositoryTests` methods, and the 1 new `AnalysisEngineTickRunnerTests` integration test.
- [ ] Confirm via `grep -c "AddScoped<LogsPlatform.Web.Services.Analysis" src/LogsPlatform.Web/Program.cs` that the Analysis DI block grew by exactly 3 (2 existing correlators + 3 new = 5 correlator registrations, plus the 3 detectors/writer/calculator/tick-runner already there).
- [ ] Manually confirm (no live browser step required for this milestone — these are backend Hypothesis-producing correlators surfaced through the existing, unchanged Finding detail page) that a Finding's Evidence list renders a clickable link for `Finding`-type evidence, by inspecting the updated `FindingDetail.razor` diff.
- [ ] Invoke `superpowers:finishing-a-development-branch`.
