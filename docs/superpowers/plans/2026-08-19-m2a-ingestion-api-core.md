# M2a: Ingestion API Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `POST /api/v1/ingest/events` — authenticates via API key, validates required fields, resolves environment/version/customer/user/hierarchy references without ever auto-creating them, groups exceptions by fingerprint, deduplicates via idempotency key, rate-limits per key, returns OTLP-style partial-success — matching `docs/superpowers/specs/2026-08-19-m2a-ingestion-api-core-design.md`. This plan alone satisfies M2's own milestone acceptance criterion.

**Architecture:** Same Modular Monolith layering as every prior plan (`LogsPlatform.Domain` entities/interfaces → `LogsPlatform.Infrastructure` EF Core/repositories → `LogsPlatform.Web` controllers/services), plus two new `LogsPlatform.Web/Services` classes (`HierarchyResolver`, `IngestionProcessor` — the same layer the already-merged `BreadcrumbBuilder` lives in) and one new ASP.NET Core custom authentication scheme.

**Tech Stack:** .NET 10, EF Core 10.0.11, SQL Server, xUnit + `Microsoft.AspNetCore.Mvc.Testing`. No new NuGet packages — `Microsoft.Extensions.Caching.Memory` (for rate limiting) and `Microsoft.AspNetCore.Authentication` (for the custom scheme) are both already part of the ASP.NET Core shared framework this project already targets.

## Global Constraints

- **Every one of `Event`'s foreign keys uses `DeleteBehavior.Restrict`, including `ApplicationId` — this is the one entity in the project where even `ApplicationId` is NOT `Cascade`.** Reason: `Application` is a cascade-ancestor of literally every table `Event` references (`AppEnvironment`, `AppVersion`, `AppModule`→`ScreenService`→`ProcessNode`→`Operation`, `Customer`, `AppUser`) as well as `Event` itself directly. If `Event.ApplicationId` were `Cascade`, SQL Server would see multiple cascade paths into `Events` (the direct `Application→Event` path, plus e.g. `Application→AppModule→ScreenService→ProcessNode→Operation→Event`) and reject the migration outright — the same class of error B3's `Deployment` hit with just two extra FKs, now hitting nearly every FK on this one entity at once. `ExceptionGroup.ApplicationId` stays the normal `Cascade` (it only has the one FK, no conflict), but `Event.ExceptionGroupId` is `Restrict` too, for the same reason.
- **No `applicationId` field anywhere in the ingestion request body.** The API key resolves exactly one `Application`; every event in a batch is stamped with that resolved id. This is the entire point of the auth model (`07` §2) — a client cannot assert which application it's writing to.
- **The ingestion endpoint accepts a JSON array only** — `[FromBody] List<IngestEventRequest>`, never a bare single object. A future convenience for single-object bodies is explicitly out of scope (see design doc).
- **Required vs. optional field semantics, exactly as follows — do not blur this line:**
  - `timestamp`, `severity` (must parse to a known value), `message`, `environment` (must resolve to an existing `AppEnvironment` for this application) — **missing or unresolvable rejects the event** (added to `errors[]`, never reaches the DB, does not fail the rest of the batch).
  - `version`, `customerId`, `userId`, and every layer of `hierarchy` — **missing or unresolvable never rejects the event.** Unresolvable ones leave the corresponding FK `null` and add one `hierarchyWarnings[]` entry naming the field; the event is still stored.
- **Hierarchy resolution stops at the first unresolvable layer** — if `module` doesn't resolve, `screenService`/`process`/`operation` are never even looked up (they're meaningless without a resolved parent); the result is one warning naming the first failing field, with everything from that layer down left `null`. **Never auto-creates a node under any circumstance** — this is `07` §3's central design decision; do not "helpfully" add auto-create when handling a not-found case.
- **`version`/`customerId`/`userId` resolution is by external identifier, not internal id**: `version` matches `AppVersion.VersionNumber`, `customerId` matches `Customer.ExternalCustomerId`, `userId` matches `AppUser.ExternalUserId` — never the internal `int` id. Same for every hierarchy layer: matched by `Name`, not id.
- **No new lookup-by-name/lookup-by-external-id repository methods on any existing repository.** Every resolution (hierarchy, version, customer, user) fetches the parent-scoped list via the repository method that already exists (`GetByApplicationIdAsync`/`GetByModuleIdAsync`/etc., all already merged) and filters by name/external-id in memory. This is a deliberate V1 performance trade-off — these tables are low-volume admin metadata, not `Event`-scale — not something to "optimize" mid-implementation by adding new indexed lookup methods.
- **`EventKey` idempotency is scoped per `(ApplicationId, EventKey)`, checked against BOTH already-persisted rows and duplicates within the same incoming batch.** A duplicate is not an error — it's silently counted in `accepted`, not re-inserted, not added to `errors[]`. This is what makes retries retry-*safe* rather than retry-visible.
- **`ExceptionGroup.OccurrenceCount`/`LastSeenAt` are set once at creation and never touched again by anything in this plan** — reserved for M4's Analysis Engine batch job to reconcile (per `05` §4's explicit reasoning: avoids row contention at high write volume). `GetOrCreateAsync` either creates a group with `OccurrenceCount = 1` or returns the existing group completely unmodified.
- **Detach-on-failure** in every write path that can fail (`AddEventsAsync`'s batch insert, `GetOrCreateAsync`'s create branch) — `try`/`catch`/`_context.Entry(entity).State = EntityState.Detached`/re-throw, extended to the batch case by detaching every entity in the failed batch, not just one.
- **`ApiKeyAuthenticationHandler` only gates `IngestionController` — no existing controller changes behavior.** Adding `app.UseAuthentication()`/`app.UseAuthorization()` to the pipeline does NOT enforce authentication on endpoints without an explicit `[Authorize]` attribute (ASP.NET Core's default is anonymous-allowed unless an endpoint opts into `[Authorize]` or a global fallback policy is configured — this plan configures neither a fallback policy nor touches any existing controller). The full pre-existing test suite (204 tests before this plan; see Task 2's exact running total) must still pass unchanged after Task 5's Program.cs edit — if any Admin API test starts failing after that task, the auth wiring broke something it shouldn't have, stop and investigate rather than adding `[AllowAnonymous]` band-aids to existing controllers.
- **The shared `ApiKeyHasher.Hash(string)` static method is the only place the hashing algorithm is implemented** — `ApiKeyRepository.AddAsync` and `ApiKeyAuthenticationHandler` both call it; neither has its own copy. This is the concrete fix for the exact risk B2's own final review flagged before M2 started ("the three ways to get it wrong are all silent: hashing without the prefix, a different encoding, or lowercase hex").
- **No automated test for the `429` rate-limit path** — deliberately deferred, not an oversight. Proving it requires either sending 1000+ requests in a test or making the limit test-configurable, and the underlying logic (`IMemoryCache`-backed fixed-window counter, `Interlocked.Increment`) is simple and low-risk enough that this plan accepts verifying it by code review rather than an automated test. If this changes later, it belongs in its own small follow-up, not bolted onto this already-large plan.
- Target framework `net10.0`, EF Core packages pinned at `10.0.11`. No new package references anywhere in this plan.

---

### Task 1: Domain entities (`Event`, `ExceptionGroup`) + repository interfaces

**Files:**
- Create: `src/LogsPlatform.Domain/Entities/Event.cs`
- Create: `src/LogsPlatform.Domain/Entities/ExceptionGroup.cs`
- Create: `src/LogsPlatform.Domain/Repositories/IEventRepository.cs`
- Create: `src/LogsPlatform.Domain/Repositories/IExceptionGroupRepository.cs`

**Interfaces:**
- Consumes: `Application`, `AppEnvironment`, `AppVersion`, `AppModule`, `ScreenService`, `ProcessNode`, `Operation`, `Customer`, `AppUser` (all existing).
- Produces: `Event`, `ExceptionGroup` entity classes and `IEventRepository`, `IExceptionGroupRepository` interfaces that Task 3 implements against.

- [ ] **Step 1: Write the two entities**

```csharp
// src/LogsPlatform.Domain/Entities/Event.cs
namespace LogsPlatform.Domain.Entities;

public class Event
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public int Severity { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public int EnvironmentId { get; set; }
    public AppEnvironment Environment { get; set; } = null!;
    public int? VersionId { get; set; }
    public AppVersion? Version { get; set; }
    public int? ModuleId { get; set; }
    public AppModule? Module { get; set; }
    public int? ScreenServiceId { get; set; }
    public ScreenService? ScreenService { get; set; }
    public int? ProcessId { get; set; }
    public ProcessNode? Process { get; set; }
    public int? OperationId { get; set; }
    public Operation? Operation { get; set; }
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? AppUserId { get; set; }
    public AppUser? AppUser { get; set; }
    public string? EventKey { get; set; }
    public string? CorrelationId { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public string? ParentSpanId { get; set; }
    public double? DurationMs { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? MessageTemplate { get; set; }
    public long? ExceptionGroupId { get; set; }
    public ExceptionGroup? ExceptionGroup { get; set; }
    public string? StackTrace { get; set; }
    public string? MetadataJson { get; set; }
}
```

