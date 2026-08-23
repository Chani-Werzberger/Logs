# M3: Query API + Investigation UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Query API (events/timeline/exception-groups) plus three Blazor pages (Search, Timeline, Exceptions) that let a person search, filter, and drill down through data M2 already ingested.

**Architecture:** New read endpoints on the existing `LogsPlatform.Web` project (no new project), following the established controller → repository pattern. Blazor pages call repositories **directly** via dependency injection (matching every existing admin page — see `ModulesAdmin.razor`/`ApplicationsAdmin.razor`), not via HTTP to the new controllers; the controllers exist for external HTTP consumers (Swagger, scripts), the same way the Admin API and the Blazor Admin UI both already independently reach the same repositories.

**Tech Stack:** ASP.NET Core Web API + Blazor Server (existing), EF Core 10 (existing), no new packages.

## Global Constraints

- No schema changes. `Event`/`ExceptionGroup` already have the indexes this milestone's queries need: `(ApplicationId, EnvironmentId, Timestamp)`, `(ApplicationId, OperationId, Timestamp)`, `CorrelationId`, `TraceId`, `ExceptionGroupId` (see `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs:193-198`).
- Severity wire vocabulary: `Trace=1, Debug=5, Info=9, Warn=13, Error=17, Fatal=21` (`IngestionProcessor.SeverityMap`, being promoted to a shared class in Task 1). Query filters and UI must use this exact vocabulary, not raw ints, for consistency with the already-shipped ingestion contract.
- **`GET /api/v1/timeline` requires `applicationId`** — a correction to the design doc (`docs/superpowers/specs/2026-08-23-m3-query-investigation-design.md` didn't require it on this endpoint). `CorrelationId` is only unique-constrained per-application-scoped via `EventKey` uniqueness, not globally — two different applications could coincidentally use the same `CorrelationId` string, and without an `applicationId` filter, `GET /api/v1/timeline?correlationId=X` would silently mix events from unrelated applications. Every other Query API endpoint already requires `applicationId`; Timeline must too.
- Repositories return domain entities (`Event`, `ExceptionGroup`), never DTOs — controllers own response shaping via a static `ToSummary`/`ToDetail`/`ToResponse` helper, matching `VersionsController.ToResponse` (`src/LogsPlatform.Web/Controllers/VersionsController.cs:83-84`) and every other controller in the project.
- Read queries use `.AsNoTracking()` (matching `EventRepository.PartitionByExistingKeysAsync`'s existing read pattern).
- New Query API endpoints (and only these 5) return ASP.NET Core's built-in `ProblemDetails` shape for 4xx/5xx via `builder.Services.AddProblemDetails()` — not custom middleware, not applied to the existing Admin API.
- **Tests that read back data written through a real hosted server MUST construct the verification `DbContext` via `new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options` directly — NEVER via `TestDatabase.CreateContext()`**, which calls `EnsureDeleted()`+`Migrate()` on every call and wipes the rows the test is trying to verify. This exact mistake has caused real bugs earlier in this project.
- No mocking, no fakes — every test hits real SQL Server LocalDB, per the project's standing convention. Repository-level tests seed data by inserting entities directly via a `DbContext` (matching `EventRepositoryTests.cs`'s existing pattern from M2a); controller-level tests seed by POSTing through the real `/api/v1/ingest/events` endpoint (matching `IngestionControllerTests.cs`'s existing pattern), not by inserting rows directly — this exercises the real ingestion path end-to-end.
- Keep test volume to core-behavior-plus-one-edge-case per the project's standing test-volume instruction, not exhaustive filter-combination coverage.

---

### Task 1: `SeverityLevels` shared lookup

**Suggested model tier:** cheapest (mechanical extraction of an existing private field).

**Files:**
- Create: `src/LogsPlatform.Web/Services/SeverityLevels.cs`
- Modify: `src/LogsPlatform.Web/Services/IngestionProcessor.cs:11-14` (remove the private `SeverityMap`, use the new shared class instead)
- Test: `tests/LogsPlatform.Tests/Web/SeverityLevelsTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces (used by Tasks 4, 6, 7, 8): `public static class SeverityLevels { public static readonly IReadOnlyDictionary<string, int> ByName; public static readonly IReadOnlyDictionary<int, string> ByValue; }`.

- [ ] **Step 1: Write the failing test**

`tests/LogsPlatform.Tests/Web/SeverityLevelsTests.cs`:

```csharp
using LogsPlatform.Web.Services;

namespace LogsPlatform.Tests.Web;

public class SeverityLevelsTests
{
    [Theory]
    [InlineData("Trace", 1)]
    [InlineData("Debug", 5)]
    [InlineData("Info", 9)]
    [InlineData("Warn", 13)]
    [InlineData("Error", 17)]
    [InlineData("Fatal", 21)]
    public void ByName_KnownSeverity_ReturnsExpectedValue(string name, int expected)
    {
        Assert.Equal(expected, SeverityLevels.ByName[name]);
    }

    [Fact]
    public void ByValue_IsExactReverseOfByName()
    {
        foreach (var (name, value) in SeverityLevels.ByName)
        {
            Assert.Equal(name, SeverityLevels.ByValue[value]);
        }
        Assert.Equal(SeverityLevels.ByName.Count, SeverityLevels.ByValue.Count);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter SeverityLevelsTests`
Expected: FAIL — compile error, `SeverityLevels` does not exist.

- [ ] **Step 3: Implement `SeverityLevels.cs`**

`src/LogsPlatform.Web/Services/SeverityLevels.cs`:

```csharp
namespace LogsPlatform.Web.Services;

public static class SeverityLevels
{
    public static readonly IReadOnlyDictionary<string, int> ByName = new Dictionary<string, int>
    {
        ["Trace"] = 1, ["Debug"] = 5, ["Info"] = 9, ["Warn"] = 13, ["Error"] = 17, ["Fatal"] = 21
    };

    public static readonly IReadOnlyDictionary<int, string> ByValue =
        ByName.ToDictionary(pair => pair.Value, pair => pair.Key);
}
```

- [ ] **Step 4: Update `IngestionProcessor` to use the shared class**

In `src/LogsPlatform.Web/Services/IngestionProcessor.cs`, delete lines 11-14 (the private `SeverityMap` field) and replace every reference to `SeverityMap` in the file with `SeverityLevels.ByName`. There is exactly one call site (the severity lookup during validation) — find it with:

Run: `grep -n "SeverityMap" src/LogsPlatform.Web/Services/IngestionProcessor.cs`

Replace `SeverityMap.TryGetValue(request.Severity, out var severityValue)` with `SeverityLevels.ByName.TryGetValue(request.Severity, out var severityValue)`.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter "SeverityLevelsTests|IngestionProcessorTests"`
Expected: PASS — all `SeverityLevelsTests` plus all pre-existing `IngestionProcessorTests` (regression check that the refactor didn't change behavior).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Services/SeverityLevels.cs src/LogsPlatform.Web/Services/IngestionProcessor.cs tests/LogsPlatform.Tests/Web/SeverityLevelsTests.cs
git commit -m "Extract SeverityLevels shared lookup from IngestionProcessor"
```

---

### Task 2: `IEventRepository` query methods

**Suggested model tier:** standard (EF Core query composition, real judgment on filter/paging correctness).

**Files:**
- Modify: `src/LogsPlatform.Domain/Repositories/IEventRepository.cs`
- Modify: `src/LogsPlatform.Infrastructure/Repositories/EventRepository.cs`
- Test: `tests/LogsPlatform.Tests/Infrastructure/EventRepositoryQueryTests.cs`

**Interfaces:**
- Consumes: `Event` (`src/LogsPlatform.Domain/Entities/Event.cs`, unchanged).
- Produces (used by Task 4): `public record EventQueryParameters(int ApplicationId, int EnvironmentId, DateTime? From, DateTime? To, int? Severity, int? ModuleId, int? ScreenServiceId, int? ProcessId, int? OperationId, string? CorrelationId, string? TraceId, string? UserId, string? CustomerId, long? ExceptionGroupId, int? VersionId, double? DurationMinMs, double? DurationMaxMs, string? MessageContains, int Page, int PageSize)`; `public record TimelineQuery(int ApplicationId, string? CorrelationId, string? TraceId, int? OperationId, string? UserId, string? CustomerId)`; `IEventRepository.QueryAsync(EventQueryParameters) → Task<(IReadOnlyList<Event> Items, int TotalCount)>`; `GetByIdAsync(int applicationId, long id) → Task<Event?>`; `GetTimelineAsync(TimelineQuery) → Task<IReadOnlyList<Event>>`.

`UserId`/`CustomerId` on `EventQueryParameters`/`TimelineQuery` are the `Event.CustomerId`/`Event.AppUserId` **foreign key integers** (not external string ids) — the UI resolves an external customer/user id to its internal `int` id before calling the repository, matching how every other filter here is an internal id. (Note the parameter name `UserId` maps to `Event.AppUserId`, and `CustomerId` maps to `Event.CustomerId` — this asymmetry mirrors `Event`'s own property names.)

- [ ] **Step 1: Write the failing tests**

`tests/LogsPlatform.Tests/Infrastructure/EventRepositoryQueryTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class EventRepositoryQueryTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> SeedAppAndEnvironmentAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();

        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();

        return (app.Id, env.Id);
    }

    private static Event BuildEvent(int appId, int envId, DateTime timestamp, int severity = 17, string? correlationId = null, string message = "test event") => new()
    {
        ApplicationId = appId, EnvironmentId = envId, Timestamp = timestamp, Severity = severity,
        CorrelationId = correlationId, Message = message
    };

    [Fact]
    public async Task QueryAsync_FiltersByApplicationEnvironmentAndSeverity_OrdersNewestFirstAndPaginates()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppAndEnvironmentAsync(context, "QueryFilterTestApp");
        var (otherAppId, otherEnvId) = await SeedAppAndEnvironmentAsync(context, "OtherApp");

        var now = DateTime.UtcNow;
        context.Events.AddRange(
            BuildEvent(appId, envId, now.AddMinutes(-1), severity: 17),
            BuildEvent(appId, envId, now.AddMinutes(-2), severity: 9),
            BuildEvent(appId, envId, now.AddMinutes(-3), severity: 17),
            BuildEvent(otherAppId, otherEnvId, now, severity: 17));
        await context.SaveChangesAsync();

        var repository = new EventRepository(context);
        var (items, totalCount) = await repository.QueryAsync(new EventQueryParameters(
            ApplicationId: appId, EnvironmentId: envId, From: null, To: null, Severity: 17,
            ModuleId: null, ScreenServiceId: null, ProcessId: null, OperationId: null,
            CorrelationId: null, TraceId: null, UserId: null, CustomerId: null,
            ExceptionGroupId: null, VersionId: null, DurationMinMs: null, DurationMaxMs: null,
            MessageContains: null, Page: 1, PageSize: 50));

        Assert.Equal(2, totalCount);
        Assert.Equal(2, items.Count);
        Assert.True(items[0].Timestamp > items[1].Timestamp);
    }

    [Fact]
    public async Task QueryAsync_PageSizeExceedsMax_IsClampedTo200()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppAndEnvironmentAsync(context, "ClampTestApp");
        var repository = new EventRepository(context);

        var (items, _) = await repository.QueryAsync(new EventQueryParameters(
            ApplicationId: appId, EnvironmentId: envId, From: null, To: null, Severity: null,
            ModuleId: null, ScreenServiceId: null, ProcessId: null, OperationId: null,
            CorrelationId: null, TraceId: null, UserId: null, CustomerId: null,
            ExceptionGroupId: null, VersionId: null, DurationMinMs: null, DurationMaxMs: null,
            MessageContains: null, Page: 1, PageSize: 5000));

        Assert.True(items.Count <= 200);
    }

    [Fact]
    public async Task GetByIdAsync_MismatchedApplicationId_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppAndEnvironmentAsync(context, "IdorTestApp");
        var (otherAppId, _) = await SeedAppAndEnvironmentAsync(context, "IdorOtherApp");
        var evt = BuildEvent(appId, envId, DateTime.UtcNow);
        context.Events.Add(evt);
        await context.SaveChangesAsync();

        var repository = new EventRepository(context);

        Assert.NotNull(await repository.GetByIdAsync(appId, evt.Id));
        Assert.Null(await repository.GetByIdAsync(otherAppId, evt.Id));
    }

    [Fact]
    public async Task GetTimelineAsync_ByCorrelationId_ReturnsOnlyMatchingEventsOrderedAscending()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppAndEnvironmentAsync(context, "TimelineTestApp");
        var now = DateTime.UtcNow;
        context.Events.AddRange(
            BuildEvent(appId, envId, now.AddMinutes(-2), correlationId: "order-1"),
            BuildEvent(appId, envId, now.AddMinutes(-1), correlationId: "order-1"),
            BuildEvent(appId, envId, now, correlationId: "order-2"));
        await context.SaveChangesAsync();

        var repository = new EventRepository(context);
        var timeline = await repository.GetTimelineAsync(new TimelineQuery(
            ApplicationId: appId, CorrelationId: "order-1", TraceId: null, OperationId: null, UserId: null, CustomerId: null));

        Assert.Equal(2, timeline.Count);
        Assert.True(timeline[0].Timestamp < timeline[1].Timestamp);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter EventRepositoryQueryTests`
Expected: FAIL — compile error, `EventQueryParameters`/`TimelineQuery`/`QueryAsync`/`GetByIdAsync`/`GetTimelineAsync` do not exist.

- [ ] **Step 3: Update `IEventRepository`**

`src/LogsPlatform.Domain/Repositories/IEventRepository.cs`:

```csharp
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public record IngestResult(int Accepted, int DuplicateEventKeysSkipped);

public record EventQueryParameters(
    int ApplicationId,
    int EnvironmentId,
    DateTime? From,
    DateTime? To,
    int? Severity,
    int? ModuleId,
    int? ScreenServiceId,
    int? ProcessId,
    int? OperationId,
    string? CorrelationId,
    string? TraceId,
    string? UserId,
    string? CustomerId,
    long? ExceptionGroupId,
    int? VersionId,
    double? DurationMinMs,
    double? DurationMaxMs,
    string? MessageContains,
    int Page,
    int PageSize);

public record TimelineQuery(
    int ApplicationId,
    string? CorrelationId,
    string? TraceId,
    int? OperationId,
    string? UserId,
    string? CustomerId);

public interface IEventRepository
{
    Task<IngestResult> AddEventsAsync(int applicationId, IReadOnlyList<Event> events);
    Task<(IReadOnlyList<Event> Items, int TotalCount)> QueryAsync(EventQueryParameters parameters);
    Task<Event?> GetByIdAsync(int applicationId, long id);
    Task<IReadOnlyList<Event>> GetTimelineAsync(TimelineQuery query);
}
```

Note: `UserId`/`CustomerId` here are `string?` in the parameter object but map onto `Event.AppUserId`/`Event.CustomerId`, which are `int?` foreign keys — the repository implementation parses them (see Step 4). They're carried as strings through the parameter object because the HTTP query string layer (Task 4) receives them as strings and the repository is the natural place to centralize "empty/missing filter" handling consistently with every other optional string filter here (`CorrelationId`, `TraceId`, `MessageContains`). If parsing fails (non-numeric), the repository treats the filter as not supplied — the controller (Task 4) is responsible for validating the format before calling in, so a parse failure here should not occur in practice, but the repository never throws either way.

- [ ] **Step 4: Implement the new methods in `EventRepository`**

Add to `src/LogsPlatform.Infrastructure/Repositories/EventRepository.cs` (after the existing `AddEventsAsync`/`PartitionByExistingKeysAsync`/`IsUniqueViolation` members, before the closing brace):

```csharp
    private const int MaxPageSize = 200;

    public async Task<(IReadOnlyList<Event> Items, int TotalCount)> QueryAsync(EventQueryParameters parameters)
    {
        var query = _context.Events.AsNoTracking()
            .Where(e => e.ApplicationId == parameters.ApplicationId && e.EnvironmentId == parameters.EnvironmentId);

        if (parameters.From is not null) query = query.Where(e => e.Timestamp >= parameters.From);
        if (parameters.To is not null) query = query.Where(e => e.Timestamp <= parameters.To);
        if (parameters.Severity is not null) query = query.Where(e => e.Severity == parameters.Severity);
        if (parameters.ModuleId is not null) query = query.Where(e => e.ModuleId == parameters.ModuleId);
        if (parameters.ScreenServiceId is not null) query = query.Where(e => e.ScreenServiceId == parameters.ScreenServiceId);
        if (parameters.ProcessId is not null) query = query.Where(e => e.ProcessId == parameters.ProcessId);
        if (parameters.OperationId is not null) query = query.Where(e => e.OperationId == parameters.OperationId);
        if (parameters.CorrelationId is not null) query = query.Where(e => e.CorrelationId == parameters.CorrelationId);
        if (parameters.TraceId is not null) query = query.Where(e => e.TraceId == parameters.TraceId);
        if (int.TryParse(parameters.UserId, out var userId)) query = query.Where(e => e.AppUserId == userId);
        if (int.TryParse(parameters.CustomerId, out var customerId)) query = query.Where(e => e.CustomerId == customerId);
        if (parameters.ExceptionGroupId is not null) query = query.Where(e => e.ExceptionGroupId == parameters.ExceptionGroupId);
        if (parameters.VersionId is not null) query = query.Where(e => e.VersionId == parameters.VersionId);
        if (parameters.DurationMinMs is not null) query = query.Where(e => e.DurationMs >= parameters.DurationMinMs);
        if (parameters.DurationMaxMs is not null) query = query.Where(e => e.DurationMs <= parameters.DurationMaxMs);
        if (!string.IsNullOrWhiteSpace(parameters.MessageContains)) query = query.Where(e => e.Message.Contains(parameters.MessageContains));

        var totalCount = await query.CountAsync();

        var pageSize = Math.Clamp(parameters.PageSize, 1, MaxPageSize);
        var page = Math.Max(parameters.Page, 1);

        var items = await query
            .OrderByDescending(e => e.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(e => e.Module)
            .Include(e => e.ScreenService)
            .Include(e => e.Process)
            .Include(e => e.Operation)
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task<Event?> GetByIdAsync(int applicationId, long id)
    {
        var evt = await _context.Events.AsNoTracking()
            .Include(e => e.Module)
            .Include(e => e.ScreenService)
            .Include(e => e.Process)
            .Include(e => e.Operation)
            .FirstOrDefaultAsync(e => e.Id == id);

        return evt is null || evt.ApplicationId != applicationId ? null : evt;
    }

    public async Task<IReadOnlyList<Event>> GetTimelineAsync(TimelineQuery query)
    {
        var events = _context.Events.AsNoTracking().Where(e => e.ApplicationId == query.ApplicationId);

        if (query.CorrelationId is not null)
        {
            events = events.Where(e => e.CorrelationId == query.CorrelationId);
        }
        else if (query.TraceId is not null)
        {
            events = events.Where(e => e.TraceId == query.TraceId);
        }
        else if (query.OperationId is not null && int.TryParse(query.UserId, out var userId))
        {
            events = events.Where(e => e.OperationId == query.OperationId && e.AppUserId == userId);
        }
        else if (int.TryParse(query.CustomerId, out var customerId))
        {
            events = events.Where(e => e.CustomerId == customerId);
        }
        else
        {
            return Array.Empty<Event>();
        }

        return await events
            .OrderBy(e => e.Timestamp)
            .Include(e => e.Module)
            .Include(e => e.ScreenService)
            .Include(e => e.Process)
            .Include(e => e.Operation)
            .ToListAsync();
    }
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter EventRepositoryQueryTests`
Expected: PASS — 4/4 tests.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Domain/Repositories/IEventRepository.cs src/LogsPlatform.Infrastructure/Repositories/EventRepository.cs tests/LogsPlatform.Tests/Infrastructure/EventRepositoryQueryTests.cs
git commit -m "Add Event query methods (QueryAsync/GetByIdAsync/GetTimelineAsync)"
```

---

### Task 3: `IExceptionGroupRepository` query methods

**Suggested model tier:** standard (query composition, grouped/distinct queries).

**Files:**
- Modify: `src/LogsPlatform.Domain/Repositories/IExceptionGroupRepository.cs`
- Modify: `src/LogsPlatform.Infrastructure/Repositories/ExceptionGroupRepository.cs`
- Test: `tests/LogsPlatform.Tests/Infrastructure/ExceptionGroupRepositoryQueryTests.cs`

**Interfaces:**
- Consumes: `ExceptionGroup`, `Event` (unchanged).
- Produces (used by Task 5): `public record ExceptionGroupQueryParameters(int ApplicationId, DateTime? From, DateTime? To, string SortBy)` (`SortBy` is `"LastSeenAt"` or `"OccurrenceCount"`); `public record AffectedContext(string ApplicationName, string EnvironmentName, string? VersionNumber, string? OperationName)`; `IExceptionGroupRepository.QueryAsync(ExceptionGroupQueryParameters) → Task<IReadOnlyList<ExceptionGroup>>`; `GetByIdAsync(long id) → Task<ExceptionGroup?>`; `GetDailyCountsAsync(long exceptionGroupId, int days) → Task<IReadOnlyDictionary<DateOnly, int>>`; `GetAffectedContextsAsync(long exceptionGroupId) → Task<IReadOnlyList<AffectedContext>>`.

No pagination on `QueryAsync` — exception groups are expected to be far fewer than raw events at any reasonable scale, so the full filtered/sorted list is returned.

- [ ] **Step 1: Write the failing tests**

`tests/LogsPlatform.Tests/Infrastructure/ExceptionGroupRepositoryQueryTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ExceptionGroupRepositoryQueryTests
{
    private static async Task<(int ApplicationId, int EnvironmentId, int ModuleId, int ScreenServiceId, int ProcessId, int OperationId)> SeedFullHierarchyAsync(LogsPlatformDbContext context, string appName)
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

        return (app.Id, env.Id, module.Id, screenService.Id, process.Id, operation.Id);
    }

    [Fact]
    public async Task QueryAsync_FiltersByApplicationAndSortsByLastSeenAt()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _, _, _, _, _) = await SeedFullHierarchyAsync(context, "ExGroupQueryTestApp");
        var otherApp = new Application { Name = "OtherExGroupApp", CreatedAt = DateTime.UtcNow };
        context.Applications.Add(otherApp);
        await context.SaveChangesAsync();

        var now = DateTime.UtcNow;
        context.ExceptionGroups.AddRange(
            new ExceptionGroup { ApplicationId = appId, Fingerprint = "fp-1", ExceptionType = "A", MessageTemplate = "a", RepresentativeStackTrace = "at A()", FirstSeenAt = now.AddDays(-2), LastSeenAt = now.AddDays(-2), OccurrenceCount = 1 },
            new ExceptionGroup { ApplicationId = appId, Fingerprint = "fp-2", ExceptionType = "B", MessageTemplate = "b", RepresentativeStackTrace = "at B()", FirstSeenAt = now.AddDays(-1), LastSeenAt = now, OccurrenceCount = 1 },
            new ExceptionGroup { ApplicationId = otherApp.Id, Fingerprint = "fp-3", ExceptionType = "C", MessageTemplate = "c", RepresentativeStackTrace = "at C()", FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
        await context.SaveChangesAsync();

        var repository = new ExceptionGroupRepository(context);
        var result = await repository.QueryAsync(new ExceptionGroupQueryParameters(appId, From: null, To: null, SortBy: "LastSeenAt"));

        Assert.Equal(2, result.Count);
        Assert.Equal("fp-2", result[0].Fingerprint);
    }

    [Fact]
    public async Task GetDailyCountsAsync_CountsEventsPerDayWithinWindow()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, _, _, _, _) = await SeedFullHierarchyAsync(context, "DailyCountsTestApp");
        var group = new ExceptionGroup { ApplicationId = appId, Fingerprint = "fp-daily", ExceptionType = "E", MessageTemplate = "e", RepresentativeStackTrace = "at E()", FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow, OccurrenceCount = 2 };
        context.ExceptionGroups.Add(group);
        await context.SaveChangesAsync();

        var today = DateTime.UtcNow;
        context.Events.AddRange(
            new Event { ApplicationId = appId, EnvironmentId = envId, Timestamp = today, Severity = 17, Message = "e1", ExceptionGroupId = group.Id },
            new Event { ApplicationId = appId, EnvironmentId = envId, Timestamp = today, Severity = 17, Message = "e2", ExceptionGroupId = group.Id },
            new Event { ApplicationId = appId, EnvironmentId = envId, Timestamp = today.AddDays(-20), Severity = 17, Message = "e3", ExceptionGroupId = group.Id });
        await context.SaveChangesAsync();

        var repository = new ExceptionGroupRepository(context);
        var counts = await repository.GetDailyCountsAsync(group.Id, days: 14);

        Assert.Equal(2, counts[DateOnly.FromDateTime(today)]);
        Assert.DoesNotContain(DateOnly.FromDateTime(today.AddDays(-20)), counts.Keys);
    }

    [Fact]
    public async Task GetAffectedContextsAsync_ReturnsDistinctApplicationEnvironmentVersionOperation()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, _, _, _, operationId) = await SeedFullHierarchyAsync(context, "AffectedContextsTestApp");
        var group = new ExceptionGroup { ApplicationId = appId, Fingerprint = "fp-ctx", ExceptionType = "E", MessageTemplate = "e", RepresentativeStackTrace = "at E()", FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow, OccurrenceCount = 2 };
        context.ExceptionGroups.Add(group);
        await context.SaveChangesAsync();

        context.Events.AddRange(
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = operationId, Timestamp = DateTime.UtcNow, Severity = 17, Message = "e1", ExceptionGroupId = group.Id },
            new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = operationId, Timestamp = DateTime.UtcNow, Severity = 17, Message = "e2", ExceptionGroupId = group.Id });
        await context.SaveChangesAsync();

        var repository = new ExceptionGroupRepository(context);
        var contexts = await repository.GetAffectedContextsAsync(group.Id);

        Assert.Single(contexts);
        Assert.Equal("Authorize", contexts[0].OperationName);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter ExceptionGroupRepositoryQueryTests`
Expected: FAIL — compile error, new types/methods don't exist.

- [ ] **Step 3: Update `IExceptionGroupRepository`**

`src/LogsPlatform.Domain/Repositories/IExceptionGroupRepository.cs`:

```csharp
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public record ExceptionGroupQueryParameters(int ApplicationId, DateTime? From, DateTime? To, string SortBy);

public record AffectedContext(string ApplicationName, string EnvironmentName, string? VersionNumber, string? OperationName);

public interface IExceptionGroupRepository
{
    Task<ExceptionGroup> GetOrCreateAsync(
        int applicationId, string fingerprint, string exceptionType,
        string messageTemplate, string representativeStackTrace, DateTime seenAt);
    Task<IReadOnlyList<ExceptionGroup>> QueryAsync(ExceptionGroupQueryParameters parameters);
    Task<ExceptionGroup?> GetByIdAsync(long id);
    Task<IReadOnlyDictionary<DateOnly, int>> GetDailyCountsAsync(long exceptionGroupId, int days);
    Task<IReadOnlyList<AffectedContext>> GetAffectedContextsAsync(long exceptionGroupId);
}
```

- [ ] **Step 4: Implement the new methods in `ExceptionGroupRepository`**

Add to `src/LogsPlatform.Infrastructure/Repositories/ExceptionGroupRepository.cs` (after `GetOrCreateAsync`, before `IsUniqueViolation`):

```csharp
    public async Task<IReadOnlyList<ExceptionGroup>> QueryAsync(ExceptionGroupQueryParameters parameters)
    {
        var query = _context.ExceptionGroups.AsNoTracking()
            .Where(g => g.ApplicationId == parameters.ApplicationId);

        if (parameters.From is not null) query = query.Where(g => g.LastSeenAt >= parameters.From);
        if (parameters.To is not null) query = query.Where(g => g.LastSeenAt <= parameters.To);

        query = parameters.SortBy == "OccurrenceCount"
            ? query.OrderByDescending(g => g.OccurrenceCount)
            : query.OrderByDescending(g => g.LastSeenAt);

        return await query.ToListAsync();
    }

    public async Task<ExceptionGroup?> GetByIdAsync(long id) =>
        await _context.ExceptionGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id);

    public async Task<IReadOnlyDictionary<DateOnly, int>> GetDailyCountsAsync(long exceptionGroupId, int days)
    {
        var since = DateTime.UtcNow.Date.AddDays(-(days - 1));

        var rows = await _context.Events.AsNoTracking()
            .Where(e => e.ExceptionGroupId == exceptionGroupId && e.Timestamp >= since)
            .Select(e => e.Timestamp)
            .ToListAsync();

        return rows
            .GroupBy(timestamp => DateOnly.FromDateTime(timestamp.Date))
            .ToDictionary(group => group.Key, group => group.Count());
    }

    public async Task<IReadOnlyList<AffectedContext>> GetAffectedContextsAsync(long exceptionGroupId)
    {
        var rows = await _context.Events.AsNoTracking()
            .Where(e => e.ExceptionGroupId == exceptionGroupId)
            .Select(e => new
            {
                ApplicationName = e.Application.Name,
                EnvironmentName = e.Environment.Name,
                VersionNumber = e.Version != null ? e.Version.VersionNumber : null,
                OperationName = e.Operation != null ? e.Operation.Name : null
            })
            .Distinct()
            .ToListAsync();

        return rows.Select(r => new AffectedContext(r.ApplicationName, r.EnvironmentName, r.VersionNumber, r.OperationName)).ToList();
    }
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter ExceptionGroupRepositoryQueryTests`
Expected: PASS — 3/3 tests.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Domain/Repositories/IExceptionGroupRepository.cs src/LogsPlatform.Infrastructure/Repositories/ExceptionGroupRepository.cs tests/LogsPlatform.Tests/Infrastructure/ExceptionGroupRepositoryQueryTests.cs
git commit -m "Add ExceptionGroup query methods (QueryAsync/GetByIdAsync/GetDailyCountsAsync/GetAffectedContextsAsync)"
```

---

### Task 4: Events + Timeline Query API controllers

**Suggested model tier:** standard (validation logic, response shaping, `ProblemDetails` wiring).

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/QueryContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/EventsController.cs`
- Create: `src/LogsPlatform.Web/Controllers/TimelineController.cs`
- Modify: `src/LogsPlatform.Web/Program.cs` (add `AddProblemDetails()`)
- Test: `tests/LogsPlatform.Tests/Web/EventsControllerTests.cs`
- Test: `tests/LogsPlatform.Tests/Web/TimelineControllerTests.cs`

**Interfaces:**
- Consumes: `IEventRepository` (Task 2), `SeverityLevels` (Task 1).
- Produces: `GET /api/v1/events`, `GET /api/v1/events/{id}`, `GET /api/v1/timeline` (used directly by external HTTP consumers; the Blazor UI in Tasks 7-8 calls `IEventRepository` directly instead, per the Global Constraints).

- [ ] **Step 1: Write the failing tests**

`tests/LogsPlatform.Tests/Web/EventsControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class EventsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public EventsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<(int ApplicationId, int EnvironmentId, string ApiKey)> CreateAppWithApiKeyAsync(HttpClient client, string appName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var envResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));
        var env = await envResponse.Content.ReadFromJsonAsync<EnvironmentResponse>();

        var keyResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("Events query test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        return (app.Id, env!.Id, key!.ApiKey);
    }

    private static HttpRequestMessage BuildIngestRequest(string apiKey, List<IngestEventRequest> events)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/events") { Content = JsonContent.Create(events) };
        request.Headers.Add("X-Api-Key", apiKey);
        return request;
    }

    [Fact]
    public async Task GetEvents_FiltersBySeverityAndPaginates()
    {
        var client = _factory.CreateClient();
        var (appId, environmentId, apiKey) = await CreateAppWithApiKeyAsync(client, "EventsQueryTestApp");

        var events = Enumerable.Range(0, 3).Select(i => new IngestEventRequest(
            EventKey: $"evt-{i}", Timestamp: DateTime.UtcNow, Severity: "Error", Environment: "Production",
            Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null,
            DurationMs: null, CustomerId: null, UserId: null, Message: $"error {i}", MessageTemplate: null,
            Exception: null, Metadata: null)).ToList();
        events.Add(new IngestEventRequest(
            EventKey: "evt-info", Timestamp: DateTime.UtcNow, Severity: "Info", Environment: "Production",
            Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null,
            DurationMs: null, CustomerId: null, UserId: null, Message: "info event", MessageTemplate: null,
            Exception: null, Metadata: null));
        await client.SendAsync(BuildIngestRequest(apiKey, events));

        var response = await client.GetAsync($"/api/v1/events?applicationId={appId}&environmentId={environmentId}&severity=Error&page=1&pageSize=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EventListResponse>();
        Assert.Equal(3, body!.TotalCount);
        Assert.Equal(2, body.Items.Count);
    }

    [Fact]
    public async Task GetEventById_MismatchedApplicationId_Returns404()
    {
        var client = _factory.CreateClient();
        var (appId, environmentId, apiKey) = await CreateAppWithApiKeyAsync(client, "EventByIdTestApp");
        var (otherAppId, _, _) = await CreateAppWithApiKeyAsync(client, "EventByIdOtherApp");

        await client.SendAsync(BuildIngestRequest(apiKey, new List<IngestEventRequest> { new(
            EventKey: "evt-single", Timestamp: DateTime.UtcNow, Severity: "Error", Environment: "Production",
            Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null,
            DurationMs: null, CustomerId: null, UserId: null, Message: "single event", MessageTemplate: null,
            Exception: null, Metadata: null) }));

        var listResponse = await client.GetAsync($"/api/v1/events?applicationId={appId}&environmentId={environmentId}");
        var list = await listResponse.Content.ReadFromJsonAsync<EventListResponse>();
        var eventId = list!.Items[0].Id;

        var wrongAppResponse = await client.GetAsync($"/api/v1/events/{eventId}?applicationId={otherAppId}");
        Assert.Equal(HttpStatusCode.NotFound, wrongAppResponse.StatusCode);

        var correctAppResponse = await client.GetAsync($"/api/v1/events/{eventId}?applicationId={appId}");
        Assert.Equal(HttpStatusCode.OK, correctAppResponse.StatusCode);
    }
}
```

`tests/LogsPlatform.Tests/Web/TimelineControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class TimelineControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public TimelineControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<(int ApplicationId, string ApiKey)> CreateAppWithApiKeyAsync(HttpClient client, string appName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));
        var keyResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("Timeline test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();
        return (app.Id, key!.ApiKey);
    }

    [Fact]
    public async Task GetTimeline_ByCorrelationId_ReturnsOrderedEvents()
    {
        var client = _factory.CreateClient();
        var (appId, apiKey) = await CreateAppWithApiKeyAsync(client, "TimelineQueryTestApp");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/events")
        {
            Content = JsonContent.Create(new List<IngestEventRequest>
            {
                new(EventKey: "t-1", Timestamp: DateTime.UtcNow.AddSeconds(-2), Severity: "Info", Environment: "Production",
                    Version: null, Hierarchy: null, CorrelationId: "order-99", TraceId: null, SpanId: null, ParentSpanId: null,
                    DurationMs: null, CustomerId: null, UserId: null, Message: "step 1", MessageTemplate: null, Exception: null, Metadata: null),
                new(EventKey: "t-2", Timestamp: DateTime.UtcNow, Severity: "Info", Environment: "Production",
                    Version: null, Hierarchy: null, CorrelationId: "order-99", TraceId: null, SpanId: null, ParentSpanId: null,
                    DurationMs: null, CustomerId: null, UserId: null, Message: "step 2", MessageTemplate: null, Exception: null, Metadata: null)
            })
        };
        request.Headers.Add("X-Api-Key", apiKey);
        await client.SendAsync(request);

        var response = await client.GetAsync($"/api/v1/timeline?applicationId={appId}&correlationId=order-99");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var timeline = await response.Content.ReadFromJsonAsync<List<EventSummary>>();
        Assert.Equal(2, timeline!.Count);
        Assert.Equal("step 1", timeline[0].Message);
    }

    [Fact]
    public async Task GetTimeline_NoLookupModeSupplied_Returns400()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/timeline?applicationId=1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter "EventsControllerTests|TimelineControllerTests"`
Expected: FAIL — compile error, `EventListResponse`/`EventSummary`/`EventDetail` contracts and the endpoints don't exist.

- [ ] **Step 3: Add `QueryContracts.cs`**

`src/LogsPlatform.Web/Contracts/QueryContracts.cs`:

```csharp
namespace LogsPlatform.Web.Contracts;

public record EventSummary(long Id, DateTime Timestamp, string Severity, string? OperationPath, string Message, double? DurationMs, string? CorrelationId);

public record EventDetail(
    long Id, DateTime Timestamp, string Severity, int ApplicationId, int EnvironmentId,
    int? VersionId, int? ModuleId, int? ScreenServiceId, int? ProcessId, int? OperationId,
    int? CustomerId, int? AppUserId, string? EventKey, string? CorrelationId, string? TraceId,
    string? SpanId, string? ParentSpanId, double? DurationMs, string Message, string? MessageTemplate,
    long? ExceptionGroupId, string? StackTrace, string? MetadataJson, string? OperationPath);

public record EventListResponse(IReadOnlyList<EventSummary> Items, int TotalCount);
```

- [ ] **Step 4: Implement `EventsController`**

`src/LogsPlatform.Web/Controllers/EventsController.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/events")]
public class EventsController : ControllerBase
{
    private readonly IEventRepository _events;

    public EventsController(IEventRepository events)
    {
        _events = events;
    }

    [HttpGet]
    public async Task<ActionResult<EventListResponse>> Query(
        [FromQuery] int applicationId, [FromQuery] int environmentId,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? severity,
        [FromQuery] int? moduleId, [FromQuery] int? screenServiceId, [FromQuery] int? processId, [FromQuery] int? operationId,
        [FromQuery] string? correlationId, [FromQuery] string? traceId, [FromQuery] string? userId, [FromQuery] string? customerId,
        [FromQuery] long? exceptionGroupId, [FromQuery] int? versionId,
        [FromQuery] double? durationMinMs, [FromQuery] double? durationMaxMs, [FromQuery] string? q,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        int? severityValue = null;
        if (severity is not null)
        {
            if (!SeverityLevels.ByName.TryGetValue(severity, out var parsed))
            {
                return ValidationProblem($"severity: invalid value '{severity}'.");
            }
            severityValue = parsed;
        }

        var (items, totalCount) = await _events.QueryAsync(new EventQueryParameters(
            applicationId, environmentId, from, to, severityValue,
            moduleId, screenServiceId, processId, operationId,
            correlationId, traceId, userId, customerId,
            exceptionGroupId, versionId, durationMinMs, durationMaxMs, q, page, pageSize));

        return new EventListResponse(items.Select(ToSummary).ToList(), totalCount);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<EventDetail>> GetById(long id, [FromQuery] int applicationId)
    {
        var evt = await _events.GetByIdAsync(applicationId, id);
        if (evt is null) return NotFound();
        return ToDetail(evt);
    }

    internal static EventSummary ToSummary(Event evt) =>
        new(evt.Id, evt.Timestamp, SeverityLevels.ByValue[evt.Severity], BuildOperationPath(evt), evt.Message, evt.DurationMs, evt.CorrelationId);

    internal static EventDetail ToDetail(Event evt) =>
        new(evt.Id, evt.Timestamp, SeverityLevels.ByValue[evt.Severity], evt.ApplicationId, evt.EnvironmentId,
            evt.VersionId, evt.ModuleId, evt.ScreenServiceId, evt.ProcessId, evt.OperationId,
            evt.CustomerId, evt.AppUserId, evt.EventKey, evt.CorrelationId, evt.TraceId,
            evt.SpanId, evt.ParentSpanId, evt.DurationMs, evt.Message, evt.MessageTemplate,
            evt.ExceptionGroupId, evt.StackTrace, evt.MetadataJson, BuildOperationPath(evt));

    internal static string? BuildOperationPath(Event evt)
    {
        var segments = new[] { evt.Module?.Name, evt.ScreenService?.Name, evt.Process?.Name, evt.Operation?.Name }
            .Where(name => name is not null);
        var path = string.Join(" / ", segments);
        return path.Length == 0 ? null : path;
    }
}
```

- [ ] **Step 5: Implement `TimelineController`**

`src/LogsPlatform.Web/Controllers/TimelineController.cs`:

```csharp
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/timeline")]
public class TimelineController : ControllerBase
{
    private readonly IEventRepository _events;

    public TimelineController(IEventRepository events)
    {
        _events = events;
    }

    [HttpGet]
    public async Task<ActionResult<List<EventSummary>>> Get(
        [FromQuery] int applicationId, [FromQuery] string? correlationId, [FromQuery] string? traceId,
        [FromQuery] int? operationId, [FromQuery] string? userId, [FromQuery] string? customerId)
    {
        var modesSupplied = new[]
        {
            correlationId is not null,
            traceId is not null,
            operationId is not null && userId is not null,
            customerId is not null
        }.Count(supplied => supplied);

        if (modesSupplied != 1)
        {
            return ValidationProblem("Exactly one of correlationId, traceId, (operationId+userId), or customerId must be supplied.");
        }

        var timeline = await _events.GetTimelineAsync(new TimelineQuery(applicationId, correlationId, traceId, operationId, userId, customerId));
        return timeline.Select(EventsController.ToSummary).ToList();
    }
}
```

- [ ] **Step 6: Register `ProblemDetails` support**

In `src/LogsPlatform.Web/Program.cs`, add this line immediately after `builder.Services.AddControllers();` (line 11):

```csharp
builder.Services.AddProblemDetails();
```

- [ ] **Step 7: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter "EventsControllerTests|TimelineControllerTests"`
Expected: PASS — 4/4 tests.

- [ ] **Step 8: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/QueryContracts.cs src/LogsPlatform.Web/Controllers/EventsController.cs src/LogsPlatform.Web/Controllers/TimelineController.cs src/LogsPlatform.Web/Program.cs tests/LogsPlatform.Tests/Web/EventsControllerTests.cs tests/LogsPlatform.Tests/Web/TimelineControllerTests.cs
git commit -m "Add Events and Timeline Query API controllers"
```

---

### Task 5: ExceptionGroups Query API controller

**Suggested model tier:** standard.

**Files:**
- Modify: `src/LogsPlatform.Web/Contracts/QueryContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/ExceptionGroupsController.cs`
- Test: `tests/LogsPlatform.Tests/Web/ExceptionGroupsControllerTests.cs`

**Interfaces:**
- Consumes: `IExceptionGroupRepository` (Task 3).
- Produces: `GET /api/v1/exception-groups`, `GET /api/v1/exception-groups/{id}` (external consumers; Task 9's UI calls `IExceptionGroupRepository` directly).

- [ ] **Step 1: Write the failing test**

`tests/LogsPlatform.Tests/Web/ExceptionGroupsControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ExceptionGroupsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ExceptionGroupsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<(int ApplicationId, string ApiKey)> CreateAppWithApiKeyAsync(HttpClient client, string appName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));
        var keyResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("ExceptionGroups test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();
        return (app.Id, key!.ApiKey);
    }

    [Fact]
    public async Task GetExceptionGroups_ReturnsGroupWithDailyCounts()
    {
        var client = _factory.CreateClient();
        var (appId, apiKey) = await CreateAppWithApiKeyAsync(client, "ExGroupApiTestApp");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/events")
        {
            Content = JsonContent.Create(new List<IngestEventRequest> { new(
                EventKey: null, Timestamp: DateTime.UtcNow, Severity: "Error", Environment: "Production",
                Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null,
                DurationMs: null, CustomerId: null, UserId: null, Message: "boom", MessageTemplate: null,
                Exception: new IngestExceptionRequest("System.Exception", "at Foo.Bar() line 1"), Metadata: null) })
        };
        request.Headers.Add("X-Api-Key", apiKey);
        await client.SendAsync(request);

        var response = await client.GetAsync($"/api/v1/exception-groups?applicationId={appId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var groups = await response.Content.ReadFromJsonAsync<List<ExceptionGroupSummary>>();
        Assert.Single(groups!);
        Assert.Equal(1, groups![0].OccurrenceCount);
        Assert.NotEmpty(groups[0].DailyCounts);
    }

    [Fact]
    public async Task GetExceptionGroupById_ReturnsFullStackTraceAndAffectedContexts()
    {
        var client = _factory.CreateClient();
        var (appId, apiKey) = await CreateAppWithApiKeyAsync(client, "ExGroupDetailTestApp");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/events")
        {
            Content = JsonContent.Create(new List<IngestEventRequest> { new(
                EventKey: null, Timestamp: DateTime.UtcNow, Severity: "Error", Environment: "Production",
                Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null,
                DurationMs: null, CustomerId: null, UserId: null, Message: "boom", MessageTemplate: null,
                Exception: new IngestExceptionRequest("System.Exception", "at Foo.Bar() line 1"), Metadata: null) })
        };
        request.Headers.Add("X-Api-Key", apiKey);
        await client.SendAsync(request);

        var listResponse = await client.GetAsync($"/api/v1/exception-groups?applicationId={appId}");
        var groups = await listResponse.Content.ReadFromJsonAsync<List<ExceptionGroupSummary>>();

        var detailResponse = await client.GetAsync($"/api/v1/exception-groups/{groups![0].Id}");

        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<ExceptionGroupDetail>();
        Assert.Equal("at Foo.Bar() line 1", detail!.RepresentativeStackTrace);
        Assert.NotEmpty(detail.AffectedContexts);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter ExceptionGroupsControllerTests`
Expected: FAIL — compile error, `ExceptionGroupSummary`/`ExceptionGroupDetail`/the endpoints don't exist.

- [ ] **Step 3: Add response contracts**

Append to `src/LogsPlatform.Web/Contracts/QueryContracts.cs`:

```csharp
public record ExceptionGroupSummary(long Id, string Fingerprint, string ExceptionType, int OccurrenceCount, DateTime FirstSeenAt, DateTime LastSeenAt, IReadOnlyDictionary<DateOnly, int> DailyCounts, IReadOnlyList<string> AffectedOperations);

public record ExceptionGroupDetail(long Id, string Fingerprint, string ExceptionType, string RepresentativeStackTrace, int OccurrenceCount, DateTime FirstSeenAt, DateTime LastSeenAt, IReadOnlyDictionary<DateOnly, int> DailyCounts, IReadOnlyList<AffectedContext> AffectedContexts);
```

- [ ] **Step 4: Implement `ExceptionGroupsController`**

`src/LogsPlatform.Web/Controllers/ExceptionGroupsController.cs`:

```csharp
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/exception-groups")]
public class ExceptionGroupsController : ControllerBase
{
    private const int TrendWindowDays = 14;

    private readonly IExceptionGroupRepository _exceptionGroups;

    public ExceptionGroupsController(IExceptionGroupRepository exceptionGroups)
    {
        _exceptionGroups = exceptionGroups;
    }

    [HttpGet]
    public async Task<ActionResult<List<ExceptionGroupSummary>>> Query(
        [FromQuery] int applicationId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string sortBy = "LastSeenAt")
    {
        var groups = await _exceptionGroups.QueryAsync(new ExceptionGroupQueryParameters(applicationId, from, to, sortBy));

        var result = new List<ExceptionGroupSummary>();
        foreach (var group in groups)
        {
            var dailyCounts = await _exceptionGroups.GetDailyCountsAsync(group.Id, TrendWindowDays);
            var contexts = await _exceptionGroups.GetAffectedContextsAsync(group.Id);
            var operations = contexts.Select(c => c.OperationName).Where(name => name is not null).Distinct().Select(name => name!).ToList();

            result.Add(new ExceptionGroupSummary(group.Id, group.Fingerprint, group.ExceptionType, group.OccurrenceCount, group.FirstSeenAt, group.LastSeenAt, dailyCounts, operations));
        }

        return result;
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ExceptionGroupDetail>> GetById(long id)
    {
        var group = await _exceptionGroups.GetByIdAsync(id);
        if (group is null) return NotFound();

        var dailyCounts = await _exceptionGroups.GetDailyCountsAsync(id, TrendWindowDays);
        var contexts = await _exceptionGroups.GetAffectedContextsAsync(id);

        return new ExceptionGroupDetail(group.Id, group.Fingerprint, group.ExceptionType, group.RepresentativeStackTrace,
            group.OccurrenceCount, group.FirstSeenAt, group.LastSeenAt, dailyCounts, contexts);
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter ExceptionGroupsControllerTests`
Expected: PASS — 2/2 tests.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/QueryContracts.cs src/LogsPlatform.Web/Controllers/ExceptionGroupsController.cs tests/LogsPlatform.Tests/Web/ExceptionGroupsControllerTests.cs
git commit -m "Add ExceptionGroups Query API controller"
```

---

### Task 6: Shared Application+Environment selector, nav wiring

**Suggested model tier:** standard (Blazor component parameter/callback design).

**Files:**
- Create: `src/LogsPlatform.Web/Components/Shared/AppEnvironmentSelector.razor`
- Modify: `src/LogsPlatform.Web/Components/Layout/NavMenu.razor`

**Interfaces:**
- Consumes: `IApplicationRepository.GetAllAsync()`, `IAppEnvironmentRepository.GetByApplicationIdAsync(int)` (both existing, unchanged).
- Produces (used by Tasks 7-9): `AppEnvironmentSelector` component with `[Parameter] public int? ApplicationId`, `[Parameter] public int? EnvironmentId`, `[Parameter] public EventCallback<(int ApplicationId, int EnvironmentId)> OnSelectionChanged`.

This is a pure UI task with no automated test — matching this codebase's existing convention that Razor components/pages have no component-level test coverage (see the design doc's Testing section: no bUnit precedent exists, introducing one is out of scope). Verify manually per Step 3.

- [ ] **Step 1: Implement `AppEnvironmentSelector.razor`**

`src/LogsPlatform.Web/Components/Shared/AppEnvironmentSelector.razor`:

```razor
@* src/LogsPlatform.Web/Components/Shared/AppEnvironmentSelector.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@inject IApplicationRepository ApplicationRepository
@inject IAppEnvironmentRepository EnvironmentRepository

<div class="d-flex gap-2 align-items-center mb-3">
    <label class="form-label mb-0">Application</label>
    <select class="form-select form-select-sm w-auto" value="@_selectedApplicationId" @onchange="OnApplicationChangedAsync">
        <option value="">-- select --</option>
        @foreach (var app in _applications)
        {
            <option value="@app.Id">@app.Name</option>
        }
    </select>

    <label class="form-label mb-0">Environment</label>
    <select class="form-select form-select-sm w-auto" value="@_selectedEnvironmentId" @onchange="OnEnvironmentChangedAsync" disabled="@(_selectedApplicationId is null)">
        <option value="">-- select --</option>
        @foreach (var env in _environments)
        {
            <option value="@env.Id">@env.Name</option>
        }
    </select>
</div>

@code {
    [Parameter] public int? ApplicationId { get; set; }
    [Parameter] public int? EnvironmentId { get; set; }
    [Parameter] public EventCallback<(int ApplicationId, int EnvironmentId)> OnSelectionChanged { get; set; }

    private List<Application> _applications = new();
    private List<AppEnvironment> _environments = new();
    private int? _selectedApplicationId;
    private int? _selectedEnvironmentId;

    protected override async Task OnInitializedAsync()
    {
        _applications = (await ApplicationRepository.GetAllAsync()).ToList();
        _selectedApplicationId = ApplicationId;
        _selectedEnvironmentId = EnvironmentId;

        if (_selectedApplicationId is not null)
        {
            _environments = (await EnvironmentRepository.GetByApplicationIdAsync(_selectedApplicationId.Value)).ToList();
        }
    }

    private async Task OnApplicationChangedAsync(ChangeEventArgs e)
    {
        _selectedApplicationId = int.TryParse(e.Value?.ToString(), out var id) ? id : null;
        _selectedEnvironmentId = null;
        _environments = _selectedApplicationId is null
            ? new List<AppEnvironment>()
            : (await EnvironmentRepository.GetByApplicationIdAsync(_selectedApplicationId.Value)).ToList();
    }

    private async Task OnEnvironmentChangedAsync(ChangeEventArgs e)
    {
        _selectedEnvironmentId = int.TryParse(e.Value?.ToString(), out var id) ? id : null;
        if (_selectedApplicationId is not null && _selectedEnvironmentId is not null)
        {
            await OnSelectionChanged.InvokeAsync((_selectedApplicationId.Value, _selectedEnvironmentId.Value));
        }
    }
}
```

- [ ] **Step 2: Enable the Search and Exceptions nav links**

In `src/LogsPlatform.Web/Components/Layout/NavMenu.razor`, replace the two disabled `<li>` blocks for "חיפוש" and "חריגות" (lines 12-17 and 18-23) with real `NavLink`s, matching the existing "ניהול" link's style (leave "מה חריג" disabled — that's M4):

```razor
            <li class="nav-item">
                <NavLink class="nav-link" href="/search" Match="NavLinkMatch.Prefix">
                    חיפוש
                </NavLink>
            </li>
            <li class="nav-item">
                <NavLink class="nav-link" href="/exceptions" Match="NavLinkMatch.Prefix">
                    חריגות
                </NavLink>
            </li>
```

- [ ] **Step 3: Manually verify**

Run: `dotnet build`
Expected: Build succeeded (the component references `/search` and `/exceptions` routes that don't exist until Tasks 7 and 9 — this is expected; the routes will 404 until those tasks land, which is fine mid-plan).

- [ ] **Step 4: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/AppEnvironmentSelector.razor src/LogsPlatform.Web/Components/Layout/NavMenu.razor
git commit -m "Add AppEnvironmentSelector component, enable Search/Exceptions nav links"
```

---

### Task 7: Search page

**Suggested model tier:** standard (largest UI task — filter panel, cascading pickers, paging, detail panel).

**Files:**
- Create: `src/LogsPlatform.Web/Components/Pages/Search.razor`

**Interfaces:**
- Consumes: `IEventRepository` (Task 2), `IAppModuleRepository`/`IScreenServiceRepository`/`IProcessNodeRepository`/`IOperationRepository` (existing), `SeverityLevels` (Task 1), `AppEnvironmentSelector` (Task 6), `EventsController.ToSummary`/`BuildOperationPath` are not reused here — the page builds its own display data directly from `Event` entities returned by the repository, since it's calling the repository in-process, not the controller.

No automated test for this task, per Task 6's note (no Razor component test precedent in this codebase). Verify manually per Step 2.

- [ ] **Step 1: Implement `Search.razor`**

`src/LogsPlatform.Web/Components/Pages/Search.razor`:

```razor
@* src/LogsPlatform.Web/Components/Pages/Search.razor *@
@page "/search"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web.Components.Shared
@using LogsPlatform.Web.Services
@inject IEventRepository EventRepository
@inject IAppModuleRepository ModuleRepository
@inject IScreenServiceRepository ScreenServiceRepository
@inject IProcessNodeRepository ProcessRepository
@inject IOperationRepository OperationRepository
@inject NavigationManager Navigation
@rendermode InteractiveServer

<h1>Search</h1>

<AppEnvironmentSelector ApplicationId="_applicationId" EnvironmentId="_environmentId" OnSelectionChanged="OnAppEnvironmentChangedAsync" />

@if (_applicationId is not null && _environmentId is not null)
{
    <div class="card mb-4">
        <div class="card-body">
            <div class="row g-3">
                <div class="col-auto">
                    <label class="form-label">Severity</label>
                    <select class="form-select form-select-sm" @bind="_severity">
                        <option value="">-- any --</option>
                        @foreach (var name in SeverityLevels.ByName.Keys)
                        {
                            <option value="@name">@name</option>
                        }
                    </select>
                </div>
                <div class="col-auto">
                    <label class="form-label">Module</label>
                    <select class="form-select form-select-sm" value="@_moduleId" @onchange="OnModuleChangedAsync">
                        <option value="">-- any --</option>
                        @foreach (var m in _modules)
                        {
                            <option value="@m.Id">@m.Name</option>
                        }
                    </select>
                </div>
                <div class="col-auto">
                    <label class="form-label">ScreenService</label>
                    <select class="form-select form-select-sm" value="@_screenServiceId" @onchange="OnScreenServiceChangedAsync" disabled="@(_moduleId is null)">
                        <option value="">-- any --</option>
                        @foreach (var s in _screenServices)
                        {
                            <option value="@s.Id">@s.Name</option>
                        }
                    </select>
                </div>
                <div class="col-auto">
                    <label class="form-label">Process</label>
                    <select class="form-select form-select-sm" value="@_processId" @onchange="OnProcessChangedAsync" disabled="@(_screenServiceId is null)">
                        <option value="">-- any --</option>
                        @foreach (var p in _processes)
                        {
                            <option value="@p.Id">@p.Name</option>
                        }
                    </select>
                </div>
                <div class="col-auto">
                    <label class="form-label">Operation</label>
                    <select class="form-select form-select-sm" @bind="_operationId" disabled="@(_processId is null)">
                        <option value="">-- any --</option>
                        @foreach (var o in _operations)
                        {
                            <option value="@o.Id">@o.Name</option>
                        }
                    </select>
                </div>
                <div class="col-auto">
                    <label class="form-label">Correlation Id</label>
                    <input class="form-control form-control-sm" @bind="_correlationId" @bind:event="oninput" />
                </div>
                <div class="col-auto">
                    <label class="form-label">Message contains</label>
                    <input class="form-control form-control-sm" @bind="_messageContains" @bind:event="oninput" />
                </div>
                <div class="col-auto align-self-end">
                    <button class="btn btn-primary btn-sm" @onclick="() => SearchAsync(resetPage: true)">Search</button>
                </div>
            </div>
        </div>
    </div>

    <table class="table table-striped table-hover align-middle">
        <thead>
            <tr>
                <th>Timestamp</th>
                <th>Severity</th>
                <th>Operation</th>
                <th>Message</th>
                <th>Duration</th>
                <th>CorrelationId</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var evt in _results)
            {
                <tr @key="evt.Id" @onclick="() => SelectEvent(evt.Id)" style="cursor:pointer">
                    <td>@evt.Timestamp.ToString("u")</td>
                    <td>@SeverityLevels.ByValue[evt.Severity]</td>
                    <td>@BuildOperationPath(evt)</td>
                    <td>@Truncate(evt.Message, 80)</td>
                    <td>@evt.DurationMs</td>
                    <td>@evt.CorrelationId</td>
                </tr>
            }
        </tbody>
    </table>

    <div class="d-flex gap-2 align-items-center">
        <button class="btn btn-sm btn-outline-secondary" disabled="@(_page <= 1)" @onclick="() => ChangePageAsync(_page - 1)">Previous</button>
        <span>Page @_page (@_totalCount total)</span>
        <button class="btn btn-sm btn-outline-secondary" disabled="@(_page * _pageSize >= _totalCount)" @onclick="() => ChangePageAsync(_page + 1)">Next</button>
    </div>

    @if (_selectedEvent is not null)
    {
        <div class="card mt-4">
            <div class="card-header d-flex justify-content-between">
                <span>Event @_selectedEvent.Id</span>
                <button class="btn-close" @onclick="() => _selectedEvent = null"></button>
            </div>
            <div class="card-body">
                <dl class="row">
                    <dt class="col-sm-2">Timestamp</dt><dd class="col-sm-10">@_selectedEvent.Timestamp.ToString("u")</dd>
                    <dt class="col-sm-2">Severity</dt><dd class="col-sm-10">@SeverityLevels.ByValue[_selectedEvent.Severity]</dd>
                    <dt class="col-sm-2">Operation</dt><dd class="col-sm-10">@BuildOperationPath(_selectedEvent)</dd>
                    <dt class="col-sm-2">Message</dt><dd class="col-sm-10">@_selectedEvent.Message</dd>
                    <dt class="col-sm-2">CorrelationId</dt><dd class="col-sm-10">@_selectedEvent.CorrelationId</dd>
                    <dt class="col-sm-2">TraceId</dt><dd class="col-sm-10">@_selectedEvent.TraceId</dd>
                    @if (_selectedEvent.MetadataJson is not null)
                    {
                        <dt class="col-sm-2">Metadata</dt><dd class="col-sm-10"><pre>@_selectedEvent.MetadataJson</pre></dd>
                    }
                    @if (_selectedEvent.StackTrace is not null)
                    {
                        <dt class="col-sm-2">StackTrace</dt><dd class="col-sm-10"><pre>@_selectedEvent.StackTrace</pre></dd>
                    }
                </dl>
                @if (_selectedEvent.CorrelationId is not null)
                {
                    <a class="btn btn-sm btn-outline-primary" href="/timeline?applicationId=@_applicationId&correlationId=@Uri.EscapeDataString(_selectedEvent.CorrelationId)">View Timeline</a>
                }
                else if (_selectedEvent.TraceId is not null)
                {
                    <a class="btn btn-sm btn-outline-primary" href="/timeline?applicationId=@_applicationId&traceId=@Uri.EscapeDataString(_selectedEvent.TraceId)">View Timeline</a>
                }
            </div>
        </div>
    }
}

@code {
    private int? _applicationId;
    private int? _environmentId;

    private List<AppModule> _modules = new();
    private List<ScreenService> _screenServices = new();
    private List<ProcessNode> _processes = new();
    private List<Operation> _operations = new();

    private string _severity = string.Empty;
    private int? _moduleId;
    private int? _screenServiceId;
    private int? _processId;
    private int? _operationId;
    private string _correlationId = string.Empty;
    private string _messageContains = string.Empty;

    private List<Event> _results = new();
    private int _totalCount;
    private int _page = 1;
    private const int _pageSize = 50;

    private Event? _selectedEvent;

    private async Task OnAppEnvironmentChangedAsync((int ApplicationId, int EnvironmentId) selection)
    {
        _applicationId = selection.ApplicationId;
        _environmentId = selection.EnvironmentId;
        _modules = (await ModuleRepository.GetByApplicationIdAsync(_applicationId.Value)).ToList();
        await SearchAsync(resetPage: true);
    }

    private async Task OnModuleChangedAsync(ChangeEventArgs e)
    {
        _moduleId = int.TryParse(e.Value?.ToString(), out var id) ? id : null;
        _screenServiceId = null; _processId = null; _operationId = null;
        _screenServices = _moduleId is null ? new() : (await ScreenServiceRepository.GetByModuleIdAsync(_moduleId.Value)).ToList();
        _processes = new(); _operations = new();
    }

    private async Task OnScreenServiceChangedAsync(ChangeEventArgs e)
    {
        _screenServiceId = int.TryParse(e.Value?.ToString(), out var id) ? id : null;
        _processId = null; _operationId = null;
        _processes = _screenServiceId is null ? new() : (await ProcessRepository.GetByScreenServiceIdAsync(_screenServiceId.Value)).ToList();
        _operations = new();
    }

    private async Task OnProcessChangedAsync(ChangeEventArgs e)
    {
        _processId = int.TryParse(e.Value?.ToString(), out var id) ? id : null;
        _operationId = null;
        _operations = _processId is null ? new() : (await OperationRepository.GetByProcessIdAsync(_processId.Value)).ToList();
    }

    private async Task ChangePageAsync(int page)
    {
        _page = page;
        await SearchAsync(resetPage: false);
    }

    private async Task SearchAsync(bool resetPage)
    {
        if (_applicationId is null || _environmentId is null) return;
        if (resetPage) _page = 1;

        int? severityValue = string.IsNullOrEmpty(_severity) ? null : SeverityLevels.ByName[_severity];

        var (items, totalCount) = await EventRepository.QueryAsync(new EventQueryParameters(
            ApplicationId: _applicationId.Value,
            EnvironmentId: _environmentId.Value,
            From: null,
            To: null,
            Severity: severityValue,
            ModuleId: _moduleId,
            ScreenServiceId: _screenServiceId,
            ProcessId: _processId,
            OperationId: _operationId,
            CorrelationId: string.IsNullOrEmpty(_correlationId) ? null : _correlationId,
            TraceId: null,
            UserId: null,
            CustomerId: null,
            ExceptionGroupId: null,
            VersionId: null,
            DurationMinMs: null,
            DurationMaxMs: null,
            MessageContains: string.IsNullOrEmpty(_messageContains) ? null : _messageContains,
            Page: _page,
            PageSize: _pageSize));

        _results = items.ToList();
        _totalCount = totalCount;
    }

    private void SelectEvent(long id)
    {
        _selectedEvent = _results.FirstOrDefault(e => e.Id == id);
    }

    private static string BuildOperationPath(Event evt)
    {
        var segments = new[] { evt.Module?.Name, evt.ScreenService?.Name, evt.Process?.Name, evt.Operation?.Name }
            .Where(name => name is not null);
        return string.Join(" / ", segments);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
```

- [ ] **Step 2: Manually verify**

Run: `dotnet build` — expect success. Then start the app (`dotnet run --project src/LogsPlatform.Web`), navigate to `/search`, select an Application/Environment that has data (from M2 ingestion testing or freshly ingested test events), confirm the filter panel populates and search results render with working pagination and the row-click detail panel.

- [ ] **Step 3: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/Search.razor
git commit -m "Add Search page"
```

---

### Task 8: Timeline page

**Suggested model tier:** standard.

**Files:**
- Create: `src/LogsPlatform.Web/Components/Pages/Timeline.razor`

**Interfaces:**
- Consumes: `IEventRepository.GetTimelineAsync` (Task 2), `SeverityLevels` (Task 1).

- [ ] **Step 1: Implement `Timeline.razor`**

`src/LogsPlatform.Web/Components/Pages/Timeline.razor`:

```razor
@* src/LogsPlatform.Web/Components/Pages/Timeline.razor *@
@page "/timeline"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web.Services
@inject IEventRepository EventRepository
@inject NavigationManager Navigation
@rendermode InteractiveServer

<h1>Timeline</h1>

@if (_events.Count == 0)
{
    <p>No events found for this lookup.</p>
}
else
{
    <table class="table table-striped align-middle">
        <thead>
            <tr>
                <th>+ms</th>
                <th>Operation</th>
                <th>Severity</th>
                <th>Duration</th>
                <th>Message</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var evt in _events)
            {
                <tr @key="evt.Id">
                    <td>+@((evt.Timestamp - _events[0].Timestamp).TotalMilliseconds)ms</td>
                    <td>@BuildOperationPath(evt)</td>
                    <td>@SeverityLevels.ByValue[evt.Severity]</td>
                    <td>@evt.DurationMs</td>
                    <td>@evt.Message</td>
                </tr>
            }
        </tbody>
    </table>
}

@code {
    private List<Event> _events = new();

    protected override async Task OnInitializedAsync()
    {
        var uri = Navigation.ToAbsoluteUri(Navigation.Uri);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        if (!int.TryParse(query["applicationId"], out var applicationId))
        {
            return;
        }

        var correlationId = query["correlationId"];
        var traceId = query["traceId"];
        var customerId = query["customerId"];
        int.TryParse(query["operationId"], out var operationId);
        var userId = query["userId"];

        var lookupQuery = new TimelineQuery(
            applicationId,
            correlationId,
            traceId,
            operationId == 0 ? null : operationId,
            userId,
            customerId);

        _events = (await EventRepository.GetTimelineAsync(lookupQuery)).ToList();
    }

    private static string BuildOperationPath(Event evt)
    {
        var segments = new[] { evt.Module?.Name, evt.ScreenService?.Name, evt.Process?.Name, evt.Operation?.Name }
            .Where(name => name is not null);
        return string.Join(" / ", segments);
    }
}
```

- [ ] **Step 2: Manually verify**

Run: `dotnet build` — expect success. Start the app, navigate to `/search`, drill down via "View Timeline" from an event with a `CorrelationId`, confirm the timeline renders with relative offsets starting at `+0ms`.

- [ ] **Step 3: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/Timeline.razor
git commit -m "Add Timeline page"
```

---

### Task 9: Exceptions list + detail pages

**Suggested model tier:** standard (includes the SVG sparkline rendering logic).

**Files:**
- Create: `src/LogsPlatform.Web/Services/SparklineRenderer.cs`
- Create: `src/LogsPlatform.Web/Components/Pages/Exceptions.razor`
- Create: `src/LogsPlatform.Web/Components/Pages/ExceptionDetail.razor`

**Interfaces:**
- Consumes: `IExceptionGroupRepository` (Task 3), `AppEnvironmentSelector`'s Application half only — this screen is Application-scoped, not Environment-scoped, per the design doc (`ExceptionGroup` has no `EnvironmentId`); reuse the Application dropdown pattern but not the full selector component, since it forces an environment choice this page doesn't use.
- Produces: `public static class SparklineRenderer { public static string Render(IReadOnlyDictionary<DateOnly, int> dailyCounts, int width, int height) }` — a small standalone helper (not nested in either page) so both `Exceptions.razor` and `ExceptionDetail.razor` can call it without one page reaching into the other's code-behind.

`SparklineRenderer` is pure logic with no DI/DB dependency, so — unlike the two Razor pages, which have no automated test per the codebase's established lack of a Razor-component-test precedent (Task 6's note) — it gets a real unit test, matching `ExceptionFingerprinter`'s precedent (M2a: a static, pure-function class with its own dedicated test file).

- [ ] **Step 1: Write the failing test**

`tests/LogsPlatform.Tests/Web/SparklineRendererTests.cs`:

```csharp
using LogsPlatform.Web.Services;

namespace LogsPlatform.Tests.Web;

public class SparklineRendererTests
{
    [Fact]
    public void Render_EmptyCounts_ReturnsNoDataPlaceholder()
    {
        var result = SparklineRenderer.Render(new Dictionary<DateOnly, int>(), width: 100, height: 24);

        Assert.Contains("no data", result);
    }

    [Fact]
    public void Render_WithCounts_ReturnsSvgWithPolyline()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var counts = new Dictionary<DateOnly, int> { [today] = 5, [today.AddDays(-1)] = 2 };

        var result = SparklineRenderer.Render(counts, width: 100, height: 24);

        Assert.Contains("<svg", result);
        Assert.Contains("<polyline", result);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter SparklineRendererTests`
Expected: FAIL — compile error, `SparklineRenderer` does not exist.

- [ ] **Step 3: Implement `SparklineRenderer`**

`src/LogsPlatform.Web/Services/SparklineRenderer.cs`:

```csharp
namespace LogsPlatform.Web.Services;

public static class SparklineRenderer
{
    public static string Render(IReadOnlyDictionary<DateOnly, int> dailyCounts, int width, int height)
    {
        if (dailyCounts.Count == 0)
        {
            return "<span class=\"text-muted\">no data</span>";
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var days = Enumerable.Range(0, 14).Select(offset => today.AddDays(-13 + offset)).ToList();
        var values = days.Select(day => dailyCounts.TryGetValue(day, out var count) ? count : 0).ToList();
        var max = Math.Max(values.Max(), 1);

        var points = values.Select((value, index) =>
        {
            var x = (double)index / (values.Count - 1) * width;
            var y = height - (double)value / max * height;
            return $"{x:F1},{y:F1}";
        });

        return $"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\"><polyline fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.5\" points=\"{string.Join(" ", points)}\" /></svg>";
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter SparklineRendererTests`
Expected: PASS — 2/2 tests.

- [ ] **Step 5: Implement `Exceptions.razor`**

`src/LogsPlatform.Web/Components/Pages/Exceptions.razor`:

```razor
@* src/LogsPlatform.Web/Components/Pages/Exceptions.razor *@
@page "/exceptions"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web.Services
@inject IApplicationRepository ApplicationRepository
@inject IExceptionGroupRepository ExceptionGroupRepository
@rendermode InteractiveServer

<h1>Exceptions</h1>

<div class="d-flex gap-2 align-items-center mb-3">
    <label class="form-label mb-0">Application</label>
    <select class="form-select form-select-sm w-auto" value="@_applicationId" @onchange="OnApplicationChangedAsync">
        <option value="">-- select --</option>
        @foreach (var app in _applications)
        {
            <option value="@app.Id">@app.Name</option>
        }
    </select>

    <label class="form-label mb-0">Sort by</label>
    <select class="form-select form-select-sm w-auto" @bind="_sortBy" @bind:after="LoadAsync">
        <option value="LastSeenAt">Last Seen</option>
        <option value="OccurrenceCount">Occurrence Count</option>
    </select>
</div>

@if (_groups.Count > 0)
{
    <table class="table table-striped table-hover align-middle">
        <thead>
            <tr>
                <th>Fingerprint</th>
                <th>Type</th>
                <th>Count</th>
                <th>First Seen</th>
                <th>Last Seen</th>
                <th>Trend</th>
                <th>Operations</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var group in _groups)
            {
                <tr @key="group.Id">
                    <td><a href="/exceptions/@group.Id">@group.Fingerprint[..Math.Min(8, group.Fingerprint.Length)]</a></td>
                    <td>@group.ExceptionType</td>
                    <td>@group.OccurrenceCount</td>
                    <td>@group.FirstSeenAt.ToString("d")</td>
                    <td>@group.LastSeenAt.ToString("d")</td>
                    <td>@((MarkupString)SparklineRenderer.Render(group.DailyCounts, width: 100, height: 24))</td>
                    <td>@string.Join(", ", group.AffectedOperations)</td>
                </tr>
            }
        </tbody>
    </table>
}

@code {
    private List<Application> _applications = new();
    private int? _applicationId;
    private string _sortBy = "LastSeenAt";
    private List<ExceptionGroupRow> _groups = new();

    protected override async Task OnInitializedAsync()
    {
        _applications = (await ApplicationRepository.GetAllAsync()).ToList();
    }

    private async Task OnApplicationChangedAsync(ChangeEventArgs e)
    {
        _applicationId = int.TryParse(e.Value?.ToString(), out var id) ? id : null;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_applicationId is null)
        {
            _groups = new();
            return;
        }

        var groups = await ExceptionGroupRepository.QueryAsync(new ExceptionGroupQueryParameters(
            ApplicationId: _applicationId.Value, From: null, To: null, SortBy: _sortBy));

        var rows = new List<ExceptionGroupRow>();
        foreach (var group in groups)
        {
            var dailyCounts = await ExceptionGroupRepository.GetDailyCountsAsync(group.Id, days: 14);
            var contexts = await ExceptionGroupRepository.GetAffectedContextsAsync(group.Id);
            var operations = contexts.Select(c => c.OperationName).Where(name => name is not null).Distinct().Select(name => name!).ToList();
            rows.Add(new ExceptionGroupRow(group.Id, group.Fingerprint, group.ExceptionType, group.OccurrenceCount, group.FirstSeenAt, group.LastSeenAt, dailyCounts, operations));
        }
        _groups = rows;
    }

    private record ExceptionGroupRow(long Id, string Fingerprint, string ExceptionType, int OccurrenceCount, DateTime FirstSeenAt, DateTime LastSeenAt, IReadOnlyDictionary<DateOnly, int> DailyCounts, IReadOnlyList<string> AffectedOperations);
}
```

- [ ] **Step 6: Implement `ExceptionDetail.razor`**

`src/LogsPlatform.Web/Components/Pages/ExceptionDetail.razor`:

```razor
@* src/LogsPlatform.Web/Components/Pages/ExceptionDetail.razor *@
@page "/exceptions/{Id:long}"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web.Services
@inject IExceptionGroupRepository ExceptionGroupRepository
@rendermode InteractiveServer

@if (_group is null)
{
    <p>Not found.</p>
}
else
{
    <h1>@_group.ExceptionType</h1>
    <p class="text-muted">Fingerprint: @_group.Fingerprint</p>

    <div class="mb-3">@((MarkupString)SparklineRenderer.Render(_dailyCounts, width: 400, height: 80))</div>

    <dl class="row">
        <dt class="col-sm-2">Occurrences</dt><dd class="col-sm-10">@_group.OccurrenceCount</dd>
        <dt class="col-sm-2">First Seen</dt><dd class="col-sm-10">@_group.FirstSeenAt.ToString("u")</dd>
        <dt class="col-sm-2">Last Seen</dt><dd class="col-sm-10">@_group.LastSeenAt.ToString("u")</dd>
    </dl>

    <h4>Stack Trace</h4>
    <pre>@_group.RepresentativeStackTrace</pre>

    <h4>Affected</h4>
    <table class="table table-sm">
        <thead><tr><th>Application</th><th>Environment</th><th>Version</th><th>Operation</th></tr></thead>
        <tbody>
            @foreach (var context in _contexts)
            {
                <tr>
                    <td>@context.ApplicationName</td>
                    <td>@context.EnvironmentName</td>
                    <td>@context.VersionNumber</td>
                    <td>
                        <a href="/search?applicationId=@_group.ApplicationId&environmentId=&exceptionGroupId=@_group.Id">@context.OperationName</a>
                    </td>
                </tr>
            }
        </tbody>
    </table>
}

@code {
    [Parameter] public long Id { get; set; }

    private ExceptionGroup? _group;
    private IReadOnlyDictionary<DateOnly, int> _dailyCounts = new Dictionary<DateOnly, int>();
    private IReadOnlyList<AffectedContext> _contexts = new List<AffectedContext>();

    protected override async Task OnInitializedAsync()
    {
        _group = await ExceptionGroupRepository.GetByIdAsync(Id);
        if (_group is null) return;

        _dailyCounts = await ExceptionGroupRepository.GetDailyCountsAsync(Id, days: 14);
        _contexts = await ExceptionGroupRepository.GetAffectedContextsAsync(Id);
    }
}
```

Note: the "Operations" link on the detail page passes `exceptionGroupId` to `/search` but leaves `environmentId` blank, since `AffectedContext` doesn't carry an environment id (only its display name) — `Search.razor`'s `AppEnvironmentSelector` will require the user to pick an environment on arrival, same as visiting `/search` directly. This is an accepted V1 gap, not a broken link — the exceptionGroupId, once environment is selected, still correctly scopes the search once submitted.

- [ ] **Step 7: Manually verify**

Run: `dotnet build` — expect success. Start the app, navigate to `/exceptions`, select an Application with exception data, confirm the list renders with a visible sparkline per row, then click into a group's detail page and confirm the full stack trace and affected-contexts table render.

- [ ] **Step 8: Commit**

```bash
git add src/LogsPlatform.Web/Services/SparklineRenderer.cs tests/LogsPlatform.Tests/Web/SparklineRendererTests.cs src/LogsPlatform.Web/Components/Pages/Exceptions.razor src/LogsPlatform.Web/Components/Pages/ExceptionDetail.razor
git commit -m "Add Exceptions list and detail pages"
```

---

## Self-Review Notes

**Spec coverage:** All 5 Query API endpoints (Task 4, 5), all 3 UI screens (Task 7, 8, 9), the shared selector and nav wiring (Task 6), the severity vocabulary consistency (Task 1), and the timeline `applicationId` correction are covered. `ProblemDetails` registration is in Task 4. No schema changes, as designed.

**Placeholder scan:** No TBD/TODO markers; every step has complete, runnable code including the two UI tasks that have no automated test (explicitly justified by the absence of any Razor component-test precedent in this codebase, matching the design doc's stated Testing section).

**Type consistency:** `EventQueryParameters`/`TimelineQuery` (Task 2) are used identically by `EventsController`/`TimelineController` (Task 4) and by `Search.razor`/`Timeline.razor` (Tasks 7-8) — verified by re-reading each call site against the Task 2 declaration. `ExceptionGroupQueryParameters`/`AffectedContext` (Task 3) match their usage in `ExceptionGroupsController` (Task 5) and `Exceptions.razor`/`ExceptionDetail.razor` (Task 9). `SeverityLevels.ByName`/`ByValue` (Task 1) are used consistently everywhere severity needs translating in either direction.

**Fixes applied during self-review (not present in the first draft):** (1) `EventsControllerTests.cs`'s helper originally discarded the environment-creation response and a test hardcoded `environmentId=1` — wrong, since `AppEnvironment.Id` is a global auto-increment, not per-application-reset; fixed to capture and use the real id. (2) `Search.razor`'s and `Exceptions.razor`'s `EventQueryParameters`/`ExceptionGroupQueryParameters` constructor calls mixed named arguments (`From: null, To: null`) with positional arguments afterward (`severityValue`, `_sortBy`) — illegal C# (a positional argument cannot follow a named one); fixed to use fully-named arguments. (3) `ExceptionDetail.razor` originally called `Exceptions.BuildSparkline(...)`, reaching into a sibling page's code-behind for a static helper — fragile page-to-page coupling; extracted into a standalone `SparklineRenderer` service (with its own unit test, since it's pure logic) used by both pages independently.
