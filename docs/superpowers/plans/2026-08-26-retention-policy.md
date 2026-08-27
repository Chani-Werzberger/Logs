# Event Retention Policy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Automatically delete `Event` rows older than a per-Application retention window, opt-in via a new nullable `Application.RetentionDays` field.

**Architecture:** A new `RetentionCleanupService : BackgroundService`, an exact structural mirror of the existing `AnalysisEngineBackgroundService` (daily `PeriodicTimer` instead of 5-minute, same `Interlocked` re-entrancy guard), reads every Application's `RetentionDays` each run and hard-deletes `Event` rows older than the resulting cutoff via a new `IEventRepository.DeleteOlderThanAsync`. A new `PUT /api/v1/admin/applications/{id}` (Super-Admin-only, RetentionDays only) plus an inline-editable field in `ApplicationsAdmin.razor` let an admin set the value.

**Tech Stack:** .NET 10 / EF Core 10 (`ExecuteDeleteAsync`, already available — no new packages).

## Global Constraints

- **Design doc:** `docs/superpowers/specs/2026-08-26-retention-policy-design.md` — read it before starting.
- **Scope is Events only** — no other table (`Finding`, `FindingStatement`, `Evidence`, `ExceptionGroup`, `AdminAuditLogEntry`) is touched by this plan.
- **`RetentionDays` is opt-in, not a global-with-override:** `null` means "keep forever" for that Application — there is no global fallback default. Do not add one.
- **Hard delete only, no batching, no archival.** A straight `ExecuteDeleteAsync()` per Application per run. This is a deliberate simplicity choice, not an oversight — do not add chunking/paging logic.
- **Logging only via `ILogger`, one line per Application per run when count > 0** — no `AdminAuditLogEntry` row for retention deletions (a deliberate, confirmed choice from brainstorming, distinct from every other mutation in this codebase which DOES get audited).
- **The new `PUT` endpoint accepts and updates ONLY `RetentionDays`** — this is not a general Application editor. `Name`/`Description` stay immutable after creation, exactly as they are today.
- **`ApplicationsController` stays Super-Admin-only** (its existing class-level `[Authorize(Policy = "RequireAdmin")]`, unchanged from V2 group B2's RBAC work, which deliberately left this controller untouched). The new `Update` action does NOT get a `CanManageApplicationAsync` per-application-grant check — Application-level settings are Super-Admin territory, matching `Create`.
- **Test convention for `RetentionCleanupService`:** mirror `AnalysisEngineBackgroundServiceTests.cs`'s exact pattern — a small hand-built `ServiceCollection` registering the real repository implementations against a shared `TestDatabase.CreateContext()`/`TestDatabase.CreateFactory()`, constructing the service directly (not via the full `TestWebApplicationFactory`), since this is a plain unit test of the service's own tick logic.
- **Frequent commits:** one commit per task.

---

## Task 1: `Application.RetentionDays` + repository methods

**Files:**
- Modify: `src/LogsPlatform.Domain/Entities/Application.cs`
- Modify: `src/LogsPlatform.Domain/Repositories/IApplicationRepository.cs`
- Modify: `src/LogsPlatform.Infrastructure/Repositories/ApplicationRepository.cs`
- Modify: `src/LogsPlatform.Domain/Repositories/IEventRepository.cs`
- Modify: `src/LogsPlatform.Infrastructure/Repositories/EventRepository.cs`
- Create (generated): `src/LogsPlatform.Infrastructure/Migrations/*_AddApplicationRetentionDays.cs` + `.Designer.cs`, updated `LogsPlatformDbContextModelSnapshot.cs`
- Modify: `tests/LogsPlatform.Tests/Infrastructure/ApplicationRepositoryTests.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/EventRepositoryRetentionTests.cs`

**Interfaces:**
- Produces: `Application.RetentionDays : int?` (new property). `IApplicationRepository.UpdateRetentionAsync(int id, int? retentionDays) : Task<Application?>`. `IEventRepository.DeleteOlderThanAsync(int applicationId, DateTime cutoffUtc) : Task<int>` — both consumed by Task 2 (`RetentionCleanupService`) and Task 3 (the controller/UI).

- [ ] **Step 1: Add the entity field**

In `src/LogsPlatform.Domain/Entities/Application.cs`, add this property after `CreatedAt`:

```csharp
    public int? RetentionDays { get; set; }
```

- [ ] **Step 2: Write the failing repository tests**

Add these two test methods to `tests/LogsPlatform.Tests/Infrastructure/ApplicationRepositoryTests.cs`, after the last existing test method, before the closing `}`:

```csharp
    [Fact]
    public async Task UpdateRetentionAsync_ExistingApplication_UpdatesAndReturnsIt()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new ApplicationRepository(TestDatabase.CreateFactory());
        var created = await repository.AddAsync(new Application { Name = "RetentionUpdateTestApp", CreatedAt = DateTime.UtcNow });

        var updated = await repository.UpdateRetentionAsync(created.Id, 30);

        Assert.NotNull(updated);
        Assert.Equal(30, updated!.RetentionDays);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.Equal(30, reloaded!.RetentionDays);
    }

    [Fact]
    public async Task UpdateRetentionAsync_NoSuchApplication_ReturnsNull()
    {
        var repository = new ApplicationRepository(TestDatabase.CreateFactory());

        var updated = await repository.UpdateRetentionAsync(999999, 30);

        Assert.Null(updated);
    }
```

Create `tests/LogsPlatform.Tests/Infrastructure/EventRepositoryRetentionTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure.Repositories;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class EventRepositoryRetentionTests
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
    public async Task DeleteOlderThanAsync_DeletesOnlyEventsOlderThanCutoffForGivenApplication()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "RetentionDeleteTestApp");
        var (otherAppId, otherEnvId) = await SeedAppEnvAsync(context, "RetentionDeleteOtherTestApp");

        var cutoff = DateTime.UtcNow.AddDays(-30);
        var oldEvent = new Event { ApplicationId = appId, EnvironmentId = envId, Timestamp = cutoff.AddDays(-1), Severity = 17, Message = "old" };
        var newEvent = new Event { ApplicationId = appId, EnvironmentId = envId, Timestamp = cutoff.AddDays(1), Severity = 17, Message = "new" };
        var otherAppOldEvent = new Event { ApplicationId = otherAppId, EnvironmentId = otherEnvId, Timestamp = cutoff.AddDays(-1), Severity = 17, Message = "other app old" };
        context.Events.AddRange(oldEvent, newEvent, otherAppOldEvent);
        await context.SaveChangesAsync();

        var repository = new EventRepository(context);
        var deletedCount = await repository.DeleteOlderThanAsync(appId, cutoff);

        Assert.Equal(1, deletedCount);
        var remaining = context.Events.Select(e => e.Id).ToHashSet();
        Assert.DoesNotContain(oldEvent.Id, remaining);
        Assert.Contains(newEvent.Id, remaining);
        Assert.Contains(otherAppOldEvent.Id, remaining);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_NoEventsOlderThanCutoff_ReturnsZero()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "RetentionDeleteNoneTestApp");
        var cutoff = DateTime.UtcNow.AddDays(-30);
        context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, Timestamp = cutoff.AddDays(1), Severity = 17, Message = "new" });
        await context.SaveChangesAsync();

        var repository = new EventRepository(context);
        var deletedCount = await repository.DeleteOlderThanAsync(appId, cutoff);

        Assert.Equal(0, deletedCount);
    }
}
```

Note: unlike most `Infrastructure` test files, this one needs `using LogsPlatform.Infrastructure;` for `LogsPlatformDbContext` itself — check the exact namespace by looking at an existing file in the same folder (e.g. `FindingRepositoryTests.cs` already imports it) and match it.

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet build`
Expected: build errors — `Application.RetentionDays`, `IApplicationRepository.UpdateRetentionAsync`, and `IEventRepository.DeleteOlderThanAsync` don't exist yet.

- [ ] **Step 4: Generate the migration**

Run:
```
dotnet ef migrations add AddApplicationRetentionDays --project src/LogsPlatform.Infrastructure --startup-project src/LogsPlatform.Infrastructure
```
Expected: a new `20260826HHMMSS_AddApplicationRetentionDays.cs` + `.Designer.cs` under `src/LogsPlatform.Infrastructure/Migrations/`, and `LogsPlatformDbContextModelSnapshot.cs` updated. Inspect the generated `Up()` — it must `AddColumn<int>` named `RetentionDays` on table `Applications`, nullable, no default value. If it doesn't match, the entity change in Step 1 has a mistake — fix it and regenerate (`dotnet ef migrations remove` then re-add) rather than hand-editing the generated file.

- [ ] **Step 5: Add UpdateRetentionAsync to the interface and repository**

In `src/LogsPlatform.Domain/Repositories/IApplicationRepository.cs`, add this line inside the interface, after `AddAsync`:

```csharp
    Task<Application?> UpdateRetentionAsync(int id, int? retentionDays);
```

In `src/LogsPlatform.Infrastructure/Repositories/ApplicationRepository.cs`, add this method after `AddAsync`, before the closing `}` of the class:

```csharp
    public async Task<Application?> UpdateRetentionAsync(int id, int? retentionDays)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var application = await context.Applications.FindAsync(id);
        if (application is null)
        {
            return null;
        }

        application.RetentionDays = retentionDays;
        await context.SaveChangesAsync();
        return application;
    }