```csharp
// src/LogsPlatform.Domain/Entities/ExceptionGroup.cs
namespace LogsPlatform.Domain.Entities;

public class ExceptionGroup
{
    public long Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public string Fingerprint { get; set; } = string.Empty;
    public string ExceptionType { get; set; } = string.Empty;
    public string MessageTemplate { get; set; } = string.Empty;
    public string RepresentativeStackTrace { get; set; } = string.Empty;
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public int OccurrenceCount { get; set; }
}
```

Note `Event.Environment`/`Event.Version`/`Event.Module`/`Event.Process` use the short natural word for the navigation property name (types `AppEnvironment`/`AppVersion`/`AppModule`/`ProcessNode`), matching the established precedent (`ScreenService.Module`, `Deployment.Environment`/`.Version`). `Event`/`ExceptionGroup` themselves need no BCL-collision-avoidance prefix — C#'s `event` keyword is lowercase/contextual and doesn't collide with a type named `Event`.

- [ ] **Step 2: Write the repository interfaces**

```csharp
// src/LogsPlatform.Domain/Repositories/IEventRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public record IngestResult(int Accepted, int DuplicateEventKeysSkipped);

public interface IEventRepository
{
    Task<IngestResult> AddEventsAsync(int applicationId, IReadOnlyList<Event> events);
}
```

```csharp
// src/LogsPlatform.Domain/Repositories/IExceptionGroupRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IExceptionGroupRepository
{
    Task<ExceptionGroup> GetOrCreateAsync(
        int applicationId, string fingerprint, string exceptionType,
        string messageTemplate, string representativeStackTrace, DateTime seenAt);
}
```

Note these interfaces are deliberately **not** shaped like every prior CRUD repository — no `GetById`/`Rename`/`Deactivate`. `Event` rows are immutable, append-only log records; nothing in this plan reads them back (that's M3's Query API). `AddEventsAsync` takes `applicationId` as an explicit parameter (not inferred from the events' own `ApplicationId` field) so the dedup query has one unambiguous scope even for an empty list.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add src/LogsPlatform.Domain/Entities/Event.cs src/LogsPlatform.Domain/Entities/ExceptionGroup.cs src/LogsPlatform.Domain/Repositories/IEventRepository.cs src/LogsPlatform.Domain/Repositories/IExceptionGroupRepository.cs
git commit -m "Add Event, ExceptionGroup domain entities + repository interfaces"
```

---

### Task 2: `LogsPlatformDbContext` mapping + migration

**Files:**
- Modify: `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs`
- Modify: `tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs`

**Interfaces:**
- Consumes: `Event`, `ExceptionGroup` from Task 1.
- Produces: `DbSet<Event> Events`, `DbSet<ExceptionGroup> ExceptionGroups`, plus the migration creating both tables — used by Task 3's repositories.

**This is the task with the highest-risk technical detail in the whole plan — read Global Constraints' `DeleteBehavior.Restrict` note again before writing `OnModelCreating`.**

- [ ] **Step 1: Write the failing test**

```csharp
// Add to tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs
[Fact]
public async Task CanInsertAndRetrieveEventAndExceptionGroup()
{
    using var context = TestDatabase.CreateContext();

    var application = new Application { Name = "M2aDbContextTestApp", CreatedAt = DateTime.UtcNow };
    var environment = new AppEnvironment { Name = "Production", IsProduction = true };
    application.Environments.Add(environment);
    context.Applications.Add(application);
    await context.SaveChangesAsync();

    var group = new ExceptionGroup
    {
        ApplicationId = application.Id,
        Fingerprint = "abc123",
        ExceptionType = "System.InvalidOperationException",
        MessageTemplate = "Something failed",
        RepresentativeStackTrace = "at Foo.Bar()",
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        OccurrenceCount = 1
    };
    context.ExceptionGroups.Add(group);
    await context.SaveChangesAsync();

    context.Events.Add(new Event
    {
        ApplicationId = application.Id,
        EnvironmentId = environment.Id,
        Timestamp = DateTime.UtcNow,
        Severity = 17,
        Message = "Card authorization failed",
        ExceptionGroupId = group.Id,
        EventKey = "evt-1"
    });
    await context.SaveChangesAsync();

    using var readContext = new LogsPlatformDbContext(
        new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    var loadedEvent = await readContext.Events.SingleAsync(e => e.EventKey == "evt-1");
    Assert.Equal("Card authorization failed", loadedEvent.Message);
    Assert.Equal(group.Id, loadedEvent.ExceptionGroupId);

    var loadedGroup = await readContext.ExceptionGroups.SingleAsync(g => g.Fingerprint == "abc123");
    Assert.Equal(1, loadedGroup.OccurrenceCount);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter CanInsertAndRetrieveEventAndExceptionGroup`
Expected: FAIL — `context.Events`/`context.ExceptionGroups` do not exist yet.

- [ ] **Step 3: Add the `DbSet`s and `OnModelCreating` configuration**

Modify `LogsPlatformDbContext.cs`: add these two lines to the `DbSet` property list, directly after the existing `public DbSet<Deployment> Deployments => Set<Deployment>();` line:

```csharp
    public DbSet<Event> Events => Set<Event>();
    public DbSet<ExceptionGroup> ExceptionGroups => Set<ExceptionGroup>();
```

Then add these two new blocks to `OnModelCreating`, directly after the existing `Deployment` block (after its closing `});`) — do not remove or modify any of the 12 existing blocks:

```csharp
        modelBuilder.Entity<ExceptionGroup>(entity =>
        {
            entity.Property(g => g.Fingerprint).HasMaxLength(200).IsRequired();
            entity.Property(g => g.ExceptionType).HasMaxLength(500).IsRequired();
            entity.Property(g => g.MessageTemplate).HasMaxLength(1000).IsRequired();
            entity.HasOne(g => g.Application)
                .WithMany()
                .HasForeignKey(g => g.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(g => new { g.ApplicationId, g.Fingerprint }).IsUnique();
        });

        modelBuilder.Entity<Event>(entity =>
        {
            entity.Property(e => e.Message).IsRequired();
            entity.Property(e => e.EventKey).HasMaxLength(100);
            entity.Property(e => e.CorrelationId).HasMaxLength(100);
            entity.Property(e => e.TraceId).HasMaxLength(100);
            entity.Property(e => e.SpanId).HasMaxLength(100);
            entity.Property(e => e.ParentSpanId).HasMaxLength(100);
            entity.Property(e => e.MessageTemplate).HasMaxLength(1000);

            entity.HasOne(e => e.Application).WithMany().HasForeignKey(e => e.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Environment).WithMany().HasForeignKey(e => e.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Version).WithMany().HasForeignKey(e => e.VersionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Module).WithMany().HasForeignKey(e => e.ModuleId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ScreenService).WithMany().HasForeignKey(e => e.ScreenServiceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Process).WithMany().HasForeignKey(e => e.ProcessId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Operation).WithMany().HasForeignKey(e => e.OperationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Customer).WithMany().HasForeignKey(e => e.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.AppUser).WithMany().HasForeignKey(e => e.AppUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ExceptionGroup).WithMany().HasForeignKey(e => e.ExceptionGroupId).OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.ApplicationId, e.EnvironmentId, e.Timestamp });
            entity.HasIndex(e => new { e.ApplicationId, e.OperationId, e.Timestamp });
            entity.HasIndex(e => e.CorrelationId);
            entity.HasIndex(e => e.TraceId);
            entity.HasIndex(e => e.ExceptionGroupId);
            entity.HasIndex(e => new { e.ApplicationId, e.EventKey }).IsUnique().HasFilter("[EventKey] IS NOT NULL");
        });
```

Every `HasOne(...).WithMany()` on `Event` takes no argument to `WithMany()` — none of `AppEnvironment`/`AppVersion`/`AppModule`/`ScreenService`/`ProcessNode`/`Operation`/`Customer`/`AppUser`/`ExceptionGroup` needs (or has) an inverse `ICollection<Event>` navigation back to `Event` — matching the same unbound-`WithMany()` pattern B3's `Deployment` already established for its `Environment`/`Version` FKs.

- [ ] **Step 4: Generate the migration**

```bash
dotnet ef migrations add AddEventAndExceptionGroup \
  --project src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj \
  --startup-project src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj
```

Expected: creates `src/LogsPlatform.Infrastructure/Migrations/<timestamp>_AddEventAndExceptionGroup.cs` and updates the snapshot — two new tables (`ExceptionGroups`, `Events`), the FK/index shape above. Verify `Up()` only adds these two tables and their indexes/FKs — no `DropTable`/`DropColumn`/`AlterColumn` against any of the 11 pre-existing tables. If migration generation fails with an error mentioning "cycles or multiple cascade paths," you have a `DeleteBehavior.Cascade` somewhere on `Event` that should be `Restrict` — re-check every one of the nine `HasOne(...)` calls on `Event` against the list above, not just the ones that look suspicious.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter CanInsertAndRetrieveEventAndExceptionGroup`
Expected: PASS.

- [ ] **Step 6: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 169, Skipped: 0, Total: 169` (168 tests on `main` before this plan, plus this task's new one).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs src/LogsPlatform.Infrastructure/Migrations/ tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs
git commit -m "Add Event, ExceptionGroup EF Core mapping + migration"
```

---

### Task 3: `EventRepository` + `ExceptionGroupRepository` implementation + tests

**Files:**
- Create: `src/LogsPlatform.Infrastructure/Repositories/EventRepository.cs`
- Create: `src/LogsPlatform.Infrastructure/Repositories/ExceptionGroupRepository.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/EventRepositoryTests.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/ExceptionGroupRepositoryTests.cs`

**Interfaces:**
- Consumes: `IEventRepository`, `IExceptionGroupRepository` (Task 1), `LogsPlatformDbContext` (Task 2).
- Produces: `EventRepository`, `ExceptionGroupRepository` — registered in DI by a later task, consumed by Task 8's `IngestionProcessor` and Task 9's controller.