```

- [ ] **Step 6: Add DeleteOlderThanAsync to the interface and repository**

In `src/LogsPlatform.Domain/Repositories/IEventRepository.cs`, add this line inside the interface, after `GetTimelineAsync`:

```csharp
    Task<int> DeleteOlderThanAsync(int applicationId, DateTime cutoffUtc);
```

In `src/LogsPlatform.Infrastructure/Repositories/EventRepository.cs`, add this method after `GetTimelineAsync`, before the closing `}` of the class:

```csharp
    public async Task<int> DeleteOlderThanAsync(int applicationId, DateTime cutoffUtc) =>
        await _context.Events
            .Where(e => e.ApplicationId == applicationId && e.Timestamp < cutoffUtc)
            .ExecuteDeleteAsync();
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ApplicationRepositoryTests|FullyQualifiedName~EventRepositoryRetentionTests"`
Expected: all passing (5 pre-existing `ApplicationRepositoryTests` + 2 new + 2 new `EventRepositoryRetentionTests` = 9 total; adjust expectation if the pre-existing count differs — the point is 0 failures).

- [ ] **Step 8: Commit**

```bash
git add src/LogsPlatform.Domain/Entities/Application.cs \
        src/LogsPlatform.Domain/Repositories/IApplicationRepository.cs \
        src/LogsPlatform.Infrastructure/Repositories/ApplicationRepository.cs \
        src/LogsPlatform.Domain/Repositories/IEventRepository.cs \
        src/LogsPlatform.Infrastructure/Repositories/EventRepository.cs \
        src/LogsPlatform.Infrastructure/Migrations/ \
        tests/LogsPlatform.Tests/Infrastructure/ApplicationRepositoryTests.cs \
        tests/LogsPlatform.Tests/Infrastructure/EventRepositoryRetentionTests.cs
git commit -m "feat: add per-application RetentionDays field and Event deletion query"
```