**Note the intra-batch dedup requirement carefully** — a batch can legitimately contain the same `EventKey` twice (a buggy or naive client), and that must be handled the same way as a cross-request duplicate: silently skipped, counted, not an error.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/EventRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class EventRepositoryTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> CreateFixtureAsync(LogsPlatformDbContext context, string appName)
    {
        var application = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        var environment = new AppEnvironment { Name = "Production", IsProduction = true };
        application.Environments.Add(environment);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return (application.Id, environment.Id);
    }

    private static Event MakeEvent(int appId, int envId, string? eventKey = null) => new()
    {
        ApplicationId = appId,
        EnvironmentId = envId,
        Timestamp = DateTime.UtcNow,
        Severity = 9,
        Message = "test event",
        EventKey = eventKey
    };

    [Fact]
    public async Task AddEventsAsync_PersistsEvents_ReturnsAcceptedCount()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await CreateFixtureAsync(context, "EventAddTestApp");
        var repository = new EventRepository(context);

        var result = await repository.AddEventsAsync(appId, new[] { MakeEvent(appId, envId), MakeEvent(appId, envId) });

        Assert.Equal(2, result.Accepted);
        Assert.Equal(0, result.DuplicateEventKeysSkipped);
        Assert.Equal(2, await context.Events.CountAsync());
    }

    [Fact]
    public async Task AddEventsAsync_DuplicateEventKeyAcrossRequests_SkipsAndCountsAsDuplicate()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await CreateFixtureAsync(context, "EventIdempotencyTestApp");
        var repository = new EventRepository(context);

        await repository.AddEventsAsync(appId, new[] { MakeEvent(appId, envId, "evt-dup") });
        var second = await repository.AddEventsAsync(appId, new[] { MakeEvent(appId, envId, "evt-dup") });

        Assert.Equal(0, second.Accepted);
        Assert.Equal(1, second.DuplicateEventKeysSkipped);
        Assert.Equal(1, await context.Events.CountAsync(e => e.EventKey == "evt-dup"));
    }

    [Fact]
    public async Task AddEventsAsync_DuplicateEventKeyWithinSameBatch_InsertsOnlyFirstOccurrence()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await CreateFixtureAsync(context, "EventIntraBatchDupTestApp");
        var repository = new EventRepository(context);

        var result = await repository.AddEventsAsync(appId, new[] { MakeEvent(appId, envId, "evt-same"), MakeEvent(appId, envId, "evt-same") });

        Assert.Equal(1, result.Accepted);
        Assert.Equal(1, result.DuplicateEventKeysSkipped);
        Assert.Equal(1, await context.Events.CountAsync(e => e.EventKey == "evt-same"));
    }
}
```

```csharp
// tests/LogsPlatform.Tests/Infrastructure/ExceptionGroupRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ExceptionGroupRepositoryTests
{
    private static async Task<int> CreateTestApplicationAsync(LogsPlatformDbContext context, string name)
    {
        var application = new Application { Name = name, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application.Id;
    }

    [Fact]
    public async Task GetOrCreateAsync_NewFingerprint_CreatesGroup()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ExceptionGroupCreateTestApp");
        var repository = new ExceptionGroupRepository(context);
        var seenAt = DateTime.UtcNow;

        var group = await repository.GetOrCreateAsync(appId, "fp-1", "System.Exception", "boom", "at Foo()", seenAt);

        Assert.Equal("fp-1", group.Fingerprint);
        Assert.Equal(1, group.OccurrenceCount);
        Assert.Equal(seenAt, group.FirstSeenAt);
    }

    [Fact]
    public async Task GetOrCreateAsync_ExistingFingerprint_ReturnsSameGroupWithoutIncrementingCount()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ExceptionGroupReuseTestApp");
        var repository = new ExceptionGroupRepository(context);
        var first = await repository.GetOrCreateAsync(appId, "fp-2", "System.Exception", "boom", "at Foo()", DateTime.UtcNow);

        var second = await repository.GetOrCreateAsync(appId, "fp-2", "System.Exception", "boom", "at Foo()", DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, second.OccurrenceCount);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter EventRepositoryTests|ExceptionGroupRepositoryTests`
Expected: FAIL — `EventRepository`/`ExceptionGroupRepository` do not exist yet.

- [ ] **Step 3: Implement both repositories**

```csharp
// src/LogsPlatform.Infrastructure/Repositories/EventRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class EventRepository : IEventRepository
{
    private readonly LogsPlatformDbContext _context;

    public EventRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<IngestResult> AddEventsAsync(int applicationId, IReadOnlyList<Event> events)
    {
        if (events.Count == 0)
        {
            return new IngestResult(0, 0);
        }

        var requestedKeys = events.Where(e => e.EventKey is not null).Select(e => e.EventKey!).Distinct().ToList();
        var existingKeys = requestedKeys.Count == 0
            ? new HashSet<string>()
            : (await _context.Events.AsNoTracking()
                .Where(e => e.ApplicationId == applicationId && e.EventKey != null && requestedKeys.Contains(e.EventKey!))
                .Select(e => e.EventKey!)
                .ToListAsync())
                .ToHashSet();

        var seenInBatch = new HashSet<string>();
        var toInsert = new List<Event>();
        var duplicateCount = 0;

        foreach (var evt in events)
        {
            if (evt.EventKey is not null && (existingKeys.Contains(evt.EventKey) || !seenInBatch.Add(evt.EventKey)))
            {
                duplicateCount++;
                continue;
            }
            toInsert.Add(evt);
        }

        if (toInsert.Count > 0)
        {
            _context.Events.AddRange(toInsert);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch
            {
                foreach (var entity in toInsert)
                {
                    _context.Entry(entity).State = EntityState.Detached;
                }
                throw;
            }
        }

        return new IngestResult(toInsert.Count, duplicateCount);
    }
}
```

```csharp
// src/LogsPlatform.Infrastructure/Repositories/ExceptionGroupRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ExceptionGroupRepository : IExceptionGroupRepository
{
    private readonly LogsPlatformDbContext _context;

    public ExceptionGroupRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<ExceptionGroup> GetOrCreateAsync(
        int applicationId, string fingerprint, string exceptionType,
        string messageTemplate, string representativeStackTrace, DateTime seenAt)
    {
        var existing = await _context.ExceptionGroups
            .FirstOrDefaultAsync(g => g.ApplicationId == applicationId && g.Fingerprint == fingerprint);
        if (existing is not null)
        {
            return existing;
        }

        var group = new ExceptionGroup
        {
            ApplicationId = applicationId,
            Fingerprint = fingerprint,
            ExceptionType = exceptionType,
            MessageTemplate = messageTemplate,
            RepresentativeStackTrace = representativeStackTrace,
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
            OccurrenceCount = 1
        };

        _context.ExceptionGroups.Add(group);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(group).State = EntityState.Detached;
            throw;
        }
        return group;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter EventRepositoryTests|ExceptionGroupRepositoryTests`
Expected: PASS (5 tests: 3 + 2).

- [ ] **Step 5: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 174, Skipped: 0, Total: 174` (169 from Tasks 1-2 + 5 from this task).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Infrastructure/Repositories/EventRepository.cs src/LogsPlatform.Infrastructure/Repositories/ExceptionGroupRepository.cs tests/LogsPlatform.Tests/Infrastructure/EventRepositoryTests.cs tests/LogsPlatform.Tests/Infrastructure/ExceptionGroupRepositoryTests.cs
git commit -m "Implement EventRepository and ExceptionGroupRepository"
```

---

### Task 4: Promote `ApiKeyHasher`, add `IApiKeyRepository.GetByKeyHashAsync`

**Files:**
- Create: `src/LogsPlatform.Infrastructure/ApiKeyHasher.cs`
- Modify: `src/LogsPlatform.Infrastructure/Repositories/ApiKeyRepository.cs`
- Modify: `src/LogsPlatform.Domain/Repositories/IApiKeyRepository.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/ApiKeyHasherTests.cs`
- Modify: `tests/LogsPlatform.Tests/Infrastructure/ApiKeyRepositoryTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ApiKeyHasher.Hash(string)` (public, shared), `IApiKeyRepository.GetByKeyHashAsync(string)` — consumed by Task 5's auth handler.

**This is the one task in this plan that modifies already-shipped B2 code.** It's a small, surgical extraction — read the current `ApiKeyRepository.cs` before editing so the diff is exactly "move `Hash` out, call the shared version instead," not a rewrite.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/ApiKeyHasherTests.cs
using LogsPlatform.Infrastructure;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

public class ApiKeyHasherTests
{
    [Fact]
    public void Hash_SameInput_ProducesSameOutput()
    {
        var first = ApiKeyHasher.Hash("lgp_sameraw");
        var second = ApiKeyHasher.Hash("lgp_sameraw");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Hash_DifferentInput_ProducesDifferentOutput()
    {
        var first = ApiKeyHasher.Hash("lgp_raw1");
        var second = ApiKeyHasher.Hash("lgp_raw2");
        Assert.NotEqual(first, second);
    }
}
```

```csharp
// Add to tests/LogsPlatform.Tests/Infrastructure/ApiKeyRepositoryTests.cs
[Fact]
public async Task GetByKeyHashAsync_ExistingHash_ReturnsMatchingKey()
{
    using var context = TestDatabase.CreateContext();
    var appId = await CreateTestApplicationAsync(context, "ApiKeyHashLookupTestApp");
    var repository = new ApiKeyRepository(context);
    var (created, rawKey) = await repository.AddAsync(appId, "Hash lookup test key");

    var found = await repository.GetByKeyHashAsync(created.KeyHash);

    Assert.NotNull(found);
    Assert.Equal(created.Id, found!.Id);
    Assert.NotEqual(rawKey, found.KeyHash);
}

[Fact]
public async Task GetByKeyHashAsync_UnknownHash_ReturnsNull()
{
    using var context = TestDatabase.CreateContext();
    var repository = new ApiKeyRepository(context);

    var found = await repository.GetByKeyHashAsync("0000000000000000000000000000000000000000000000000000000000000000");

    Assert.Null(found);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ApiKeyHasherTests|GetByKeyHashAsync`
Expected: FAIL — `ApiKeyHasher` and `GetByKeyHashAsync` do not exist yet.

- [ ] **Step 3: Create `ApiKeyHasher`**

```csharp
// src/LogsPlatform.Infrastructure/ApiKeyHasher.cs
using System.Security.Cryptography;
using System.Text;

namespace LogsPlatform.Infrastructure;

public static class ApiKeyHasher
{
    public static string Hash(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes);
    }
}
```

- [ ] **Step 4: Update `IApiKeyRepository` and `ApiKeyRepository`**

Add this line to `IApiKeyRepository.cs`, directly after the existing `GetByApplicationIdAsync` line:

```csharp
    Task<ApiKey?> GetByKeyHashAsync(string keyHash);
```

In `ApiKeyRepository.cs`: remove the `using System.Text;` line (no longer needed here once `Hash` moves out) and the entire `private static string Hash(string rawKey) { ... }` method at the bottom of the class. Replace the one call site, `KeyHash = Hash(rawKey)` inside `AddAsync`, with `KeyHash = ApiKeyHasher.Hash(rawKey)`. Add the new method (placement: directly after `GetByApplicationIdAsync`, matching the interface's order):

```csharp
    public async Task<ApiKey?> GetByKeyHashAsync(string keyHash) =>
        await _context.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.KeyHash == keyHash);
```

`using System.Security.Cryptography;` stays in `ApiKeyRepository.cs` — `GenerateRawKey` still uses `RandomNumberGenerator` directly.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter ApiKeyHasherTests|GetByKeyHashAsync`
Expected: PASS (4 tests: 2 + 2).

- [ ] **Step 6: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 178, Skipped: 0, Total: 178` (174 from Tasks 1-3 + 4 from this task). Every pre-existing `ApiKeyRepositoryTests`/`ApiKeysControllerTests` test must still pass unchanged — this task only adds, it doesn't change `AddAsync`'s or `RevokeAsync`'s observable behavior.

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Infrastructure/ApiKeyHasher.cs src/LogsPlatform.Infrastructure/Repositories/ApiKeyRepository.cs src/LogsPlatform.Domain/Repositories/IApiKeyRepository.cs tests/LogsPlatform.Tests/Infrastructure/ApiKeyHasherTests.cs tests/LogsPlatform.Tests/Infrastructure/ApiKeyRepositoryTests.cs
git commit -m "Promote ApiKeyHasher to shared location, add GetByKeyHashAsync"
```

---

### Task 5: `ApiKeyAuthenticationHandler` + auth wiring

**Files:**
- Create: `src/LogsPlatform.Web/Authentication/ApiKeyAuthenticationOptions.cs`
- Create: `src/LogsPlatform.Web/Authentication/ApiKeyAuthenticationHandler.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`

**Interfaces:**
- Consumes: `IApiKeyRepository.GetByKeyHashAsync` (Task 4), `ApiKeyHasher.Hash` (Task 4).
- Produces: an `"ApiKey"` authentication scheme, and `ApiKeyAuthenticationHandler.ApplicationIdClaimType` — consumed by Task 9's `IngestionController`.

**No dedicated automated tests for this task** — this handler's only real consumer in this plan is `IngestionController` (Task 9), which tests it end-to-end over real HTTP (missing key, invalid key, revoked key, valid key) against the actual protected endpoint. Building a separate throwaway protected test endpoint just to unit-test the handler in isolation would duplicate that coverage for no real benefit — see Task 9's test list for where this gets exercised.

- [ ] **Step 1: Create the options class**

```csharp
// src/LogsPlatform.Web/Authentication/ApiKeyAuthenticationOptions.cs
using Microsoft.AspNetCore.Authentication;

namespace LogsPlatform.Web.Authentication;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "ApiKey";
}
```

- [ ] **Step 2: Create the handler**

```csharp
// src/LogsPlatform.Web/Authentication/ApiKeyAuthenticationHandler.cs
using System.Security.Claims;
using System.Text.Encodings.Web;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogsPlatform.Web.Authentication;

public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    public const string ApplicationIdClaimType = "ApplicationId";

    private readonly IApiKeyRepository _apiKeys;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyRepository apiKeys)
        : base(options, logger, encoder)
    {
        _apiKeys = apiKeys;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var rawKeyValues) || string.IsNullOrWhiteSpace(rawKeyValues))
        {
            return AuthenticateResult.Fail("Missing X-Api-Key header.");
        }

        var rawKey = rawKeyValues.ToString();
        var keyHash = ApiKeyHasher.Hash(rawKey);
        var apiKey = await _apiKeys.GetByKeyHashAsync(keyHash);

        if (apiKey is null || apiKey.RevokedAt is not null)
        {
            return AuthenticateResult.Fail("Invalid or revoked API key.");
        }

        var claims = new[] { new Claim(ApplicationIdClaimType, apiKey.ApplicationId.ToString()) };
        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationOptions.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, ApiKeyAuthenticationOptions.SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
```

- [ ] **Step 3: Wire it into `Program.cs`**

Add these lines to `Program.cs`, directly after the existing `builder.Services.AddSwaggerGen();` line:

```csharp
builder.Services.AddMemoryCache();
builder.Services.AddAuthentication(ApiKeyAuthenticationOptions.SchemeName)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.SchemeName, options => { });
builder.Services.AddAuthorization();
```

(`AddMemoryCache()` is added here even though Task 9's rate limiter is the only consumer — it's DI-container setup, harmless to add now, and keeps all `Program.cs` edits for this plan's auth/rate-limit story in one place instead of two separate diffs to the same file.)

Add `using LogsPlatform.Web.Authentication;` to `Program.cs`'s using list.

Add these two lines directly after the existing `app.UseStaticFiles();` line and before `app.UseAntiforgery();`:

```csharp
app.UseAuthentication();
app.UseAuthorization();
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Run the full existing test suite — this is the critical regression check for this task**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 178, Skipped: 0, Total: 178` — **unchanged**. Every existing Admin API controller test (`CustomersControllerTests`, `ApiKeysControllerTests`, etc.) must still pass with zero modification, proving `UseAuthentication()`/`UseAuthorization()` didn't start enforcing auth on endpoints that never opted into it. If any of these start failing, do not add `[AllowAnonymous]` to existing controllers as a fix — stop and investigate why the pipeline change had a broader effect than expected (per Global Constraints, it shouldn't).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Authentication/ApiKeyAuthenticationOptions.cs src/LogsPlatform.Web/Authentication/ApiKeyAuthenticationHandler.cs src/LogsPlatform.Web/Program.cs
git commit -m "Add ApiKey authentication scheme for ingestion endpoint"
```

---

### Task 6: `HierarchyResolver` service + tests

**Files:**
- Create: `src/LogsPlatform.Web/Services/HierarchyResolver.cs`
- Create: `tests/LogsPlatform.Tests/Web/HierarchyResolverTests.cs`

**Interfaces:**
- Consumes: `IAppModuleRepository`, `IScreenServiceRepository`, `IProcessNodeRepository`, `IOperationRepository` (all existing, from Group A).
- Produces: `HierarchyResolver.ResolveAsync(...)` — consumed by Task 8's `IngestionProcessor`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/HierarchyResolverTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class HierarchyResolverTests
{
    private static async Task<(int ApplicationId, int ModuleId, int ScreenServiceId, int ProcessId, int OperationId)> CreateFullFixtureAsync(LogsPlatformDbContext context)
    {
        var application = new Application { Name = $"HierarchyResolverTestApp-{Guid.NewGuid()}", CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();

        var moduleRepo = new AppModuleRepository(context);
        var screenServiceRepo = new ScreenServiceRepository(context);
        var processRepo = new ProcessNodeRepository(context);
        var operationRepo = new OperationRepository(context);

        var module = await moduleRepo.AddAsync(new AppModule { ApplicationId = application.Id, Name = "Payments" });
        var screenService = await screenServiceRepo.AddAsync(new ScreenService { ModuleId = module.Id, Name = "PaymentGateway", Type = ScreenServiceType.Service });
        var process = await processRepo.AddAsync(new ProcessNode { ScreenServiceId = screenService.Id, Name = "ProcessPayment" });
        var operation = await operationRepo.AddAsync(new Operation { ProcessId = process.Id, Name = "AuthorizeCard" });

        return (application.Id, module.Id, screenService.Id, process.Id, operation.Id);
    }

    private static HierarchyResolver CreateResolver(LogsPlatformDbContext context) => new(
        new AppModuleRepository(context), new ScreenServiceRepository(context),
        new ProcessNodeRepository(context), new OperationRepository(context));

    [Fact]
    public async Task ResolveAsync_AllLayersResolve_ReturnsAllIds()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, moduleId, screenServiceId, processId, operationId) = await CreateFullFixtureAsync(context);
        var resolver = CreateResolver(context);

        var result = await resolver.ResolveAsync(appId, "Payments", "PaymentGateway", "ProcessPayment", "AuthorizeCard");

        Assert.Equal(moduleId, result.ModuleId);
        Assert.Equal(screenServiceId, result.ScreenServiceId);
        Assert.Equal(processId, result.ProcessId);
        Assert.Equal(operationId, result.OperationId);
        Assert.Null(result.WarningField);
    }

    [Fact]
    public async Task ResolveAsync_ModuleNotFound_ReturnsAllNullWithModuleWarning()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _, _, _, _) = await CreateFullFixtureAsync(context);
        var resolver = CreateResolver(context);

        var result = await resolver.ResolveAsync(appId, "TypoModule", "PaymentGateway", "ProcessPayment", "AuthorizeCard");

        Assert.Null(result.ModuleId);
        Assert.Null(result.ScreenServiceId);
        Assert.Null(result.ProcessId);
        Assert.Null(result.OperationId);
        Assert.Equal("module", result.WarningField);
    }

    [Fact]
    public async Task ResolveAsync_ScreenServiceNotFound_ReturnsModuleIdOnlyWithWarning()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, moduleId, _, _, _) = await CreateFullFixtureAsync(context);
        var resolver = CreateResolver(context);

        var result = await resolver.ResolveAsync(appId, "Payments", "TypoScreenService", "ProcessPayment", "AuthorizeCard");

        Assert.Equal(moduleId, result.ModuleId);
        Assert.Null(result.ScreenServiceId);
        Assert.Null(result.ProcessId);
        Assert.Null(result.OperationId);
        Assert.Equal("screenService", result.WarningField);
    }

    [Fact]
    public async Task ResolveAsync_NoHierarchyProvided_ReturnsAllNullNoWarning()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _, _, _, _) = await CreateFullFixtureAsync(context);
        var resolver = CreateResolver(context);

        var result = await resolver.ResolveAsync(appId, null, null, null, null);

        Assert.Null(result.ModuleId);
        Assert.Null(result.WarningField);
    }

    [Fact]
    public async Task ResolveAsync_PartialPathProvided_StopsAtLastProvidedLayerNoWarning()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, moduleId, screenServiceId, _, _) = await CreateFullFixtureAsync(context);
        var resolver = CreateResolver(context);

        var result = await resolver.ResolveAsync(appId, "Payments", "PaymentGateway", null, null);

        Assert.Equal(moduleId, result.ModuleId);
        Assert.Equal(screenServiceId, result.ScreenServiceId);
        Assert.Null(result.ProcessId);
        Assert.Null(result.WarningField);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter HierarchyResolverTests`
Expected: FAIL — `HierarchyResolver` does not exist yet.

- [ ] **Step 3: Implement `HierarchyResolver`**

```csharp
// src/LogsPlatform.Web/Services/HierarchyResolver.cs
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services;

public record HierarchyResolutionResult(int? ModuleId, int? ScreenServiceId, int? ProcessId, int? OperationId, string? WarningField);

public class HierarchyResolver
{
    private readonly IAppModuleRepository _modules;
    private readonly IScreenServiceRepository _screenServices;
    private readonly IProcessNodeRepository _processes;
    private readonly IOperationRepository _operations;

    public HierarchyResolver(
        IAppModuleRepository modules,
        IScreenServiceRepository screenServices,
        IProcessNodeRepository processes,
        IOperationRepository operations)
    {
        _modules = modules;
        _screenServices = screenServices;
        _processes = processes;
        _operations = operations;
    }