---

## Task 2: RetentionCleanupService

**Files:**
- Create: `src/LogsPlatform.Web/Services/Retention/RetentionCleanupService.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Create: `tests/LogsPlatform.Tests/Web/RetentionCleanupServiceTests.cs`

**Interfaces:**
- Consumes: `IApplicationRepository.GetAllAsync() : Task<IReadOnlyList<Application>>` (existing), `Application.RetentionDays : int?` (Task 1), `IEventRepository.DeleteOlderThanAsync(int, DateTime) : Task<int>` (Task 1).
- Produces: `RetentionCleanupService.TryRunOneCleanupAsync() : Task<bool>` — not consumed by any later task, but the class is registered as a hosted service so it runs automatically once deployed.

- [ ] **Step 1: Write the failing test**

Create `tests/LogsPlatform.Tests/Web/RetentionCleanupServiceTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class RetentionCleanupServiceTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> SeedAppEnvAsync(LogsPlatformDbContext context, string appName, int? retentionDays)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow, RetentionDays = retentionDays };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();
        return (app.Id, env.Id);
    }

    private static RetentionCleanupService BuildService(LogsPlatformDbContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton(TestDatabase.CreateFactory());
        services.AddSingleton<IApplicationRepository, ApplicationRepository>();
        services.AddSingleton<IEventRepository, EventRepository>();

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new RetentionCleanupService(scopeFactory, NullLogger<RetentionCleanupService>.Instance);
    }

    [Fact]
    public async Task TryRunOneCleanupAsync_DeletesOldEventsForApplicationsWithRetentionSet_LeavesOthersUntouched()
    {
        using var context = TestDatabase.CreateContext();
        var (retainedAppId, retainedEnvId) = await SeedAppEnvAsync(context, "RetentionCleanupRetainedTestApp", retentionDays: 30);
        var (foreverAppId, foreverEnvId) = await SeedAppEnvAsync(context, "RetentionCleanupForeverTestApp", retentionDays: null);

        var cutoff = DateTime.UtcNow.AddDays(-30);
        var oldRetainedEvent = new Event { ApplicationId = retainedAppId, EnvironmentId = retainedEnvId, Timestamp = cutoff.AddDays(-1), Severity = 17, Message = "old" };
        var newRetainedEvent = new Event { ApplicationId = retainedAppId, EnvironmentId = retainedEnvId, Timestamp = cutoff.AddDays(1), Severity = 17, Message = "new" };
        var oldForeverEvent = new Event { ApplicationId = foreverAppId, EnvironmentId = foreverEnvId, Timestamp = cutoff.AddDays(-1), Severity = 17, Message = "old but null retention" };
        context.Events.AddRange(oldRetainedEvent, newRetainedEvent, oldForeverEvent);
        await context.SaveChangesAsync();

        var service = BuildService(context);
        var result = await service.TryRunOneCleanupAsync();

        Assert.True(result);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var remainingIds = await verifyContext.Events.Select(e => e.Id).ToListAsync();

        Assert.DoesNotContain(oldRetainedEvent.Id, remainingIds);
        Assert.Contains(newRetainedEvent.Id, remainingIds);
        Assert.Contains(oldForeverEvent.Id, remainingIds);
    }

    [Fact]
    public async Task TryRunOneCleanupAsync_CalledWhileAlreadyRunning_SecondCallIsSkipped()
    {
        using var context = TestDatabase.CreateContext();
        await SeedAppEnvAsync(context, "RetentionCleanupConcurrentTestApp", retentionDays: null);

        var service = BuildService(context);

        var firstRun = service.TryRunOneCleanupAsync();
        var secondRunRan = await service.TryRunOneCleanupAsync();
        var firstRunRan = await firstRun;

        Assert.True(firstRunRan);
        Assert.False(secondRunRan);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build`
Expected: build error — `RetentionCleanupService` does not exist yet.

- [ ] **Step 3: Implement RetentionCleanupService**

Create `src/LogsPlatform.Web/Services/Retention/RetentionCleanupService.cs`:

```csharp
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
```

- [ ] **Step 4: Register in DI**

In `src/LogsPlatform.Web/Program.cs`, add this line immediately after `builder.Services.AddHostedService<LogsPlatform.Web.Services.Analysis.AnalysisEngineBackgroundService>();`:

```csharp
builder.Services.AddHostedService<LogsPlatform.Web.Services.Retention.RetentionCleanupService>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RetentionCleanupServiceTests"`
Expected: 2/2 passing.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Services/Retention/RetentionCleanupService.cs \
        src/LogsPlatform.Web/Program.cs \
        tests/LogsPlatform.Tests/Web/RetentionCleanupServiceTests.cs
git commit -m "feat: add daily RetentionCleanupService for per-application Event retention"
```

---

## Task 3: Admin API + UI to set RetentionDays

**Files:**
- Modify: `src/LogsPlatform.Web/Contracts/ApplicationContracts.cs`
- Modify: `src/LogsPlatform.Web/Controllers/ApplicationsController.cs`
- Modify: `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor`
- Modify: `tests/LogsPlatform.Tests/Web/ApplicationsControllerTests.cs`

**Interfaces:**
- Consumes: `IApplicationRepository.UpdateRetentionAsync(int id, int? retentionDays) : Task<Application?>` (Task 1).
- Produces: nothing — this is the last task.

- [ ] **Step 1: Extend the contracts**

In `src/LogsPlatform.Web/Contracts/ApplicationContracts.cs`, change:

```csharp
public record ApplicationResponse(int Id, string Name, string? Description, DateTime CreatedAt);
```

to:

```csharp
public record ApplicationResponse(int Id, string Name, string? Description, DateTime CreatedAt, int? RetentionDays);

public record UpdateApplicationRetentionRequest(int? RetentionDays);
```

- [ ] **Step 2: Write the failing controller tests**

Add these tests to `tests/LogsPlatform.Tests/Web/ApplicationsControllerTests.cs`, after the last existing test method, before the closing `}`:

```csharp
    [Fact]
    public async Task UpdateRetention_ValidRequest_UpdatesAndReturnsIt()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/applications",
            new CreateApplicationRequest("RetentionUpdateEndpointTestApp", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var updateResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{created!.Id}",
            new UpdateApplicationRetentionRequest(60));

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.Equal(60, updated!.RetentionDays);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{created.Id}");
        var fetched = await getResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.Equal(60, fetched!.RetentionDays);
    }

    [Fact]
    public async Task UpdateRetention_NoSuchApplication_Returns404()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/admin/applications/999999",
            new UpdateApplicationRetentionRequest(30));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRetention_NonAdminUser_Returns403()
    {
        var (client, _) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "RetentionUpdateNonAdminTestUser");
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var createResponse = await adminClient.PostAsJsonAsync(
            "/api/v1/admin/applications",
            new CreateApplicationRequest("RetentionUpdateNonAdminTestApp", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var response = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{created!.Id}",
            new UpdateApplicationRetentionRequest(30));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet build`
Expected: build errors — the 3 existing `new ApplicationResponse(...)` call sites in `ApplicationsController.cs` are now missing the 5th positional argument, and there's no `PUT` action yet.

- [ ] **Step 4: Update ApplicationsController**

Replace the full contents of `src/LogsPlatform.Web/Controllers/ApplicationsController.cs`:

```csharp
using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications")]
[Authorize(Policy = "RequireAdmin")]
public class ApplicationsController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly AuditLogger _audit;

    public ApplicationsController(IApplicationRepository applications, AuditLogger audit)
    {
        _applications = applications;
        _audit = audit;
    }

    [HttpPost]
    public async Task<ActionResult<ApplicationResponse>> Create(CreateApplicationRequest request)
    {
        try
        {
            var application = await _applications.AddAsync(new Application
            {
                Name = request.Name,
                Description = request.Description,
                CreatedAt = DateTime.UtcNow
            });

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            await _audit.RecordAsync(platformUserId, "Application", application.Id.ToString(), "Create", $"Created application '{application.Name}'");

            var response = new ApplicationResponse(application.Id, application.Name, application.Description, application.CreatedAt, application.RetentionDays);
            return CreatedAtAction(nameof(GetById), new { id = application.Id }, response);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return Conflict(new { message = $"An application named '{request.Name}' already exists." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApplicationResponse>> GetById(int id)
    {
        var application = await _applications.GetByIdAsync(id);
        if (application is null) return NotFound();
        return new ApplicationResponse(application.Id, application.Name, application.Description, application.CreatedAt, application.RetentionDays);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApplicationResponse>>> GetAll()
    {
        var applications = await _applications.GetAllAsync();
        return applications
            .Select(a => new ApplicationResponse(a.Id, a.Name, a.Description, a.CreatedAt, a.RetentionDays))
            .ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ApplicationResponse>> UpdateRetention(int id, UpdateApplicationRetentionRequest request)
    {
        var updated = await _applications.UpdateRetentionAsync(id, request.RetentionDays);
        if (updated is null) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "Application", id.ToString(), "Update", $"Set RetentionDays to {(request.RetentionDays.HasValue ? request.RetentionDays.Value.ToString() : "null (keep forever)")} for application {id}");

        return new ApplicationResponse(updated.Id, updated.Name, updated.Description, updated.CreatedAt, updated.RetentionDays);
    }
}
```

(Note: this action's own mutation IS still audit-logged via the existing `AuditLogger` — the "no `AdminAuditLogEntry` for retention deletions" constraint applies only to the `RetentionCleanupService`'s own automatic deletions, not to an admin's deliberate act of *setting* the policy, which follows the same audit convention every other admin mutation in this codebase already uses.)

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ApplicationsControllerTests"`
Expected: all passing (3 pre-existing + 3 new = 6 total).

- [ ] **Step 6: Add the inline-editable field to ApplicationsAdmin.razor**

In `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor`, change the table header from:

```razor
<table class="table table-striped table-hover align-middle">
    <thead>
        <tr>
            <th></th>
            <th></th>
            <th>שם</th>
            <th>תיאור</th>
            <th>נוצר בתאריך</th>
        </tr>
    </thead>
```

to:

```razor
<table class="table table-striped table-hover align-middle">
    <thead>
        <tr>
            <th></th>
            <th></th>
            <th>שם</th>
            <th>תיאור</th>
            <th>נוצר בתאריך</th>
            <th>שמירת Events (ימים)</th>
        </tr>
    </thead>
```

Change the row body from:

```razor
            <tr @key="application.Id">
                <td>
                    <button class="btn btn-sm btn-outline-secondary" @onclick="() => ToggleExpandAsync(application.Id)">
                        @(_expandedAppIds.Contains(application.Id) ? "-" : "+")
                    </button>
                </td>
                <td><a href="/admin/applications/@application.Id/modules">מודולים</a></td>
                <td>@application.Name</td>
                <td>@application.Description</td>
                <td>@application.CreatedAt</td>
            </tr>
```

to:

```razor
            <tr @key="application.Id">
                <td>
                    <button class="btn btn-sm btn-outline-secondary" @onclick="() => ToggleExpandAsync(application.Id)">
                        @(_expandedAppIds.Contains(application.Id) ? "-" : "+")
                    </button>
                </td>
                <td><a href="/admin/applications/@application.Id/modules">מודולים</a></td>
                <td>@application.Name</td>
                <td>@application.Description</td>
                <td>@application.CreatedAt</td>
                <td>
                    @if (_isSuperAdmin)
                    {
                        <div class="input-group input-group-sm" style="width: 10rem;">
                            <input type="number" min="1" class="form-control form-control-sm"
                                   value="@application.RetentionDays"
                                   placeholder="לנצח"
                                   @onchange="@(e => UpdateRetentionAsync(application.Id, e.Value as string))" />
                        </div>
                    }
                    else
                    {
                        @(application.RetentionDays?.ToString() ?? "לנצח")
                    }
                    @if (_retentionErrors.TryGetValue(application.Id, out var retentionError) && retentionError is not null)
                    {
                        <div class="text-danger small">@retentionError</div>
                    }
                </td>
            </tr>
```

Add this field alongside the other `Dictionary`-typed fields (after `_environmentErrors`):

```csharp
    private readonly Dictionary<int, string?> _retentionErrors = new();
```

Add this method after `CreateEnvironmentAsync`:

```csharp
    private async Task UpdateRetentionAsync(int applicationId, string? rawValue)
    {
        _retentionErrors[applicationId] = null;

        int? retentionDays;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            retentionDays = null;
        }
        else if (int.TryParse(rawValue, out var parsed) && parsed > 0)
        {
            retentionDays = parsed;
        }
        else
        {
            _retentionErrors[applicationId] = "יש להזין מספר ימים חיובי, או להשאיר ריק לשמירה לנצח.";
            return;
        }

        var updated = await ApplicationRepository.UpdateRetentionAsync(applicationId, retentionDays);
        if (updated is null)
        {
            _retentionErrors[applicationId] = "האפליקציה לא נמצאה.";
            return;
        }

        await Audit.RecordAsync(_currentPlatformUserId, "Application", applicationId.ToString(), "Update", $"Set RetentionDays to {(retentionDays.HasValue ? retentionDays.Value.ToString() : "null (keep forever)")} for application {applicationId}");

        _applications = (await ApplicationRepository.GetAllAsync()).ToList();
    }
```

This calls `IApplicationRepository.UpdateRetentionAsync` directly (matching this project's established Blazor-bypasses-controller convention for admin components), and records its own `AuditLogger` call the same way `CreateApplicationAsync`/`CreateEnvironmentAsync` already do in this same file — no `CanManageApplicationAsync` check, matching the controller's own Super-Admin-only stance (the form/input is only rendered at all when `_isSuperAdmin`, so a non-Super-Admin never sees an editable field to submit in the first place).

- [ ] **Step 7: Build and run the full test suite**

Run: `dotnet build` then `dotnet test`
Expected: 0 build errors, all tests green — this closes out all 3 tasks.

- [ ] **Step 8: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/ApplicationContracts.cs \
        src/LogsPlatform.Web/Controllers/ApplicationsController.cs \
        src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor \
        tests/LogsPlatform.Tests/Web/ApplicationsControllerTests.cs
git commit -m "feat: add admin API and UI to set per-application RetentionDays"
```

---

## Final Verification

- [ ] Run `dotnet build` — 0 errors.
- [ ] Run `dotnet test` — full suite green, including all new tests across the 3 tasks.
- [ ] Confirm via `git diff main --stat` that no file outside this plan's own list was touched, and that no other `EvidenceType`/`FindingStatus`-style enum or unrelated table was modified — this plan's blast radius is `Application`/`Event` only.
- [ ] Manually verify via a live request or the Blazor UI (as Super Admin) that setting a Application's RetentionDays to a small number (e.g. 1) and then manually triggering `RetentionCleanupService.TryRunOneCleanupAsync()` (or waiting for its daily tick) actually removes old Events for that Application and leaves others untouched — matches the design doc's own manual/Playwright verification note, since there's no bUnit harness for the UI half.
- [ ] Invoke `superpowers:finishing-a-development-branch`.