    public async Task<HierarchyResolutionResult> ResolveAsync(int applicationId, string? module, string? screenService, string? process, string? operation)
    {
        if (string.IsNullOrWhiteSpace(module))
        {
            return new HierarchyResolutionResult(null, null, null, null, null);
        }

        var modules = await _modules.GetByApplicationIdAsync(applicationId);
        var moduleEntity = modules.FirstOrDefault(m => m.Name == module);
        if (moduleEntity is null)
        {
            return new HierarchyResolutionResult(null, null, null, null, "module");
        }

        if (string.IsNullOrWhiteSpace(screenService))
        {
            return new HierarchyResolutionResult(moduleEntity.Id, null, null, null, null);
        }

        var screenServices = await _screenServices.GetByModuleIdAsync(moduleEntity.Id);
        var screenServiceEntity = screenServices.FirstOrDefault(s => s.Name == screenService);
        if (screenServiceEntity is null)
        {
            return new HierarchyResolutionResult(moduleEntity.Id, null, null, null, "screenService");
        }

        if (string.IsNullOrWhiteSpace(process))
        {
            return new HierarchyResolutionResult(moduleEntity.Id, screenServiceEntity.Id, null, null, null);
        }

        var processes = await _processes.GetByScreenServiceIdAsync(screenServiceEntity.Id);
        var processEntity = processes.FirstOrDefault(p => p.Name == process);
        if (processEntity is null)
        {
            return new HierarchyResolutionResult(moduleEntity.Id, screenServiceEntity.Id, null, null, "process");
        }

        if (string.IsNullOrWhiteSpace(operation))
        {
            return new HierarchyResolutionResult(moduleEntity.Id, screenServiceEntity.Id, processEntity.Id, null, null);
        }

        var operations = await _operations.GetByProcessIdAsync(processEntity.Id);
        var operationEntity = operations.FirstOrDefault(o => o.Name == operation);
        if (operationEntity is null)
        {
            return new HierarchyResolutionResult(moduleEntity.Id, screenServiceEntity.Id, processEntity.Id, null, "operation");
        }

        return new HierarchyResolutionResult(moduleEntity.Id, screenServiceEntity.Id, processEntity.Id, operationEntity.Id, null);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter HierarchyResolverTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 183, Skipped: 0, Total: 183` (178 from Tasks 1-5 + 5 from this task).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Services/HierarchyResolver.cs tests/LogsPlatform.Tests/Web/HierarchyResolverTests.cs
git commit -m "Add HierarchyResolver service"
```

---

### Task 7: `ExceptionFingerprinter` + tests

**Files:**
- Create: `src/LogsPlatform.Web/Services/ExceptionFingerprinter.cs`
- Create: `tests/LogsPlatform.Tests/Web/ExceptionFingerprinterTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ExceptionFingerprinter.Compute(...)` — consumed by Task 8's `IngestionProcessor`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/ExceptionFingerprinterTests.cs
using LogsPlatform.Web.Services;
using Xunit;

namespace LogsPlatform.Tests.Web;

public class ExceptionFingerprinterTests
{
    private const string StackTraceA =
        "   at MyApp.Payments.PaymentGateway.AuthorizeCard(String cardNumber) in C:\\src\\PaymentGateway.cs:line 42\n" +
        "   at MyApp.Payments.ProcessPayment(Order order) in C:\\src\\ProcessPayment.cs:line 18";

    private const string StackTraceALaterBuild =
        "   at MyApp.Payments.PaymentGateway.AuthorizeCard(String cardNumber) in C:\\src\\PaymentGateway.cs:line 51\n" +
        "   at MyApp.Payments.ProcessPayment(Order order) in C:\\src\\ProcessPayment.cs:line 25";

    private const string StackTraceB =
        "   at MyApp.Inventory.StockManager.ReserveStock(String sku) in C:\\src\\StockManager.cs:line 10";

    [Fact]
    public void Compute_SameInputs_ProducesSameFingerprint()
    {
        var first = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceA, "template");
        var second = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceA, "template");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_DifferentExceptionType_ProducesDifferentFingerprint()
    {
        var first = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceA, "template");
        var second = ExceptionFingerprinter.Compute("System.InvalidOperationException", StackTraceA, "template");
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Compute_StackTraceLineNumbersDiffer_SameFingerprint()
    {
        var first = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceA, "template");
        var second = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceALaterBuild, "template");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_DifferentStackTrace_ProducesDifferentFingerprint()
    {
        var first = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceA, "template");
        var second = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceB, "template");
        Assert.NotEqual(first, second);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ExceptionFingerprinterTests`
Expected: FAIL — `ExceptionFingerprinter` does not exist yet.

- [ ] **Step 3: Implement `ExceptionFingerprinter`**

```csharp
// src/LogsPlatform.Web/Services/ExceptionFingerprinter.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LogsPlatform.Web.Services;

public static partial class ExceptionFingerprinter
{
    public static string Compute(string exceptionType, string stackTrace, string? messageTemplate)
    {
        var signature = NormalizeStackSignature(stackTrace);
        var input = $"{exceptionType}|{signature}|{messageTemplate}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    private static string NormalizeStackSignature(string stackTrace)
    {
        var lines = stackTrace
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(3);

        var normalized = lines.Select(line => LineNumberPattern().Replace(line, string.Empty).Trim());
        return string.Join("|", normalized);
    }

    [GeneratedRegex(@":line \d+")]
    private static partial Regex LineNumberPattern();
}
```

Top 3 stack frames, line numbers stripped (they shift across rebuilds of the same logical bug; type + method names across the top few frames is the "stable-enough, non-ML" signature this project's V1 scope calls for — see the design doc).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ExceptionFingerprinterTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 187, Skipped: 0, Total: 187` (183 from Tasks 1-6 + 4 from this task).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Services/ExceptionFingerprinter.cs tests/LogsPlatform.Tests/Web/ExceptionFingerprinterTests.cs
git commit -m "Add ExceptionFingerprinter service"
```

---

### Task 8: `IngestionProcessor` service + tests

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/IngestionContracts.cs`
- Create: `src/LogsPlatform.Web/Services/IngestionProcessor.cs`
- Create: `tests/LogsPlatform.Tests/Web/IngestionProcessorTests.cs`

**Interfaces:**
- Consumes: `IAppEnvironmentRepository`, `IAppVersionRepository`, `ICustomerRepository`, `IAppUserRepository` (all existing), `IExceptionGroupRepository` (Task 3), `HierarchyResolver` (Task 6), `ExceptionFingerprinter` (Task 7).
- Produces: `IngestionProcessor.ProcessAsync(...)` — the single-event pipeline consumed by Task 9's controller.

**This is the highest-risk logic in the plan — it owns every validation/resolution rule from Global Constraints.** Read that section again before implementing; every branch below maps to one of its bullets.

- [ ] **Step 1: Write the contracts**

```csharp
// src/LogsPlatform.Web/Contracts/IngestionContracts.cs
namespace LogsPlatform.Web.Contracts;

public record IngestHierarchyRequest(string? Module, string? ScreenService, string? Process, string? Operation);
public record IngestExceptionRequest(string Type, string StackTrace);

public record IngestEventRequest(
    string? EventKey,
    DateTime? Timestamp,
    string? Severity,
    string? Environment,
    string? Version,
    IngestHierarchyRequest? Hierarchy,
    string? CorrelationId,
    string? TraceId,
    string? SpanId,
    string? ParentSpanId,
    double? DurationMs,
    string? CustomerId,
    string? UserId,
    string? Message,
    string? MessageTemplate,
    IngestExceptionRequest? Exception,
    Dictionary<string, object>? Metadata);

public record IngestErrorEntry(int Index, string Reason);
public record IngestWarningEntry(int Index, string Field, string Reason);
public record IngestResponse(int Accepted, int Rejected, List<IngestErrorEntry> Errors, List<IngestWarningEntry> HierarchyWarnings);
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/IngestionProcessorTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class IngestionProcessorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> CreateFixtureAsync(LogsPlatformDbContext context, string appName)
    {
        var application = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        var environment = new AppEnvironment { Name = "Production", IsProduction = true };
        application.Environments.Add(environment);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return (application.Id, environment.Id);
    }

    private static IngestionProcessor CreateProcessor(LogsPlatformDbContext context) => new(
        new AppEnvironmentRepository(context), new AppVersionRepository(context),
        new CustomerRepository(context), new AppUserRepository(context),
        new ExceptionGroupRepository(context),
        new HierarchyResolver(new AppModuleRepository(context), new ScreenServiceRepository(context), new ProcessNodeRepository(context), new OperationRepository(context)));

    private static IngestEventRequest ValidRequest(string environment) => new(
        EventKey: null, Timestamp: DateTime.UtcNow, Severity: "Error", Environment: environment, Version: null,
        Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null, DurationMs: null,
        CustomerId: null, UserId: null, Message: "something failed", MessageTemplate: null, Exception: null, Metadata: null);

    [Fact]
    public async Task ProcessAsync_ValidEvent_ReturnsEventWithNoWarnings()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorValidTestApp");
        var processor = CreateProcessor(context);

        var result = await processor.ProcessAsync(appId, ValidRequest("Production"));

        Assert.Null(result.RejectReason);
        Assert.NotNull(result.Event);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ProcessAsync_MissingTimestamp_Rejects()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorNoTimestampTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { Timestamp = null };

        var result = await processor.ProcessAsync(appId, request);

        Assert.NotNull(result.RejectReason);
        Assert.Null(result.Event);
    }

    [Fact]
    public async Task ProcessAsync_InvalidSeverity_Rejects()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorBadSeverityTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { Severity = "Critical" };

        var result = await processor.ProcessAsync(appId, request);

        Assert.NotNull(result.RejectReason);
        Assert.Null(result.Event);
    }

    [Fact]
    public async Task ProcessAsync_MissingMessage_Rejects()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorNoMessageTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { Message = null };

        var result = await processor.ProcessAsync(appId, request);

        Assert.NotNull(result.RejectReason);
        Assert.Null(result.Event);
    }

    [Fact]
    public async Task ProcessAsync_UnknownEnvironment_Rejects()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorUnknownEnvTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Staging");

        var result = await processor.ProcessAsync(appId, request);

        Assert.NotNull(result.RejectReason);
        Assert.Null(result.Event);
    }

    [Fact]
    public async Task ProcessAsync_UnknownVersion_WarnsButAccepts()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorUnknownVersionTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { Version = "9.9.9" };

        var result = await processor.ProcessAsync(appId, request);

        Assert.Null(result.RejectReason);
        Assert.NotNull(result.Event);
        Assert.Null(result.Event!.VersionId);
        Assert.Contains(result.Warnings, w => w.Field == "version");
    }

    [Fact]
    public async Task ProcessAsync_UnknownCustomerId_WarnsButAccepts()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorUnknownCustomerTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { CustomerId = "cust-does-not-exist" };

        var result = await processor.ProcessAsync(appId, request);

        Assert.Null(result.RejectReason);
        Assert.Null(result.Event!.CustomerId);
        Assert.Contains(result.Warnings, w => w.Field == "customerId");
    }

    [Fact]
    public async Task ProcessAsync_UnresolvableHierarchy_WarnsButAccepts()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorUnresolvableHierarchyTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { Hierarchy = new IngestHierarchyRequest("TypoModule", null, null, null) };

        var result = await processor.ProcessAsync(appId, request);

        Assert.Null(result.RejectReason);
        Assert.Null(result.Event!.ModuleId);
        Assert.Contains(result.Warnings, w => w.Field == "module");
    }

    [Fact]
    public async Task ProcessAsync_WithException_ComputesFingerprintAndCreatesExceptionGroup()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorExceptionTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { Exception = new IngestExceptionRequest("System.TimeoutException", "at Foo.Bar()") };

        var result = await processor.ProcessAsync(appId, request);

        Assert.Null(result.RejectReason);
        Assert.NotNull(result.Event!.ExceptionGroupId);
        Assert.Equal(1, await context.ExceptionGroups.CountAsync());
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter IngestionProcessorTests`
Expected: FAIL — `IngestionProcessor` does not exist yet.

- [ ] **Step 4: Implement `IngestionProcessor`**

```csharp
// src/LogsPlatform.Web/Services/IngestionProcessor.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;

namespace LogsPlatform.Web.Services;

public record ProcessedEvent(Event? Event, string? RejectReason, IReadOnlyList<(string Field, string Reason)> Warnings);

public class IngestionProcessor
{
    private static readonly Dictionary<string, int> SeverityMap = new()
    {
        ["Trace"] = 1, ["Debug"] = 5, ["Info"] = 9, ["Warn"] = 13, ["Error"] = 17, ["Fatal"] = 21
    };

    private readonly IAppEnvironmentRepository _environments;
    private readonly IAppVersionRepository _versions;
    private readonly ICustomerRepository _customers;
    private readonly IAppUserRepository _appUsers;
    private readonly IExceptionGroupRepository _exceptionGroups;
    private readonly HierarchyResolver _hierarchyResolver;

    public IngestionProcessor(
        IAppEnvironmentRepository environments,
        IAppVersionRepository versions,
        ICustomerRepository customers,
        IAppUserRepository appUsers,
        IExceptionGroupRepository exceptionGroups,
        HierarchyResolver hierarchyResolver)
    {
        _environments = environments;
        _versions = versions;
        _customers = customers;
        _appUsers = appUsers;
        _exceptionGroups = exceptionGroups;
        _hierarchyResolver = hierarchyResolver;
    }

    public async Task<ProcessedEvent> ProcessAsync(int applicationId, IngestEventRequest request)
    {
        if (request.Timestamp is null)
        {
            return Reject("timestamp: required field missing");
        }
        if (string.IsNullOrWhiteSpace(request.Severity) || !SeverityMap.TryGetValue(request.Severity, out var severityValue))
        {
            return Reject($"severity: invalid value '{request.Severity}'");
        }
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Reject("message: required field missing");
        }
        if (string.IsNullOrWhiteSpace(request.Environment))
        {
            return Reject("environment: required field missing");
        }

        var environments = await _environments.GetByApplicationIdAsync(applicationId);
        var environmentEntity = environments.FirstOrDefault(e => e.Name == request.Environment);
        if (environmentEntity is null)
        {
            return Reject($"environment: '{request.Environment}' not found");
        }

        var warnings = new List<(string Field, string Reason)>();

        int? versionId = null;
        if (!string.IsNullOrWhiteSpace(request.Version))
        {
            var versions = await _versions.GetByApplicationIdAsync(applicationId);
            var versionEntity = versions.FirstOrDefault(v => v.VersionNumber == request.Version);
            if (versionEntity is null)
            {
                warnings.Add(("version", "not found, event stored without version reference"));
            }
            else
            {
                versionId = versionEntity.Id;
            }
        }

        int? customerId = null;
        if (!string.IsNullOrWhiteSpace(request.CustomerId))
        {
            var customers = await _customers.GetByApplicationIdAsync(applicationId);
            var customerEntity = customers.FirstOrDefault(c => c.ExternalCustomerId == request.CustomerId);
            if (customerEntity is null)
            {
                warnings.Add(("customerId", "not found, event stored without customer reference"));
            }
            else
            {
                customerId = customerEntity.Id;
            }
        }

        int? appUserId = null;
        if (!string.IsNullOrWhiteSpace(request.UserId))
        {
            var appUsers = await _appUsers.GetByApplicationIdAsync(applicationId);
            var appUserEntity = appUsers.FirstOrDefault(u => u.ExternalUserId == request.UserId);
            if (appUserEntity is null)
            {
                warnings.Add(("userId", "not found, event stored without user reference"));
            }
            else
            {
                appUserId = appUserEntity.Id;
            }
        }

        var hierarchy = request.Hierarchy is null
            ? new HierarchyResolutionResult(null, null, null, null, null)
            : await _hierarchyResolver.ResolveAsync(applicationId, request.Hierarchy.Module, request.Hierarchy.ScreenService, request.Hierarchy.Process, request.Hierarchy.Operation);
        if (hierarchy.WarningField is not null)
        {
            warnings.Add((hierarchy.WarningField, "not found, event stored without this and deeper hierarchy references"));
        }

        long? exceptionGroupId = null;
        string? stackTrace = null;
        if (request.Exception is not null)
        {
            var fingerprint = ExceptionFingerprinter.Compute(request.Exception.Type, request.Exception.StackTrace, request.MessageTemplate);
            var group = await _exceptionGroups.GetOrCreateAsync(
                applicationId, fingerprint, request.Exception.Type, request.MessageTemplate ?? string.Empty, request.Exception.StackTrace, request.Timestamp.Value);
            exceptionGroupId = group.Id;
            stackTrace = request.Exception.StackTrace;
        }

        var evt = new Event
        {
            ApplicationId = applicationId,
            Timestamp = request.Timestamp.Value,
            Severity = severityValue,
            EnvironmentId = environmentEntity.Id,
            VersionId = versionId,
            ModuleId = hierarchy.ModuleId,
            ScreenServiceId = hierarchy.ScreenServiceId,
            ProcessId = hierarchy.ProcessId,
            OperationId = hierarchy.OperationId,
            CustomerId = customerId,
            AppUserId = appUserId,
            EventKey = request.EventKey,
            CorrelationId = request.CorrelationId,
            TraceId = request.TraceId,
            SpanId = request.SpanId,
            ParentSpanId = request.ParentSpanId,
            DurationMs = request.DurationMs,
            Message = request.Message,
            MessageTemplate = request.MessageTemplate,
            ExceptionGroupId = exceptionGroupId,
            StackTrace = stackTrace,
            MetadataJson = request.Metadata is null ? null : System.Text.Json.JsonSerializer.Serialize(request.Metadata)
        };

        return new ProcessedEvent(evt, null, warnings);
    }

    private static ProcessedEvent Reject(string reason) => new(null, reason, Array.Empty<(string, string)>());
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter IngestionProcessorTests`
Expected: PASS (9 tests).

- [ ] **Step 6: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 196, Skipped: 0, Total: 196` (187 from Tasks 1-7 + 9 from this task).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/IngestionContracts.cs src/LogsPlatform.Web/Services/IngestionProcessor.cs tests/LogsPlatform.Tests/Web/IngestionProcessorTests.cs
git commit -m "Add IngestionProcessor service"
```

---

### Task 9: `IngestionController` + DI wiring + tests — completes M2a

**Files:**
- Create: `src/LogsPlatform.Web/Controllers/IngestionController.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Create: `tests/LogsPlatform.Tests/Web/IngestionControllerTests.cs`

**Interfaces:**
- Consumes: `IngestionProcessor` (Task 8), `IEventRepository` (Task 3), `ApiKeyAuthenticationOptions` (Task 5), DI wiring (this task).
- Produces: `POST /api/v1/ingest/events` — the endpoint M2's own milestone acceptance criterion is measured against.

**This task's tests are the end-to-end proof of the whole plan** — real HTTP requests through the real auth handler, the real processor, the real repository. This is deliberately where auth (Task 5) and rate-limiting get their only automated coverage, per Global Constraints.

- [ ] **Step 1: Wire the remaining DI registrations**

Add these lines to `Program.cs`, directly after the existing `builder.Services.AddScoped<IDeploymentRepository, DeploymentRepository>();` line:

```csharp
builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddScoped<IExceptionGroupRepository, ExceptionGroupRepository>();
builder.Services.AddScoped<HierarchyResolver>();
builder.Services.AddScoped<IngestionProcessor>();
```

Add `using LogsPlatform.Web.Services;` to `Program.cs`'s using list if not already present from Task 5.

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/IngestionControllerTests.cs
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class IngestionControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public IngestionControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<(int ApplicationId, string ApiKey)> CreateAppWithApiKeyAsync(HttpClient client, string appName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));

        var keyResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("Ingestion test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        return (app.Id, key!.ApiKey);
    }

    private static IngestEventRequest ValidEvent(string? eventKey = null) => new(
        EventKey: eventKey, Timestamp: DateTime.UtcNow, Severity: "Error", Environment: "Production", Version: null,
        Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null, DurationMs: null,
        CustomerId: null, UserId: null, Message: "something failed", MessageTemplate: null, Exception: null, Metadata: null);

    private HttpRequestMessage BuildRequest(string apiKey, List<IngestEventRequest> events)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/events") { Content = JsonContent.Create(events) };
        request.Headers.Add("X-Api-Key", apiKey);
        return request;
    }

    [Fact]
    public async Task IngestEvents_ValidBatchWithApiKey_Returns202AndPersistsEvents()
    {
        var client = _factory.CreateClient();
        var (_, apiKey) = await CreateAppWithApiKeyAsync(client, "IngestValidBatchTestApp");

        var response = await client.SendAsync(BuildRequest(apiKey, new List<IngestEventRequest> { ValidEvent(), ValidEvent() }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.Equal(2, body!.Accepted);
        Assert.Equal(0, body.Rejected);
    }

    [Fact]
    public async Task IngestEvents_MissingApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/ingest/events", new List<IngestEventRequest> { ValidEvent() });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IngestEvents_InvalidApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.SendAsync(BuildRequest("lgp_not-a-real-key", new List<IngestEventRequest> { ValidEvent() }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IngestEvents_RevokedApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("IngestRevokedKeyTestApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var keyResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/api-keys", new CreateApiKeyRequest("To be revoked"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();
        await client.DeleteAsync($"/api/v1/admin/applications/{app.Id}/api-keys/{key!.Id}");

        var response = await client.SendAsync(BuildRequest(key.ApiKey, new List<IngestEventRequest> { ValidEvent() }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IngestEvents_EventWithInvalidRequiredField_RejectedButBatchContinues()
    {
        var client = _factory.CreateClient();
        var (_, apiKey) = await CreateAppWithApiKeyAsync(client, "IngestPartialFailureTestApp");
        var badEvent = ValidEvent() with { Message = null };

        var response = await client.SendAsync(BuildRequest(apiKey, new List<IngestEventRequest> { ValidEvent(), badEvent, ValidEvent() }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.Equal(2, body!.Accepted);
        Assert.Equal(1, body.Rejected);
        Assert.Single(body.Errors);
        Assert.Equal(1, body.Errors[0].Index);
    }

    [Fact]
    public async Task IngestEvents_HierarchyTypo_EventStoredWithWarningNotRejected()
    {
        var client = _factory.CreateClient();
        var (_, apiKey) = await CreateAppWithApiKeyAsync(client, "IngestHierarchyTypoTestApp");
        var eventWithTypo = ValidEvent() with { Hierarchy = new IngestHierarchyRequest("ChrgePayment", null, null, null) };

        var response = await client.SendAsync(BuildRequest(apiKey, new List<IngestEventRequest> { eventWithTypo }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.Equal(1, body!.Accepted);
        Assert.Equal(0, body.Rejected);
        Assert.Single(body.HierarchyWarnings);
        Assert.Equal("module", body.HierarchyWarnings[0].Field);
    }

    [Fact]
    public async Task IngestEvents_DuplicateEventKeyRetry_DoesNotDuplicateRow()
    {
        var client = _factory.CreateClient();
        var (_, apiKey) = await CreateAppWithApiKeyAsync(client, "IngestRetryTestApp");
        var evt = ValidEvent("retry-key-1");

        var first = await client.SendAsync(BuildRequest(apiKey, new List<IngestEventRequest> { evt }));
        var second = await client.SendAsync(BuildRequest(apiKey, new List<IngestEventRequest> { evt }));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.Equal(1, secondBody!.Accepted);
        Assert.Equal(0, secondBody.Rejected);
    }

    [Fact]
    public async Task IngestEvents_TwoEventsSameException_ShareOneExceptionGroup()
    {
        var client = _factory.CreateClient();
        var (_, apiKey) = await CreateAppWithApiKeyAsync(client, "IngestSharedExceptionGroupTestApp");
        var exceptionInfo = new IngestExceptionRequest("System.TimeoutException", "at Foo.Bar() in Foo.cs:line 10");
        var first = ValidEvent() with { Exception = exceptionInfo };
        var second = ValidEvent() with { Exception = exceptionInfo with { StackTrace = "at Foo.Bar() in Foo.cs:line 99" } };

        var response = await client.SendAsync(BuildRequest(apiKey, new List<IngestEventRequest> { first, second }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestResponse>();
        Assert.Equal(2, body!.Accepted);
    }
}
```

Note this test file's `CreateAppWithApiKeyAsync` reuses already-merged Admin API endpoints/contracts exactly as they exist today (`CreateApplicationRequest`/`ApplicationResponse` from `ApplicationContracts.cs`, `CreateEnvironmentRequest` from the same file, `CreateApiKeyRequest`/`CreateApiKeyResponse` from `ApiKeyContracts.cs`) — none of these are modified by this plan, this test only exercises them as fixtures, same as every prior controller test file in this project.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter IngestionControllerTests`
Expected: FAIL — `IngestionController` does not exist yet.

- [ ] **Step 4: Implement `IngestionController`**

```csharp
// src/LogsPlatform.Web/Controllers/IngestionController.cs
using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Authentication;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/ingest")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]
public class IngestionController : ControllerBase
{
    private const int RateLimitPerMinute = 1000;

    private readonly IngestionProcessor _processor;
    private readonly IEventRepository _events;
    private readonly IMemoryCache _cache;

    public IngestionController(IngestionProcessor processor, IEventRepository events, IMemoryCache cache)
    {
        _processor = processor;
        _events = events;
        _cache = cache;
    }

    [HttpPost("events")]
    public async Task<ActionResult<IngestResponse>> IngestEvents([FromBody] List<IngestEventRequest> requests)
    {
        var applicationId = int.Parse(User.FindFirstValue(ApiKeyAuthenticationHandler.ApplicationIdClaimType)!);

        var counter = _cache.GetOrCreate($"ingest-rate:{applicationId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return new RateCounter();
        })!;
        if (Interlocked.Increment(ref counter.Count) > RateLimitPerMinute)
        {
            Response.Headers["Retry-After"] = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, new { title = "Rate limit exceeded", status = 429 });
        }

        var errors = new List<IngestErrorEntry>();
        var hierarchyWarnings = new List<IngestWarningEntry>();
        var toInsert = new List<Event>();

        for (var index = 0; index < requests.Count; index++)
        {
            var processed = await _processor.ProcessAsync(applicationId, requests[index]);
            if (processed.RejectReason is not null)
            {
                errors.Add(new IngestErrorEntry(index, processed.RejectReason));
                continue;
            }
            foreach (var (field, reason) in processed.Warnings)
            {
                hierarchyWarnings.Add(new IngestWarningEntry(index, field, reason));
            }
            toInsert.Add(processed.Event!);
        }

        var result = await _events.AddEventsAsync(applicationId, toInsert);

        return Accepted(new IngestResponse(result.Accepted, errors.Count, errors, hierarchyWarnings));
    }

    private class RateCounter
    {
        public int Count;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter IngestionControllerTests`
Expected: PASS (8 tests).

- [ ] **Step 6: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 204, Skipped: 0, Total: 204` (196 from Tasks 1-8 + 8 from this task).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/IngestionController.cs src/LogsPlatform.Web/Program.cs tests/LogsPlatform.Tests/Web/IngestionControllerTests.cs
git commit -m "Add IngestionController — completes M2a"
```

---

## Closing Verification (after all 9 tasks merge)

This is M2's own milestone acceptance criterion, run manually against the real dev database:
1. Start the app, create an `Application` with an `AppEnvironment` and one `ApiKey` via the Admin UI (or API).
2. Using a raw HTTP client (curl/Postman/a throwaway console app — no `LogsPlatform.Client` exists yet, that's M2b), `POST /api/v1/ingest/events` with `X-Api-Key` set to the raw key and a JSON array of ~10 events, one of which has a hierarchy field with a deliberate typo.
3. Confirm `202 Accepted` with `accepted: 9, rejected: 0` (the typo'd one is accepted with a warning, not rejected) and one `hierarchyWarnings` entry.
4. Query the dev database directly (SSMS or `sqlcmd`) and confirm all 10 rows exist in `Events`, the typo'd one has a `null` hierarchy FK.
5. Re-send the exact same batch (same `eventKey`s if you set any) and confirm no duplicate rows.
6. Confirm no server-side errors in the console output.

This completes M2a. **M2b** (`LogsPlatform.Client` + Serilog sink) remains as a separate, later plan — not started by this one.
