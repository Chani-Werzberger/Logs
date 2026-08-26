# Per-Application RBAC Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a Super Admin grant a non-Super-Admin `PlatformUser` management access to specific `Application`s, replacing the current all-or-nothing `IsAdmin` boolean for the 11 mutating admin surfaces (both API controllers and their Blazor UI counterparts), while investigation/read endpoints stay open to all authenticated users exactly as today.

**Architecture:** A new `PlatformUserApplicationGrant` join entity (binary grant, no role levels) backs a new `ApplicationAccessGrantRepository`. A new `ApplicationAccessService.CanManageApplicationAsync(bool isSuperAdmin, int platformUserId, int applicationId)` composes "IsAdmin overrides everything" with "or a matching grant exists." All 11 controllers lose their class-level `[Authorize(Policy = "RequireAdmin")]` (falling back to "any authenticated user") and gain an explicit `CanManageApplicationAsync` check before every mutation; their 11 Blazor counterparts get the identical check inline before each mutation method runs. `NavMenu.razor`'s "ניהול" link becomes visible to grant-holders too. `PlatformUsersSection.razor` gains a grant-management UI.

**Tech Stack:** .NET 10 / EF Core 10 / SQL Server / ASP.NET Core MVC controllers / Blazor Server (InteractiveServer). No new NuGet packages.

## Global Constraints

- **Design doc:** `docs/superpowers/specs/2026-08-25-rbac-per-application-design.md` — read it before starting; every task below implements one of its sections.
- **Scope of protection (design doc §2, exact):** protected = Create/Update/Deactivate/Revoke on the 11 entities (`AppEnvironment`, `ApiKey`, `Customer`, `AppUser`, `LogSource`, `Deployment`, `AppVersion`, `AppModule`, `ScreenService`, `ProcessNode`, `Operation`). Unprotected, unchanged = everything else, including every `GetAll`/`GetById` on these same 11 controllers. The design doc's own text ("לא מוגן: כל פעולות הקריאה/חקירה... וכו'") is a general category, not a closed list of examples — so read/listing actions on these 11 controllers stay exactly as they are today (reachable once the class-level `[Authorize]` is removed, with **no new access check added to them**). Do not add `CanManageApplicationAsync` to any `GetAll`/`GetById` method in this plan.
- **Untouched routes (design doc §2, exact):** `ApplicationsController` (all 3 actions, including `Create`), `AuditLogController`, `PlatformUsersAdmin.razor`'s own page attribute, and `PlatformUsersSection.razor`'s existing user CRUD all stay Super-Admin-only. Do not remove their `[Authorize(Policy = "RequireAdmin")]`.
- **Decisive fact (self-verified 2026-08-25 against live `Program.cs`):** the app's fallback authorization policy (applied whenever no explicit `[Authorize]` is present) is `RequireAuthenticatedUser()` only — no `IsAdmin` claim required. Removing `[Authorize(Policy = "RequireAdmin")]` from a controller/page therefore relaxes it to "any authenticated user," never to "anonymous."
- **Resolved implementation decision — no interface for the access service:** the design doc calls it `IApplicationAccessService`, but this project's own established convention for a thin, non-mocked, composed policy service is a concrete class injected directly with no interface — see `AuditLogger` (`src/LogsPlatform.Web/Services/AuditLogger.cs`), which wraps `IAuditLogRepository` the same way this wraps `IApplicationAccessGrantRepository` and has no interface of its own. Build `ApplicationAccessService` as a concrete class in `LogsPlatform.Web.Services`, registered via `AddScoped<ApplicationAccessService>()`, injected as the concrete type everywhere (matching `AuditLogger`'s own registration one line above it in `Program.cs`). The repository underneath it (`IApplicationAccessGrantRepository`/`ApplicationAccessGrantRepository`) DOES get an interface, matching `IAuditLogRepository`/`AuditLogRepository`.
- **Resolved implementation decision — signature takes `isSuperAdmin` as a caller-supplied bool, not a DB lookup:** `IPlatformUserRepository` has no `GetByIdAsync` (confirmed by reading it fresh: `GetByUsernameAsync`, `GetAllAsync`, `AddAsync`, `DeactivateAsync`, `AnyAsync` only). Every caller (controller or Blazor component) already has the `"IsAdmin"` claim on hand for free from the auth cookie — reading it costs nothing, whereas adding a `GetByIdAsync` to look it up from the DB would be new, unrequested repository surface. So `CanManageApplicationAsync(bool isSuperAdmin, int platformUserId, int applicationId)` takes `isSuperAdmin` as a parameter, always read by the caller as `User.FindFirstValue("IsAdmin") == "true"` (controllers) or `authState.User.FindFirstValue("IsAdmin") == "true"` (Blazor) — the exact literal claim type `"IsAdmin"` used by `AuthController.Login` today, not a `ClaimTypes.*` constant.
- **No Blazor component test harness exists in this project (no bUnit).** Blazor changes in Tasks 6–8 are verified by build success + the existing full test suite (which exercises the underlying repositories/services the components call) + one manual Playwright script at the end of Task 8, matching exactly how the B1 UI-wiring fix was verified live. Do not introduce bUnit or any new test framework.
- **appId resolution is direct from the route for 8 of the 11 controllers** (`EnvironmentsController`, `ApiKeysController`, `CustomersController`, `AppUsersController`, `LogSourcesController`, `DeploymentsController`, `VersionsController`, `ModulesController` — all routed under `/applications/{appId:int}/...`). It requires walking an existing FK for the other 3: `ScreenServicesController` (route has `moduleId`; `AppModule.ApplicationId` is already loaded by the controller's existing "module exists" check), `ProcessesController` (route has `screenServiceId`; needs one extra `IAppModuleRepository` lookup via the loaded `ScreenService.ModuleId`), `OperationsController` (route has `processId`; needs one extra `IScreenServiceRepository` lookup and one extra `IAppModuleRepository` lookup, chained through `ProcessNode.ScreenServiceId` → `ScreenService.ModuleId` → `AppModule.ApplicationId`). All 5 Blazor entry pages (`ApplicationsAdmin`, `ModulesAdmin`, `ScreenServicesAdmin`, `ProcessesAdmin`, `OperationsAdmin`) already carry `AppId` as a page route parameter directly (confirmed by reading each fresh) — no chain-walking needed on the Blazor side at all.
- **Claim reading convention (matches existing controllers exactly):** `var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);` — already present in every mutating action; add the super-admin read and the access check immediately after the existing not-found checks and before the mutation, in that order. On failure: controllers return `Forbid()`; Blazor sets the same `_createError`/`_editError` string field the component already uses and returns without mutating.
- **Migration naming convention (matches existing migrations):** run `dotnet ef migrations add AddPlatformUserApplicationGrant --project src/LogsPlatform.Infrastructure --startup-project src/LogsPlatform.Web` — do not hand-author the migration's `Designer.cs`/snapshot; let the EF CLI generate them, then inspect the generated `Up()`/`Down()` against this plan's DbContext changes.
- **Test convention (matches B1's "representative coverage, not exhaustive per-controller"):** one 3-test file per controller group (super-admin succeeds, grant-holder succeeds, no-grant returns 403), using ONE representative controller per group, not all controllers in the group. Grants are seeded directly via `IApplicationAccessGrantRepository` in the test (scope-resolved), never via HTTP — the design doc is explicit that there is no grant API endpoint, only the Blazor UI built in Task 8.
- **Frequent commits:** one commit per task, following this session's established pattern.

---

## Task 1: Foundation — grant entity, repository, access service, migration, DI, test helper

**Files:**
- Create: `src/LogsPlatform.Domain/Entities/PlatformUserApplicationGrant.cs`
- Create: `src/LogsPlatform.Domain/Repositories/IApplicationAccessGrantRepository.cs`
- Create: `src/LogsPlatform.Infrastructure/Repositories/ApplicationAccessGrantRepository.cs`
- Create: `src/LogsPlatform.Web/Services/ApplicationAccessService.cs`
- Modify: `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Modify: `tests/LogsPlatform.Tests/Infrastructure/AuthenticatedTestClientHelper.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/ApplicationAccessGrantRepositoryTests.cs`
- Create: `tests/LogsPlatform.Tests/Web/ApplicationAccessServiceTests.cs`
- Create (generated): `src/LogsPlatform.Infrastructure/Migrations/*_AddPlatformUserApplicationGrant.cs` + `.Designer.cs`, and updated `LogsPlatformDbContextModelSnapshot.cs`

**Interfaces:**
- Produces: `PlatformUserApplicationGrant { int Id; int PlatformUserId; PlatformUser PlatformUser; int ApplicationId; Application Application; }`
- Produces: `IApplicationAccessGrantRepository.HasGrantAsync(int platformUserId, int applicationId) : Task<bool>`, `.HasAnyGrantAsync(int platformUserId) : Task<bool>`, `.GetGrantedApplicationIdsAsync(int platformUserId) : Task<IReadOnlyList<int>>`, `.GrantAsync(int platformUserId, int applicationId) : Task`, `.RevokeAsync(int platformUserId, int applicationId) : Task`
- Produces: `ApplicationAccessService.CanManageApplicationAsync(bool isSuperAdmin, int platformUserId, int applicationId) : Task<bool>` — consumed by every task from Task 2 onward.
- Produces: `AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory, string username) : Task<(HttpClient Client, int PlatformUserId)>` — consumed by every test file from Task 2 onward.

- [ ] **Step 1: Create the entity**

```csharp
// src/LogsPlatform.Domain/Entities/PlatformUserApplicationGrant.cs
namespace LogsPlatform.Domain.Entities;

public class PlatformUserApplicationGrant
{
    public int Id { get; set; }
    public int PlatformUserId { get; set; }
    public PlatformUser PlatformUser { get; set; } = null!;
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
}
```

- [ ] **Step 2: Create the repository interface**

```csharp
// src/LogsPlatform.Domain/Repositories/IApplicationAccessGrantRepository.cs
namespace LogsPlatform.Domain.Repositories;

public interface IApplicationAccessGrantRepository
{
    Task<bool> HasGrantAsync(int platformUserId, int applicationId);
    Task<bool> HasAnyGrantAsync(int platformUserId);
    Task<IReadOnlyList<int>> GetGrantedApplicationIdsAsync(int platformUserId);
    Task GrantAsync(int platformUserId, int applicationId);
    Task RevokeAsync(int platformUserId, int applicationId);
}
```

- [ ] **Step 3: Add the DbSet and OnModelCreating block**

In `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs`, add the DbSet after `AdminAuditLogEntries`:

```csharp
    public DbSet<AdminAuditLogEntry> AdminAuditLogEntries => Set<AdminAuditLogEntry>();
    public DbSet<PlatformUserApplicationGrant> PlatformUserApplicationGrants => Set<PlatformUserApplicationGrant>();
```

And add this block after the `AdminAuditLogEntry` entity block inside `OnModelCreating`, immediately before the final closing braces of the method:

```csharp
        modelBuilder.Entity<PlatformUserApplicationGrant>(entity =>
        {
            entity.HasOne(g => g.PlatformUser)
                .WithMany()
                .HasForeignKey(g => g.PlatformUserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(g => g.Application)
                .WithMany()
                .HasForeignKey(g => g.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(g => new { g.PlatformUserId, g.ApplicationId }).IsUnique();
        });
```

- [ ] **Step 4: Generate the migration**

Run:
```
dotnet ef migrations add AddPlatformUserApplicationGrant --project src/LogsPlatform.Infrastructure --startup-project src/LogsPlatform.Web
```
Expected: a new `20260825HHMMSS_AddPlatformUserApplicationGrant.cs` + `.Designer.cs` appear under `src/LogsPlatform.Infrastructure/Migrations/`, and `LogsPlatformDbContextModelSnapshot.cs` is updated to include the new table. Inspect the generated `Up()` — it must create table `PlatformUserApplicationGrants` with columns `Id` (identity PK), `PlatformUserId` (int, FK to `PlatformUsers`, cascade), `ApplicationId` (int, FK to `Applications`, cascade), and a unique index on `(PlatformUserId, ApplicationId)`. If any of these don't match, the `OnModelCreating` block in Step 3 has a mistake — fix it and regenerate (`dotnet ef migrations remove` then re-add) rather than hand-editing the generated file.

- [ ] **Step 5: Implement the repository**

```csharp
// src/LogsPlatform.Infrastructure/Repositories/ApplicationAccessGrantRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ApplicationAccessGrantRepository : IApplicationAccessGrantRepository
{
    private readonly IDbContextFactory<LogsPlatformDbContext> _contextFactory;

    public ApplicationAccessGrantRepository(IDbContextFactory<LogsPlatformDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<bool> HasGrantAsync(int platformUserId, int applicationId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.PlatformUserApplicationGrants
            .AnyAsync(g => g.PlatformUserId == platformUserId && g.ApplicationId == applicationId);
    }

    public async Task<bool> HasAnyGrantAsync(int platformUserId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.PlatformUserApplicationGrants.AnyAsync(g => g.PlatformUserId == platformUserId);
    }

    public async Task<IReadOnlyList<int>> GetGrantedApplicationIdsAsync(int platformUserId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.PlatformUserApplicationGrants
            .Where(g => g.PlatformUserId == platformUserId)
            .Select(g => g.ApplicationId)
            .ToListAsync();
    }

    public async Task GrantAsync(int platformUserId, int applicationId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var exists = await context.PlatformUserApplicationGrants
            .AnyAsync(g => g.PlatformUserId == platformUserId && g.ApplicationId == applicationId);
        if (exists) return;

        context.PlatformUserApplicationGrants.Add(new PlatformUserApplicationGrant
        {
            PlatformUserId = platformUserId,
            ApplicationId = applicationId
        });
        await context.SaveChangesAsync();
    }

    public async Task RevokeAsync(int platformUserId, int applicationId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var grant = await context.PlatformUserApplicationGrants
            .SingleOrDefaultAsync(g => g.PlatformUserId == platformUserId && g.ApplicationId == applicationId);
        if (grant is null) return;

        context.PlatformUserApplicationGrants.Remove(grant);
        await context.SaveChangesAsync();
    }
}
```

- [ ] **Step 6: Implement the access service**

```csharp
// src/LogsPlatform.Web/Services/ApplicationAccessService.cs
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services;

public class ApplicationAccessService
{
    private readonly IApplicationAccessGrantRepository _grants;

    public ApplicationAccessService(IApplicationAccessGrantRepository grants)
    {
        _grants = grants;
    }

    public async Task<bool> CanManageApplicationAsync(bool isSuperAdmin, int platformUserId, int applicationId)
    {
        if (isSuperAdmin) return true;
        return await _grants.HasGrantAsync(platformUserId, applicationId);
    }
}
```

- [ ] **Step 7: Register in DI**

In `src/LogsPlatform.Web/Program.cs`, add these two lines immediately after `builder.Services.AddScoped<AuditLogger>();`:

```csharp
builder.Services.AddScoped<IApplicationAccessGrantRepository, ApplicationAccessGrantRepository>();
builder.Services.AddScoped<ApplicationAccessService>();
```

- [ ] **Step 8: Add the non-admin authenticated test client helper**

In `tests/LogsPlatform.Tests/Infrastructure/AuthenticatedTestClientHelper.cs`, add this method inside the existing `AuthenticatedTestClientHelper` static class, after `CreateAuthenticatedClientAsync`:

```csharp
    private const string TestNonAdminPassword = "Test-Password-123!";

    public static async Task<(HttpClient Client, int PlatformUserId)> CreateNonAdminAuthenticatedClientAsync<TEntryPoint>(
        WebApplicationFactory<TEntryPoint> factory, string username) where TEntryPoint : class
    {
        int platformUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
            var user = new PlatformUser
            {
                Username = username,
                PasswordHash = PasswordHasher.Hash(TestNonAdminPassword),
                IsAdmin = false,
                CreatedAt = DateTime.UtcNow
            };
            context.PlatformUsers.Add(user);
            await context.SaveChangesAsync();
            platformUserId = user.Id;
        }

        var client = factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(username, TestNonAdminPassword));
        if (loginResponse.StatusCode != System.Net.HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException(
                $"AuthenticatedTestClientHelper: non-admin test login failed with {loginResponse.StatusCode} for user '{username}'.");
        }

        return (client, platformUserId);
    }
```

- [ ] **Step 9: Write the repository tests**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/ApplicationAccessGrantRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ApplicationAccessGrantRepositoryTests
{
    private static async Task<(PlatformUser User, Application App)> SeedAsync(LogsPlatformDbContext context, string username)
    {
        var user = new PlatformUser { Username = username, PasswordHash = "hash", IsAdmin = false, CreatedAt = DateTime.UtcNow };
        var app = new Application { Name = $"{username}-App", CreatedAt = DateTime.UtcNow };
        context.PlatformUsers.Add(user);
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        return (user, app);
    }

    [Fact]
    public async Task GrantAsync_ThenHasGrantAsync_ReturnsTrue()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "GrantRepoAddTestUser");
        var repository = new ApplicationAccessGrantRepository(TestDatabase.CreateFactory());

        await repository.GrantAsync(user.Id, app.Id);

        Assert.True(await repository.HasGrantAsync(user.Id, app.Id));
    }

    [Fact]
    public async Task HasGrantAsync_NoGrant_ReturnsFalse()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "GrantRepoNoGrantTestUser");
        var repository = new ApplicationAccessGrantRepository(TestDatabase.CreateFactory());

        Assert.False(await repository.HasGrantAsync(user.Id, app.Id));
    }

    [Fact]
    public async Task GrantAsync_CalledTwice_DoesNotDuplicate()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "GrantRepoDuplicateTestUser");
        var repository = new ApplicationAccessGrantRepository(TestDatabase.CreateFactory());

        await repository.GrantAsync(user.Id, app.Id);
        await repository.GrantAsync(user.Id, app.Id);

        var granted = await repository.GetGrantedApplicationIdsAsync(user.Id);
        Assert.Single(granted);
    }

    [Fact]
    public async Task RevokeAsync_RemovesGrant()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "GrantRepoRevokeTestUser");
        var repository = new ApplicationAccessGrantRepository(TestDatabase.CreateFactory());
        await repository.GrantAsync(user.Id, app.Id);

        await repository.RevokeAsync(user.Id, app.Id);

        Assert.False(await repository.HasGrantAsync(user.Id, app.Id));
    }

    [Fact]
    public async Task HasAnyGrantAsync_WithAtLeastOneGrant_ReturnsTrue()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "GrantRepoHasAnyTestUser");
        var repository = new ApplicationAccessGrantRepository(TestDatabase.CreateFactory());
        await repository.GrantAsync(user.Id, app.Id);

        Assert.True(await repository.HasAnyGrantAsync(user.Id));
    }

    [Fact]
    public async Task HasAnyGrantAsync_NoGrants_ReturnsFalse()
    {
        using var context = TestDatabase.CreateContext();
        var (user, _) = await SeedAsync(context, "GrantRepoHasAnyFalseTestUser");
        var repository = new ApplicationAccessGrantRepository(TestDatabase.CreateFactory());

        Assert.False(await repository.HasAnyGrantAsync(user.Id));
    }
}
```

- [ ] **Step 10: Write the access service tests**

```csharp
// tests/LogsPlatform.Tests/Web/ApplicationAccessServiceTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ApplicationAccessServiceTests
{
    private static async Task<(PlatformUser User, Application App)> SeedAsync(LogsPlatformDbContext context, string username, bool isAdmin)
    {
        var user = new PlatformUser { Username = username, PasswordHash = "hash", IsAdmin = isAdmin, CreatedAt = DateTime.UtcNow };
        var app = new Application { Name = $"{username}-App", CreatedAt = DateTime.UtcNow };
        context.PlatformUsers.Add(user);
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        return (user, app);
    }

    [Fact]
    public async Task CanManageApplicationAsync_SuperAdmin_AlwaysTrue()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "AccessServiceSuperAdminTest", isAdmin: true);
        var service = new ApplicationAccessService(new ApplicationAccessGrantRepository(TestDatabase.CreateFactory()));

        var result = await service.CanManageApplicationAsync(isSuperAdmin: true, user.Id, app.Id);

        Assert.True(result);
    }

    [Fact]
    public async Task CanManageApplicationAsync_NonAdminWithGrant_ReturnsTrue()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "AccessServiceGrantedTest", isAdmin: false);
        var grantRepository = new ApplicationAccessGrantRepository(TestDatabase.CreateFactory());
        await grantRepository.GrantAsync(user.Id, app.Id);
        var service = new ApplicationAccessService(grantRepository);

        var result = await service.CanManageApplicationAsync(isSuperAdmin: false, user.Id, app.Id);

        Assert.True(result);
    }

    [Fact]
    public async Task CanManageApplicationAsync_NonAdminWithoutGrant_ReturnsFalse()
    {
        using var context = TestDatabase.CreateContext();
        var (user, app) = await SeedAsync(context, "AccessServiceNoGrantTest", isAdmin: false);
        var service = new ApplicationAccessService(new ApplicationAccessGrantRepository(TestDatabase.CreateFactory()));

        var result = await service.CanManageApplicationAsync(isSuperAdmin: false, user.Id, app.Id);

        Assert.False(result);
    }
}
```

- [ ] **Step 11: Run the full suite**

Run: `dotnet test`
Expected: all previous tests still pass, plus the 9 new tests from Steps 9–10 (6 repository + 3 service), all green.

- [ ] **Step 12: Commit**

```bash
git add src/LogsPlatform.Domain/Entities/PlatformUserApplicationGrant.cs \
        src/LogsPlatform.Domain/Repositories/IApplicationAccessGrantRepository.cs \
        src/LogsPlatform.Infrastructure/Repositories/ApplicationAccessGrantRepository.cs \
        src/LogsPlatform.Web/Services/ApplicationAccessService.cs \
        src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs \
        src/LogsPlatform.Infrastructure/Migrations/ \
        src/LogsPlatform.Web/Program.cs \
        tests/LogsPlatform.Tests/Infrastructure/AuthenticatedTestClientHelper.cs \
        tests/LogsPlatform.Tests/Infrastructure/ApplicationAccessGrantRepositoryTests.cs \
        tests/LogsPlatform.Tests/Web/ApplicationAccessServiceTests.cs
git commit -m "feat: add per-application access grant foundation (entity, repository, service)"
```

---

## Task 2: Group A controllers — EnvironmentsController, ApiKeysController

**Files:**
- Modify: `src/LogsPlatform.Web/Controllers/EnvironmentsController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/ApiKeysController.cs`
- Create: `tests/LogsPlatform.Tests/Web/ApplicationAccessGroupATests.cs`

**Interfaces:**
- Consumes: `ApplicationAccessService.CanManageApplicationAsync(bool isSuperAdmin, int platformUserId, int applicationId) : Task<bool>` (Task 1). `IApplicationAccessGrantRepository.GrantAsync(int platformUserId, int applicationId) : Task` (Task 1, used directly by the test to seed grants).
- Produces: nothing new consumed by later tasks — Groups A–D are independent of each other.

- [ ] **Step 1: Update EnvironmentsController**

Replace the full contents of `src/LogsPlatform.Web/Controllers/EnvironmentsController.cs`:

```csharp
using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/environments")]
public class EnvironmentsController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppEnvironmentRepository _environments;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public EnvironmentsController(
        IApplicationRepository applications,
        IAppEnvironmentRepository environments,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _applications = applications;
        _environments = environments;
        _audit = audit;
        _access = access;
    }

    [HttpPost]
    public async Task<ActionResult<EnvironmentResponse>> Create(int appId, CreateEnvironmentRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        var environment = await _environments.AddAsync(new AppEnvironment
        {
            ApplicationId = appId,
            Name = request.Name,
            IsProduction = request.IsProduction
        });

        await _audit.RecordAsync(platformUserId, "AppEnvironment", environment.Id.ToString(), "Create", $"Created environment '{environment.Name}' in application {appId}");

        var response = new EnvironmentResponse(environment.Id, environment.ApplicationId, environment.Name, environment.IsProduction);
        return CreatedAtAction(nameof(GetAll), new { appId }, response);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EnvironmentResponse>>> GetAll(int appId)
    {
        var environments = await _environments.GetByApplicationIdAsync(appId);
        return environments
            .Select(e => new EnvironmentResponse(e.Id, e.ApplicationId, e.Name, e.IsProduction))
            .ToList();
    }
}
```

- [ ] **Step 2: Update ApiKeysController**

Replace the full contents of `src/LogsPlatform.Web/Controllers/ApiKeysController.cs`:

```csharp
using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/api-keys")]
public class ApiKeysController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IApiKeyRepository _apiKeys;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public ApiKeysController(
        IApplicationRepository applications,
        IApiKeyRepository apiKeys,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _applications = applications;
        _apiKeys = apiKeys;
        _audit = audit;
        _access = access;
    }

    [HttpPost]
    public async Task<ActionResult<CreateApiKeyResponse>> Create(int appId, CreateApiKeyRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        var (apiKey, rawKey) = await _apiKeys.AddAsync(appId, request.Label);

        await _audit.RecordAsync(platformUserId, "ApiKey", apiKey.Id.ToString(), "Create", $"Created API key '{apiKey.Label}' in application {appId}");

        var response = new CreateApiKeyResponse(apiKey.Id, apiKey.ApplicationId, apiKey.Label, apiKey.CreatedAt, rawKey);
        return CreatedAtAction(nameof(GetById), new { appId, id = apiKey.Id }, response);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiKeyResponse>> GetById(int appId, int id)
    {
        var apiKey = await _apiKeys.GetByIdAsync(id);
        if (apiKey is null || apiKey.ApplicationId != appId) return NotFound();
        return ToResponse(apiKey);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApiKeyResponse>>> GetAll(int appId, [FromQuery] bool includeRevoked = false)
    {
        var apiKeys = await _apiKeys.GetByApplicationIdAsync(appId, includeRevoked);
        return apiKeys.Select(ToResponse).ToList();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Revoke(int appId, int id)
    {
        var existing = await _apiKeys.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        await _apiKeys.RevokeAsync(id);

        await _audit.RecordAsync(platformUserId, "ApiKey", id.ToString(), "Revoke", $"Revoked API key {id} in application {appId}");

        return NoContent();
    }

    private static ApiKeyResponse ToResponse(ApiKey apiKey) =>
        new(apiKey.Id, apiKey.ApplicationId, apiKey.Label, apiKey.CreatedAt, apiKey.RevokedAt);
}
```

- [ ] **Step 3: Run these two controllers' pre-existing tests**

Run: `dotnet test --filter "FullyQualifiedName~AuditLogWiringGroupATests"`
Expected: still all passing — the access check runs before the mutation and passes for the Super Admin client `AuditLogWiringGroupATests` already uses, so behavior for Super Admins is unchanged.

- [ ] **Step 4: Write the Group A access tests**

```csharp
// tests/LogsPlatform.Tests/Web/ApplicationAccessGroupATests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ApplicationAccessGroupATests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ApplicationAccessGroupATests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task EnvironmentCreate_SuperAdmin_Succeeds()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupASuperAdminApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var response = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task EnvironmentCreate_NonAdminWithGrant_Succeeds()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupAGrantedApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var (nonAdminClient, platformUserId) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "AccessGroupAGrantedUser");
        using (var scope = _factory.Services.CreateScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<IApplicationAccessGrantRepository>();
            await grants.GrantAsync(platformUserId, app!.Id);
        }

        var response = await nonAdminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task EnvironmentCreate_NonAdminWithoutGrant_ReturnsForbidden()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupANoGrantApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var (nonAdminClient, _) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "AccessGroupANoGrantUser");

        var response = await nonAdminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
```

- [ ] **Step 5: Run the new tests**

Run: `dotnet test --filter "FullyQualifiedName~ApplicationAccessGroupATests"`
Expected: 3/3 passing.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/EnvironmentsController.cs \
        src/LogsPlatform.Web/Controllers/ApiKeysController.cs \
        tests/LogsPlatform.Tests/Web/ApplicationAccessGroupATests.cs
git commit -m "feat: enforce per-application access grants on Environment/ApiKey controllers"
```

---

## Task 3: Group B controllers — CustomersController, AppUsersController, LogSourcesController

**Files:**
- Modify: `src/LogsPlatform.Web/Controllers/CustomersController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/AppUsersController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/LogSourcesController.cs`
- Create: `tests/LogsPlatform.Tests/Web/ApplicationAccessGroupBTests.cs`

**Interfaces:**
- Consumes: `ApplicationAccessService.CanManageApplicationAsync(bool isSuperAdmin, int platformUserId, int applicationId) : Task<bool>` (Task 1).
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Update CustomersController**

Replace the full contents of `src/LogsPlatform.Web/Controllers/CustomersController.cs`:

```csharp
using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/customers")]
public class CustomersController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly ICustomerRepository _customers;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public CustomersController(
        IApplicationRepository applications,
        ICustomerRepository customers,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _applications = applications;
        _customers = customers;
        _audit = audit;
        _access = access;
    }

    [HttpPost]
    public async Task<ActionResult<CustomerResponse>> Create(int appId, CreateCustomerRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        try
        {
            var customer = await _customers.AddAsync(new Customer
            {
                ApplicationId = appId,
                ExternalCustomerId = request.ExternalCustomerId,
                Name = request.Name
            });

            await _audit.RecordAsync(platformUserId, "Customer", customer.Id.ToString(), "Create", $"Created customer '{customer.Name}' (external id '{customer.ExternalCustomerId}') in application {appId}");

            return CreatedAtAction(nameof(GetById), new { appId, id = customer.Id }, ToResponse(customer));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A customer with external id '{request.ExternalCustomerId}' already exists in this application." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<CustomerResponse>> GetById(int appId, int id)
    {
        var customer = await _customers.GetByIdAsync(id);
        if (customer is null || customer.ApplicationId != appId) return NotFound();
        return ToResponse(customer);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CustomerResponse>>> GetAll(int appId, [FromQuery] bool includeInactive = false)
    {
        var customers = await _customers.GetByApplicationIdAsync(appId, includeInactive);
        return customers.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<CustomerResponse>> Rename(int appId, int id, RenameCustomerRequest request)
    {
        var existing = await _customers.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        var customer = await _customers.RenameAsync(id, request.Name);

        await _audit.RecordAsync(platformUserId, "Customer", id.ToString(), "Update", $"Renamed customer {id} to '{request.Name}' in application {appId}");

        return ToResponse(customer);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _customers.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        await _customers.DeactivateAsync(id);

        await _audit.RecordAsync(platformUserId, "Customer", id.ToString(), "Deactivate", $"Deactivated customer {id} in application {appId}");

        return NoContent();
    }

    private static CustomerResponse ToResponse(Customer customer) =>
        new(customer.Id, customer.ApplicationId, customer.ExternalCustomerId, customer.Name, customer.IsActive);
}
```

- [ ] **Step 2: Update AppUsersController**

Replace the full contents of `src/LogsPlatform.Web/Controllers/AppUsersController.cs`:

```csharp
using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/users")]
public class AppUsersController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppUserRepository _users;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public AppUsersController(
        IApplicationRepository applications,
        IAppUserRepository users,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _applications = applications;
        _users = users;
        _audit = audit;
        _access = access;
    }

    [HttpPost]
    public async Task<ActionResult<AppUserResponse>> Create(int appId, CreateAppUserRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        try
        {
            var user = await _users.AddAsync(new AppUser
            {
                ApplicationId = appId,
                ExternalUserId = request.ExternalUserId,
                DisplayName = request.DisplayName
            });

            await _audit.RecordAsync(platformUserId, "AppUser", user.Id.ToString(), "Create", $"Created user '{user.DisplayName}' (external id '{user.ExternalUserId}') in application {appId}");

            return CreatedAtAction(nameof(GetById), new { appId, id = user.Id }, ToResponse(user));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A user with external id '{request.ExternalUserId}' already exists in this application." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<AppUserResponse>> GetById(int appId, int id)
    {
        var user = await _users.GetByIdAsync(id);
        if (user is null || user.ApplicationId != appId) return NotFound();
        return ToResponse(user);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AppUserResponse>>> GetAll(int appId, [FromQuery] bool includeInactive = false)
    {
        var users = await _users.GetByApplicationIdAsync(appId, includeInactive);
        return users.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<AppUserResponse>> Rename(int appId, int id, RenameAppUserRequest request)
    {
        var existing = await _users.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        var user = await _users.RenameAsync(id, request.DisplayName);

        await _audit.RecordAsync(platformUserId, "AppUser", id.ToString(), "Update", $"Renamed user {id} to '{request.DisplayName}' in application {appId}");

        return ToResponse(user);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _users.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        await _users.DeactivateAsync(id);

        await _audit.RecordAsync(platformUserId, "AppUser", id.ToString(), "Deactivate", $"Deactivated user {id} in application {appId}");

        return NoContent();
    }

    private static AppUserResponse ToResponse(AppUser user) =>
        new(user.Id, user.ApplicationId, user.ExternalUserId, user.DisplayName, user.IsActive);
}
```

- [ ] **Step 3: Update LogSourcesController**

Replace the full contents of `src/LogsPlatform.Web/Controllers/LogSourcesController.cs`:

```csharp
using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/log-sources")]
public class LogSourcesController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly ILogSourceRepository _logSources;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public LogSourcesController(
        IApplicationRepository applications,
        ILogSourceRepository logSources,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _applications = applications;
        _logSources = logSources;
        _audit = audit;
        _access = access;
    }

    [HttpPost]
    public async Task<ActionResult<LogSourceResponse>> Create(int appId, CreateLogSourceRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        try
        {
            var logSource = await _logSources.AddAsync(new LogSource
            {
                ApplicationId = appId,
                Name = request.Name,
                Description = request.Description
            });

            await _audit.RecordAsync(platformUserId, "LogSource", logSource.Id.ToString(), "Create", $"Created log source '{logSource.Name}' in application {appId}");

            return CreatedAtAction(nameof(GetById), new { appId, id = logSource.Id }, ToResponse(logSource));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A log source named '{request.Name}' already exists in this application." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<LogSourceResponse>> GetById(int appId, int id)
    {
        var logSource = await _logSources.GetByIdAsync(id);
        if (logSource is null || logSource.ApplicationId != appId) return NotFound();
        return ToResponse(logSource);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LogSourceResponse>>> GetAll(int appId, [FromQuery] bool includeInactive = false)
    {
        var logSources = await _logSources.GetByApplicationIdAsync(appId, includeInactive);
        return logSources.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<LogSourceResponse>> Rename(int appId, int id, RenameLogSourceRequest request)
    {
        var existing = await _logSources.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        try
        {
            var logSource = await _logSources.RenameAsync(id, request.Name, request.Description);

            await _audit.RecordAsync(platformUserId, "LogSource", id.ToString(), "Update", $"Renamed log source {id} to '{request.Name}' in application {appId}");

            return ToResponse(logSource);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A log source named '{request.Name}' already exists in this application." });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _logSources.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        await _logSources.DeactivateAsync(id);

        await _audit.RecordAsync(platformUserId, "LogSource", id.ToString(), "Deactivate", $"Deactivated log source {id} in application {appId}");

        return NoContent();
    }

    private static LogSourceResponse ToResponse(LogSource logSource) =>
        new(logSource.Id, logSource.ApplicationId, logSource.Name, logSource.Description, logSource.IsActive);
}
```

- [ ] **Step 4: Write the Group B access tests**

```csharp
// tests/LogsPlatform.Tests/Web/ApplicationAccessGroupBTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ApplicationAccessGroupBTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ApplicationAccessGroupBTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CustomerCreate_SuperAdmin_Succeeds()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupBSuperAdminApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var response = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/customers", new CreateCustomerRequest("ext-1", "Customer One"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CustomerCreate_NonAdminWithGrant_Succeeds()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupBGrantedApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var (nonAdminClient, platformUserId) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "AccessGroupBGrantedUser");
        using (var scope = _factory.Services.CreateScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<IApplicationAccessGrantRepository>();
            await grants.GrantAsync(platformUserId, app!.Id);
        }

        var response = await nonAdminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/customers", new CreateCustomerRequest("ext-1", "Customer One"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CustomerCreate_NonAdminWithoutGrant_ReturnsForbidden()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupBNoGrantApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var (nonAdminClient, _) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "AccessGroupBNoGrantUser");

        var response = await nonAdminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/customers", new CreateCustomerRequest("ext-1", "Customer One"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
```

- [ ] **Step 5: Run the new tests**

Run: `dotnet test --filter "FullyQualifiedName~ApplicationAccessGroupBTests"`
Expected: 3/3 passing.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/CustomersController.cs \
        src/LogsPlatform.Web/Controllers/AppUsersController.cs \
        src/LogsPlatform.Web/Controllers/LogSourcesController.cs \
        tests/LogsPlatform.Tests/Web/ApplicationAccessGroupBTests.cs
git commit -m "feat: enforce per-application access grants on Customer/AppUser/LogSource controllers"
```

---

## Task 4: Group C controllers — DeploymentsController, VersionsController, ModulesController

**Files:**
- Modify: `src/LogsPlatform.Web/Controllers/DeploymentsController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/VersionsController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/ModulesController.cs`
- Create: `tests/LogsPlatform.Tests/Web/ApplicationAccessGroupCTests.cs`

**Interfaces:**
- Consumes: `ApplicationAccessService.CanManageApplicationAsync(bool isSuperAdmin, int platformUserId, int applicationId) : Task<bool>` (Task 1).
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Update DeploymentsController**

Replace the full contents of `src/LogsPlatform.Web/Controllers/DeploymentsController.cs`:

```csharp
using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/deployments")]
public class DeploymentsController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppEnvironmentRepository _environments;
    private readonly IAppVersionRepository _versions;
    private readonly IDeploymentRepository _deployments;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public DeploymentsController(
        IApplicationRepository applications,
        IAppEnvironmentRepository environments,
        IAppVersionRepository versions,
        IDeploymentRepository deployments,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _applications = applications;
        _environments = environments;
        _versions = versions;
        _deployments = deployments;
        _audit = audit;
        _access = access;
    }

    [HttpPost]
    public async Task<ActionResult<DeploymentResponse>> Create(int appId, CreateDeploymentRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        var environment = await _environments.GetByIdAsync(request.EnvironmentId);
        if (environment is null || environment.ApplicationId != appId)
        {
            return NotFound(new { message = $"Environment {request.EnvironmentId} not found in application {appId}." });
        }

        var version = await _versions.GetByIdAsync(request.VersionId);
        if (version is null || version.ApplicationId != appId)
        {
            return NotFound(new { message = $"Version {request.VersionId} not found in application {appId}." });
        }

        var deployment = await _deployments.AddAsync(new Deployment
        {
            ApplicationId = appId,
            EnvironmentId = request.EnvironmentId,
            VersionId = request.VersionId,
            DeployedAt = request.DeployedAt,
            Notes = request.Notes
        });

        await _audit.RecordAsync(platformUserId, "Deployment", deployment.Id.ToString(), "Create", $"Created deployment {deployment.Id} (environment {request.EnvironmentId}, version {request.VersionId}) in application {appId}");

        return CreatedAtAction(nameof(GetById), new { appId, id = deployment.Id }, ToResponse(deployment));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<DeploymentResponse>> GetById(int appId, int id)
    {
        var deployment = await _deployments.GetByIdAsync(id);
        if (deployment is null || deployment.ApplicationId != appId) return NotFound();
        return ToResponse(deployment);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DeploymentResponse>>> GetAll(int appId, [FromQuery] bool includeInactive = false)
    {
        var deployments = await _deployments.GetByApplicationIdAsync(appId, includeInactive);
        return deployments.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<DeploymentResponse>> Rename(int appId, int id, RenameDeploymentRequest request)
    {
        var existing = await _deployments.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        var deployment = await _deployments.RenameAsync(id, request.Notes);

        await _audit.RecordAsync(platformUserId, "Deployment", id.ToString(), "Update", $"Updated deployment {id} notes in application {appId}");

        return ToResponse(deployment);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _deployments.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        await _deployments.DeactivateAsync(id);

        await _audit.RecordAsync(platformUserId, "Deployment", id.ToString(), "Deactivate", $"Deactivated deployment {id} in application {appId}");

        return NoContent();
    }

    private static DeploymentResponse ToResponse(Deployment deployment) =>
        new(deployment.Id, deployment.ApplicationId, deployment.EnvironmentId, deployment.VersionId, deployment.DeployedAt, deployment.Notes, deployment.IsActive);
}
```

- [ ] **Step 2: Update VersionsController**

Replace the full contents of `src/LogsPlatform.Web/Controllers/VersionsController.cs`:

```csharp
using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/versions")]
public class VersionsController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppVersionRepository _versions;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public VersionsController(
        IApplicationRepository applications,
        IAppVersionRepository versions,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _applications = applications;
        _versions = versions;
        _audit = audit;
        _access = access;
    }

    [HttpPost]
    public async Task<ActionResult<VersionResponse>> Create(int appId, CreateVersionRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        try
        {
            var version = await _versions.AddAsync(new AppVersion
            {
                ApplicationId = appId,
                VersionNumber = request.VersionNumber,
                ReleaseNotes = request.ReleaseNotes,
                CreatedAt = DateTime.UtcNow
            });

            await _audit.RecordAsync(platformUserId, "AppVersion", version.Id.ToString(), "Create", $"Created version '{version.VersionNumber}' in application {appId}");

            return CreatedAtAction(nameof(GetById), new { appId, id = version.Id }, ToResponse(version));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A version '{request.VersionNumber}' already exists in this application." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<VersionResponse>> GetById(int appId, int id)
    {
        var version = await _versions.GetByIdAsync(id);
        if (version is null || version.ApplicationId != appId) return NotFound();
        return ToResponse(version);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<VersionResponse>>> GetAll(int appId, [FromQuery] bool includeInactive = false)
    {
        var versions = await _versions.GetByApplicationIdAsync(appId, includeInactive);
        return versions.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<VersionResponse>> Rename(int appId, int id, RenameVersionRequest request)
    {
        var existing = await _versions.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        var version = await _versions.RenameAsync(id, request.ReleaseNotes);

        await _audit.RecordAsync(platformUserId, "AppVersion", id.ToString(), "Update", $"Updated version {id} release notes in application {appId}");

        return ToResponse(version);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _versions.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        await _versions.DeactivateAsync(id);

        await _audit.RecordAsync(platformUserId, "AppVersion", id.ToString(), "Deactivate", $"Deactivated version {id} in application {appId}");

        return NoContent();
    }

    private static VersionResponse ToResponse(AppVersion version) =>
        new(version.Id, version.ApplicationId, version.VersionNumber, version.ReleaseNotes, version.CreatedAt, version.IsActive);
}
```

- [ ] **Step 3: Update ModulesController**

Replace the full contents of `src/LogsPlatform.Web/Controllers/ModulesController.cs`:

```csharp
using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/modules")]
public class ModulesController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppModuleRepository _modules;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public ModulesController(
        IApplicationRepository applications,
        IAppModuleRepository modules,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _applications = applications;
        _modules = modules;
        _audit = audit;
        _access = access;
    }

    [HttpPost]
    public async Task<ActionResult<ModuleResponse>> Create(int appId, CreateModuleRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        try
        {
            var module = await _modules.AddAsync(new AppModule
            {
                ApplicationId = appId,
                Name = request.Name,
                Description = request.Description
            });

            await _audit.RecordAsync(platformUserId, "AppModule", module.Id.ToString(), "Create", $"Created module '{module.Name}' in application {appId}");

            return CreatedAtAction(nameof(GetById), new { appId, id = module.Id }, ToResponse(module));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A module named '{request.Name}' already exists in this application." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ModuleResponse>> GetById(int appId, int id)
    {
        var module = await _modules.GetByIdAsync(id);
        if (module is null || module.ApplicationId != appId) return NotFound();
        return ToResponse(module);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ModuleResponse>>> GetAll(int appId, [FromQuery] bool includeInactive = false)
    {
        var modules = await _modules.GetByApplicationIdAsync(appId, includeInactive);
        return modules.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ModuleResponse>> Rename(int appId, int id, RenameModuleRequest request)
    {
        var existing = await _modules.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        try
        {
            var module = await _modules.RenameAsync(id, request.Name, request.Description);

            await _audit.RecordAsync(platformUserId, "AppModule", id.ToString(), "Update", $"Renamed module {id} to '{request.Name}' in application {appId}");

            return ToResponse(module);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A module named '{request.Name}' already exists in this application." });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _modules.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        await _modules.DeactivateAsync(id);

        await _audit.RecordAsync(platformUserId, "AppModule", id.ToString(), "Deactivate", $"Deactivated module {id} in application {appId}");

        return NoContent();
    }

    private static ModuleResponse ToResponse(AppModule module) =>
        new(module.Id, module.ApplicationId, module.Name, module.Description, module.IsActive);
}
```

- [ ] **Step 4: Write the Group C access tests**

```csharp
// tests/LogsPlatform.Tests/Web/ApplicationAccessGroupCTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ApplicationAccessGroupCTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ApplicationAccessGroupCTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task VersionCreate_SuperAdmin_Succeeds()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupCSuperAdminApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var response = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/versions", new CreateVersionRequest("1.0.0", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task VersionCreate_NonAdminWithGrant_Succeeds()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupCGrantedApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var (nonAdminClient, platformUserId) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "AccessGroupCGrantedUser");
        using (var scope = _factory.Services.CreateScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<IApplicationAccessGrantRepository>();
            await grants.GrantAsync(platformUserId, app!.Id);
        }

        var response = await nonAdminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/versions", new CreateVersionRequest("1.0.0", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task VersionCreate_NonAdminWithoutGrant_ReturnsForbidden()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupCNoGrantApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var (nonAdminClient, _) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "AccessGroupCNoGrantUser");

        var response = await nonAdminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/versions", new CreateVersionRequest("1.0.0", null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
```

- [ ] **Step 5: Run the new tests**

Run: `dotnet test --filter "FullyQualifiedName~ApplicationAccessGroupCTests"`
Expected: 3/3 passing.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/DeploymentsController.cs \
        src/LogsPlatform.Web/Controllers/VersionsController.cs \
        src/LogsPlatform.Web/Controllers/ModulesController.cs \
        tests/LogsPlatform.Tests/Web/ApplicationAccessGroupCTests.cs
git commit -m "feat: enforce per-application access grants on Deployment/AppVersion/AppModule controllers"
```

---

## Task 5: Group D controllers — ScreenServicesController, ProcessesController, OperationsController

These three do not carry `appId` directly in their route (they route on `moduleId`, `screenServiceId`, `processId` respectively), so each resolves `applicationId` by walking an existing FK chain — see Global Constraints for the exact chain per controller.

**Files:**
- Modify: `src/LogsPlatform.Web/Controllers/ScreenServicesController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/ProcessesController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/OperationsController.cs`
- Create: `tests/LogsPlatform.Tests/Web/ApplicationAccessGroupDTests.cs`

**Interfaces:**
- Consumes: `ApplicationAccessService.CanManageApplicationAsync(bool isSuperAdmin, int platformUserId, int applicationId) : Task<bool>` (Task 1). `IAppModuleRepository.GetByIdAsync(int id) : Task<AppModule?>` (existing, `AppModule.ApplicationId` is the field being walked to). `IScreenServiceRepository.GetByIdAsync(int id) : Task<ScreenService?>` (existing, `ScreenService.ModuleId` is the field being walked to).
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Update ScreenServicesController**

`AppModule.ApplicationId` is already available from the existing "module exists" check — no new dependency needed.

Replace the full contents of `src/LogsPlatform.Web/Controllers/ScreenServicesController.cs`:

```csharp
using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/modules/{moduleId:int}/screen-services")]
public class ScreenServicesController : ControllerBase
{
    private readonly IAppModuleRepository _modules;
    private readonly IScreenServiceRepository _screenServices;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public ScreenServicesController(
        IAppModuleRepository modules,
        IScreenServiceRepository screenServices,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _modules = modules;
        _screenServices = screenServices;
        _audit = audit;
        _access = access;
    }

    [HttpPost]
    public async Task<ActionResult<ScreenServiceResponse>> Create(int moduleId, CreateScreenServiceRequest request)
    {
        var module = await _modules.GetByIdAsync(moduleId);
        if (module is null)
        {
            return NotFound(new { message = $"Module {moduleId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, module.ApplicationId))
        {
            return Forbid();
        }

        if (!Enum.TryParse<ScreenServiceType>(request.Type, ignoreCase: true, out var type))
        {
            return BadRequest(new { message = $"Type must be 'Screen' or 'Service', got '{request.Type}'." });
        }

        try
        {
            var screenService = await _screenServices.AddAsync(new ScreenService
            {
                ModuleId = moduleId,
                Name = request.Name,
                Type = type,
                Description = request.Description
            });

            await _audit.RecordAsync(platformUserId, "ScreenService", screenService.Id.ToString(), "Create", $"Created screen/service '{screenService.Name}' in module {moduleId}");

            return CreatedAtAction(nameof(GetById), new { moduleId, id = screenService.Id }, ToResponse(screenService));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A screen/service named '{request.Name}' already exists in this module." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ScreenServiceResponse>> GetById(int moduleId, int id)
    {
        var screenService = await _screenServices.GetByIdAsync(id);
        if (screenService is null || screenService.ModuleId != moduleId) return NotFound();
        return ToResponse(screenService);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ScreenServiceResponse>>> GetAll(int moduleId, [FromQuery] bool includeInactive = false)
    {
        var screenServices = await _screenServices.GetByModuleIdAsync(moduleId, includeInactive);
        return screenServices.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ScreenServiceResponse>> Rename(int moduleId, int id, RenameScreenServiceRequest request)
    {
        var existing = await _screenServices.GetByIdAsync(id);
        if (existing is null || existing.ModuleId != moduleId) return NotFound();

        var module = await _modules.GetByIdAsync(moduleId);
        if (module is null) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, module.ApplicationId))
        {
            return Forbid();
        }

        try
        {
            var screenService = await _screenServices.RenameAsync(id, request.Name, request.Description);

            await _audit.RecordAsync(platformUserId, "ScreenService", id.ToString(), "Update", $"Renamed screen/service {id} to '{request.Name}' in module {moduleId}");

            return ToResponse(screenService);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A screen/service named '{request.Name}' already exists in this module." });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int moduleId, int id)
    {
        var existing = await _screenServices.GetByIdAsync(id);
        if (existing is null || existing.ModuleId != moduleId) return NotFound();

        var module = await _modules.GetByIdAsync(moduleId);
        if (module is null) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, module.ApplicationId))
        {
            return Forbid();
        }

        await _screenServices.DeactivateAsync(id);

        await _audit.RecordAsync(platformUserId, "ScreenService", id.ToString(), "Deactivate", $"Deactivated screen/service {id} in module {moduleId}");

        return NoContent();
    }

    private static ScreenServiceResponse ToResponse(ScreenService screenService) =>
        new(screenService.Id, screenService.ModuleId, screenService.Name, screenService.Type.ToString(), screenService.Description, screenService.IsActive);
}
```

- [ ] **Step 2: Update ProcessesController**

Needs a new `IAppModuleRepository` dependency to walk `ScreenService.ModuleId` → `AppModule.ApplicationId`.

Replace the full contents of `src/LogsPlatform.Web/Controllers/ProcessesController.cs`:

```csharp
using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/screen-services/{screenServiceId:int}/processes")]
public class ProcessesController : ControllerBase
{
    private readonly IScreenServiceRepository _screenServices;
    private readonly IAppModuleRepository _modules;
    private readonly IProcessNodeRepository _processes;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public ProcessesController(
        IScreenServiceRepository screenServices,
        IAppModuleRepository modules,
        IProcessNodeRepository processes,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _screenServices = screenServices;
        _modules = modules;
        _processes = processes;
        _audit = audit;
        _access = access;
    }

    private async Task<int?> ResolveApplicationIdAsync(int screenServiceId)
    {
        var screenService = await _screenServices.GetByIdAsync(screenServiceId);
        if (screenService is null) return null;

        var module = await _modules.GetByIdAsync(screenService.ModuleId);
        return module?.ApplicationId;
    }

    [HttpPost]
    public async Task<ActionResult<ProcessResponse>> Create(int screenServiceId, CreateProcessRequest request)
    {
        if (await _screenServices.GetByIdAsync(screenServiceId) is null)
        {
            return NotFound(new { message = $"ScreenService {screenServiceId} not found." });
        }

        var applicationId = await ResolveApplicationIdAsync(screenServiceId);
        if (applicationId is null)
        {
            return NotFound(new { message = $"ScreenService {screenServiceId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, applicationId.Value))
        {
            return Forbid();
        }

        try
        {
            var process = await _processes.AddAsync(new ProcessNode
            {
                ScreenServiceId = screenServiceId,
                Name = request.Name,
                Description = request.Description
            });

            await _audit.RecordAsync(platformUserId, "ProcessNode", process.Id.ToString(), "Create", $"Created process '{process.Name}' in screen/service {screenServiceId}");

            return CreatedAtAction(nameof(GetById), new { screenServiceId, id = process.Id }, ToResponse(process));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A process named '{request.Name}' already exists in this screen/service." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProcessResponse>> GetById(int screenServiceId, int id)
    {
        var process = await _processes.GetByIdAsync(id);
        if (process is null || process.ScreenServiceId != screenServiceId) return NotFound();
        return ToResponse(process);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProcessResponse>>> GetAll(int screenServiceId, [FromQuery] bool includeInactive = false)
    {
        var processes = await _processes.GetByScreenServiceIdAsync(screenServiceId, includeInactive);
        return processes.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ProcessResponse>> Rename(int screenServiceId, int id, RenameProcessRequest request)
    {
        var existing = await _processes.GetByIdAsync(id);
        if (existing is null || existing.ScreenServiceId != screenServiceId) return NotFound();

        var applicationId = await ResolveApplicationIdAsync(screenServiceId);
        if (applicationId is null) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, applicationId.Value))
        {
            return Forbid();
        }

        try
        {
            var process = await _processes.RenameAsync(id, request.Name, request.Description);

            await _audit.RecordAsync(platformUserId, "ProcessNode", id.ToString(), "Update", $"Renamed process {id} to '{request.Name}' in screen/service {screenServiceId}");

            return ToResponse(process);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A process named '{request.Name}' already exists in this screen/service." });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int screenServiceId, int id)
    {
        var existing = await _processes.GetByIdAsync(id);
        if (existing is null || existing.ScreenServiceId != screenServiceId) return NotFound();

        var applicationId = await ResolveApplicationIdAsync(screenServiceId);
        if (applicationId is null) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, applicationId.Value))
        {
            return Forbid();
        }

        await _processes.DeactivateAsync(id);

        await _audit.RecordAsync(platformUserId, "ProcessNode", id.ToString(), "Deactivate", $"Deactivated process {id} in screen/service {screenServiceId}");

        return NoContent();
    }

    private static ProcessResponse ToResponse(ProcessNode process) =>
        new(process.Id, process.ScreenServiceId, process.Name, process.Description, process.IsActive);
}
```

- [ ] **Step 3: Update OperationsController**

Needs new `IScreenServiceRepository` and `IAppModuleRepository` dependencies to walk `ProcessNode.ScreenServiceId` → `ScreenService.ModuleId` → `AppModule.ApplicationId`.

Replace the full contents of `src/LogsPlatform.Web/Controllers/OperationsController.cs`:

```csharp
using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/processes/{processId:int}/operations")]
public class OperationsController : ControllerBase
{
    private readonly IProcessNodeRepository _processes;
    private readonly IScreenServiceRepository _screenServices;
    private readonly IAppModuleRepository _modules;
    private readonly IOperationRepository _operations;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public OperationsController(
        IProcessNodeRepository processes,
        IScreenServiceRepository screenServices,
        IAppModuleRepository modules,
        IOperationRepository operations,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _processes = processes;
        _screenServices = screenServices;
        _modules = modules;
        _operations = operations;
        _audit = audit;
        _access = access;
    }

    private async Task<int?> ResolveApplicationIdAsync(int processId)
    {
        var process = await _processes.GetByIdAsync(processId);
        if (process is null) return null;

        var screenService = await _screenServices.GetByIdAsync(process.ScreenServiceId);
        if (screenService is null) return null;

        var module = await _modules.GetByIdAsync(screenService.ModuleId);
        return module?.ApplicationId;
    }

    [HttpPost]
    public async Task<ActionResult<OperationResponse>> Create(int processId, CreateOperationRequest request)
    {
        if (await _processes.GetByIdAsync(processId) is null)
        {
            return NotFound(new { message = $"ProcessNode {processId} not found." });
        }

        var applicationId = await ResolveApplicationIdAsync(processId);
        if (applicationId is null)
        {
            return NotFound(new { message = $"ProcessNode {processId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, applicationId.Value))
        {
            return Forbid();
        }

        try
        {
            var operation = await _operations.AddAsync(new Operation
            {
                ProcessId = processId,
                Name = request.Name,
                Description = request.Description
            });

            await _audit.RecordAsync(platformUserId, "Operation", operation.Id.ToString(), "Create", $"Created operation '{operation.Name}' in process {processId}");

            return CreatedAtAction(nameof(GetById), new { processId, id = operation.Id }, ToResponse(operation));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"An operation named '{request.Name}' already exists in this process." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OperationResponse>> GetById(int processId, int id)
    {
        var operation = await _operations.GetByIdAsync(id);
        if (operation is null || operation.ProcessId != processId) return NotFound();
        return ToResponse(operation);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<OperationResponse>>> GetAll(int processId, [FromQuery] bool includeInactive = false)
    {
        var operations = await _operations.GetByProcessIdAsync(processId, includeInactive);
        return operations.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<OperationResponse>> Rename(int processId, int id, RenameOperationRequest request)
    {
        var existing = await _operations.GetByIdAsync(id);
        if (existing is null || existing.ProcessId != processId) return NotFound();

        var applicationId = await ResolveApplicationIdAsync(processId);
        if (applicationId is null) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, applicationId.Value))
        {
            return Forbid();
        }

        try
        {
            var operation = await _operations.RenameAsync(id, request.Name, request.Description);

            await _audit.RecordAsync(platformUserId, "Operation", id.ToString(), "Update", $"Renamed operation {id} to '{request.Name}' in process {processId}");

            return ToResponse(operation);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"An operation named '{request.Name}' already exists in this process." });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int processId, int id)
    {
        var existing = await _operations.GetByIdAsync(id);
        if (existing is null || existing.ProcessId != processId) return NotFound();

        var applicationId = await ResolveApplicationIdAsync(processId);
        if (applicationId is null) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, applicationId.Value))
        {
            return Forbid();
        }

        await _operations.DeactivateAsync(id);

        await _audit.RecordAsync(platformUserId, "Operation", id.ToString(), "Deactivate", $"Deactivated operation {id} in process {processId}");

        return NoContent();
    }

    private static OperationResponse ToResponse(Operation operation) =>
        new(operation.Id, operation.ProcessId, operation.Name, operation.Description, operation.IsActive);
}
```

- [ ] **Step 4: Write the Group D access tests**

```csharp
// tests/LogsPlatform.Tests/Web/ApplicationAccessGroupDTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ApplicationAccessGroupDTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ApplicationAccessGroupDTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ScreenServiceCreate_SuperAdmin_Succeeds()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupDSuperAdminApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var moduleResponse = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/modules", new CreateModuleRequest("Module1", null));
        var module = await moduleResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var response = await adminClient.PostAsJsonAsync($"/api/v1/admin/modules/{module!.Id}/screen-services", new CreateScreenServiceRequest("Screen1", "Screen", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ScreenServiceCreate_NonAdminWithGrant_Succeeds()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupDGrantedApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var moduleResponse = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/modules", new CreateModuleRequest("Module1", null));
        var module = await moduleResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var (nonAdminClient, platformUserId) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "AccessGroupDGrantedUser");
        using (var scope = _factory.Services.CreateScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<IApplicationAccessGrantRepository>();
            await grants.GrantAsync(platformUserId, app!.Id);
        }

        var response = await nonAdminClient.PostAsJsonAsync($"/api/v1/admin/modules/{module!.Id}/screen-services", new CreateScreenServiceRequest("Screen1", "Screen", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ScreenServiceCreate_NonAdminWithoutGrant_ReturnsForbidden()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AccessGroupDNoGrantApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var moduleResponse = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/modules", new CreateModuleRequest("Module1", null));
        var module = await moduleResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var (nonAdminClient, _) = await AuthenticatedTestClientHelper.CreateNonAdminAuthenticatedClientAsync(_factory, "AccessGroupDNoGrantUser");

        var response = await nonAdminClient.PostAsJsonAsync($"/api/v1/admin/modules/{module!.Id}/screen-services", new CreateScreenServiceRequest("Screen1", "Screen", null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
```

- [ ] **Step 5: Run the new tests**

Run: `dotnet test --filter "FullyQualifiedName~ApplicationAccessGroupDTests"`
Expected: 3/3 passing.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: all tests green — this closes out all 11 controllers.

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/ScreenServicesController.cs \
        src/LogsPlatform.Web/Controllers/ProcessesController.cs \
        src/LogsPlatform.Web/Controllers/OperationsController.cs \
        tests/LogsPlatform.Tests/Web/ApplicationAccessGroupDTests.cs
git commit -m "feat: enforce per-application access grants on ScreenService/ProcessNode/Operation controllers"
```

---

## Task 6: ApplicationsAdmin.razor + its 6 nested Shared components

`ApplicationsAdmin.razor` hosts the Super-Admin-only "create Application" form AND, per expanded row, the environment-creation form plus all 6 nested components (`CustomersSection`, `UsersSection`, `LogSourcesSection`, `ApiKeysSection`, `VersionsSection`, `DeploymentsSection`). Relaxing the page's `@attribute` makes it reachable by any authenticated user; the create-Application form must then be hidden from non-Super-Admins (Applications creation stays Super-Admin-only per Global Constraints), while the environment form and all 6 nested components stay visible for everyone and enforce the real check at mutation time (no per-row hiding, to avoid N+1 grant lookups just for rendering — see design doc §3.2/§3.3 and the resolved decision below).

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor`
- Modify: `src/LogsPlatform.Web/Components/Shared/ApiKeysSection.razor`
- Modify: `src/LogsPlatform.Web/Components/Shared/CustomersSection.razor`
- Modify: `src/LogsPlatform.Web/Components/Shared/DeploymentsSection.razor`
- Modify: `src/LogsPlatform.Web/Components/Shared/LogSourcesSection.razor`
- Modify: `src/LogsPlatform.Web/Components/Shared/UsersSection.razor`
- Modify: `src/LogsPlatform.Web/Components/Shared/VersionsSection.razor`

**Interfaces:**
- Consumes: `ApplicationAccessService.CanManageApplicationAsync(bool isSuperAdmin, int platformUserId, int applicationId) : Task<bool>` (Task 1).
- Produces: nothing consumed by later tasks (Task 7's pages are independent files).

- [ ] **Step 1: Update ApplicationsAdmin.razor**

Replace the full contents of `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor`:

```razor
@* src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor *@
@page "/admin/applications"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web.Components.Shared
@using LogsPlatform.Web.Services
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.EntityFrameworkCore
@using Microsoft.Data.SqlClient
@using System.Security.Claims
@inject IApplicationRepository ApplicationRepository
@inject IAppEnvironmentRepository EnvironmentRepository
@inject AuditLogger Audit
@inject ApplicationAccessService Access
@rendermode InteractiveServer

<h1>אפליקציות</h1>

@if (_isSuperAdmin)
{
    <div class="card mb-4">
        <div class="card-header">הוספת אפליקציה</div>
        <div class="card-body">
            <EditForm Model="_newApplication" OnValidSubmit="CreateApplicationAsync">
                <div class="row g-3 align-items-end">
                    <div class="col-auto">
                        <label class="form-label">שם</label>
                        <InputText @bind-Value="_newApplication.Name" required class="form-control" aria-label="שם" />
                    </div>
                    <div class="col-auto">
                        <label class="form-label">תיאור</label>
                        <InputText @bind-Value="_newApplication.Description" class="form-control" aria-label="תיאור" />
                    </div>
                    <div class="col-auto">
                        <button type="submit" class="btn btn-primary">צור</button>
                    </div>
                </div>
            </EditForm>
            @if (_createError is not null)
            {
                <div class="alert alert-danger mt-3 mb-0">@_createError</div>
            }
        </div>
    </div>
}

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
    <tbody>
        @foreach (var application in _applications)
        {
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
            @if (_expandedAppIds.Contains(application.Id))
            {
                <tr>
                    <td colspan="5">
                        <div class="app-row-details">
                            <div>
                                <h4>סביבות</h4>
                                <table class="table table-sm table-striped align-middle">
                                    <thead>
                                        <tr>
                                            <th>שם</th>
                                            <th>סביבת ייצור</th>
                                        </tr>
                                    </thead>
                                    <tbody>
                                        @if (_environmentsByAppId.TryGetValue(application.Id, out var environments))
                                        {
                                            @foreach (var environment in environments)
                                            {
                                                <tr>
                                                    <td>@environment.Name</td>
                                                    <td>@environment.IsProduction</td>
                                                </tr>
                                            }
                                        }
                                    </tbody>
                                </table>

                                @if (_newEnvironmentModels.TryGetValue(application.Id, out var newEnvironment))
                                {
                                    <EditForm Model="newEnvironment" OnValidSubmit="() => CreateEnvironmentAsync(application.Id)">
                                        <div class="row g-3 align-items-end">
                                            <div class="col-auto">
                                                <label class="form-label">שם</label>
                                                <InputText @bind-Value="newEnvironment.Name" required class="form-control" aria-label="שם" />
                                            </div>
                                            <div class="col-auto form-check mb-2">
                                                <InputCheckbox @bind-Value="newEnvironment.IsProduction" class="form-check-input" id="@($"isProduction-{application.Id}")" />
                                                <label class="form-check-label" for="@($"isProduction-{application.Id}")">סביבת ייצור</label>
                                            </div>
                                            <div class="col-auto">
                                                <button type="submit" class="btn btn-primary">הוסף סביבה</button>
                                            </div>
                                        </div>
                                    </EditForm>
                                    @if (_environmentErrors.TryGetValue(application.Id, out var environmentError) && environmentError is not null)
                                    {
                                        <div class="alert alert-danger mt-2 mb-0 py-1">@environmentError</div>
                                    }
                                }
                            </div>

                            <CustomersSection ApplicationId="application.Id" />
                            <UsersSection ApplicationId="application.Id" />
                            <LogSourcesSection ApplicationId="application.Id" />
                            <ApiKeysSection ApplicationId="application.Id" />
                            <VersionsSection ApplicationId="application.Id" />
                            <DeploymentsSection ApplicationId="application.Id" />
                        </div>
                    </td>
                </tr>
            }
        }
    </tbody>
</table>

@code {
    private List<Application> _applications = new();
    private readonly NewApplicationModel _newApplication = new();
    private string? _createError;

    private readonly HashSet<int> _expandedAppIds = new();
    private readonly Dictionary<int, List<AppEnvironment>> _environmentsByAppId = new();
    private readonly Dictionary<int, NewEnvironmentModel> _newEnvironmentModels = new();
    private readonly Dictionary<int, string?> _environmentErrors = new();

    private bool _isSuperAdmin;
    private int _currentPlatformUserId;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthenticationStateTask!;
        _isSuperAdmin = authState.User.FindFirstValue("IsAdmin") == "true";
        _currentPlatformUserId = int.Parse(authState.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        _applications = (await ApplicationRepository.GetAllAsync()).ToList();
    }

    private async Task CreateApplicationAsync()
    {
        _createError = null;
        try
        {
            var application = await ApplicationRepository.AddAsync(new Application
            {
                Name = _newApplication.Name,
                Description = _newApplication.Description,
                CreatedAt = DateTime.UtcNow
            });

            await Audit.RecordAsync(_currentPlatformUserId, "Application", application.Id.ToString(), "Create", $"Created application '{application.Name}'");

            _newApplication.Name = string.Empty;
            _newApplication.Description = null;
            _applications = (await ApplicationRepository.GetAllAsync()).ToList();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            _createError = $"אפליקציה בשם '{_newApplication.Name}' כבר קיימת.";
        }
    }

    private async Task ToggleExpandAsync(int applicationId)
    {
        if (_expandedAppIds.Contains(applicationId))
        {
            _expandedAppIds.Remove(applicationId);
            return;
        }

        _expandedAppIds.Add(applicationId);

        if (!_environmentsByAppId.ContainsKey(applicationId))
        {
            _environmentsByAppId[applicationId] =
                (await EnvironmentRepository.GetByApplicationIdAsync(applicationId)).ToList();
        }

        if (!_newEnvironmentModels.ContainsKey(applicationId))
        {
            _newEnvironmentModels[applicationId] = new NewEnvironmentModel();
        }
    }

    private async Task CreateEnvironmentAsync(int applicationId)
    {
        _environmentErrors[applicationId] = null;

        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, applicationId))
        {
            _environmentErrors[applicationId] = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        var model = _newEnvironmentModels[applicationId];

        var environment = await EnvironmentRepository.AddAsync(new AppEnvironment
        {
            ApplicationId = applicationId,
            Name = model.Name,
            IsProduction = model.IsProduction
        });

        await Audit.RecordAsync(_currentPlatformUserId, "AppEnvironment", environment.Id.ToString(), "Create", $"Created environment '{environment.Name}' in application {applicationId}");

        _newEnvironmentModels[applicationId] = new NewEnvironmentModel();
        _environmentsByAppId[applicationId] =
            (await EnvironmentRepository.GetByApplicationIdAsync(applicationId)).ToList();
    }

    private class NewApplicationModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    private class NewEnvironmentModel
    {
        public string Name { get; set; } = string.Empty;
        public bool IsProduction { get; set; }
    }
}
```

- [ ] **Step 2: Update ApiKeysSection.razor**

Replace the full contents of `src/LogsPlatform.Web/Components/Shared/ApiKeysSection.razor`:

```razor
@* src/LogsPlatform.Web/Components/Shared/ApiKeysSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web.Services
@using Microsoft.AspNetCore.Components.Authorization
@using System.Security.Claims
@inject IApiKeyRepository ApiKeyRepository
@inject AuditLogger Audit
@inject ApplicationAccessService Access

<h4>מפתחות API</h4>
@if (_newRawKey is not null)
{
    <div class="alert alert-warning">זוהי הפעם היחידה שבה תוכל/י לראות את המפתח הזה — העתק/י אותו עכשיו.</div>
    <pre class="bg-light border rounded p-2" dir="ltr">@_newRawKey</pre>
}
<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th>תווית</th>
            <th>נוצר בתאריך</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var apiKey in _apiKeys)
        {
            <tr>
                <td>@apiKey.Label</td>
                <td>@apiKey.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) UTC</td>
                <td>
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => RevokeAsync(apiKey.Id)">בטל תוקף</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newApiKey" OnValidSubmit="CreateApiKeyAsync">
    <div class="row g-3 align-items-end">
        <div class="col-auto">
            <label class="form-label">תווית</label>
            <InputText @bind-Value="_newApiKey.Label" required maxlength="200" class="form-control" aria-label="תווית" />
        </div>
        <div class="col-auto">
            <button type="submit" class="btn btn-primary">הוסף מפתח API</button>
        </div>
    </div>
</EditForm>
@if (_error is not null)
{
    <div class="alert alert-danger mt-3">@_error</div>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<ApiKey> _apiKeys = new();
    private readonly NewApiKeyModel _newApiKey = new();
    private string? _newRawKey;
    private string? _error;
    private int? _lastLoadedApplicationId;

    private bool _isSuperAdmin;
    private int _currentPlatformUserId;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    private async Task LoadAccessAsync()
    {
        var authState = await AuthenticationStateTask!;
        _isSuperAdmin = authState.User.FindFirstValue("IsAdmin") == "true";
        _currentPlatformUserId = int.Parse(authState.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_lastLoadedApplicationId == ApplicationId)
        {
            return;
        }

        await LoadAccessAsync();
        _lastLoadedApplicationId = ApplicationId;
        _newRawKey = null;
        _error = null;
        _apiKeys = (await ApiKeyRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateApiKeyAsync()
    {
        _error = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _error = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        var (apiKey, rawKey) = await ApiKeyRepository.AddAsync(ApplicationId, _newApiKey.Label);

        await Audit.RecordAsync(_currentPlatformUserId, "ApiKey", apiKey.Id.ToString(), "Create", $"Created API key '{apiKey.Label}' in application {ApplicationId}");

        _newRawKey = rawKey;
        _newApiKey.Label = string.Empty;
        _apiKeys = (await ApiKeyRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task RevokeAsync(int apiKeyId)
    {
        _error = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _error = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        await ApiKeyRepository.RevokeAsync(apiKeyId);

        await Audit.RecordAsync(_currentPlatformUserId, "ApiKey", apiKeyId.ToString(), "Revoke", $"Revoked API key {apiKeyId} in application {ApplicationId}");

        _apiKeys = (await ApiKeyRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private class NewApiKeyModel
    {
        public string Label { get; set; } = string.Empty;
    }
}
```

- [ ] **Step 3: Update CustomersSection.razor**

Replace the full contents of `src/LogsPlatform.Web/Components/Shared/CustomersSection.razor`:

```razor
@* src/LogsPlatform.Web/Components/Shared/CustomersSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using LogsPlatform.Web.Services
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.EntityFrameworkCore
@using System.Security.Claims
@inject ICustomerRepository CustomerRepository
@inject AuditLogger Audit
@inject ApplicationAccessService Access

<h4>לקוחות</h4>
<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th>מזהה לקוח חיצוני</th>
            <th>שם</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var customer in _customers)
        {
            <tr>
                @if (_editingId == customer.Id)
                {
                    <td colspan="2">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(customer.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Name" required maxlength="200" class="form-control form-control-sm" aria-label="שם" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                    </td>
                }
                else
                {
                    <td>@customer.ExternalCustomerId</td>
                    <td>@customer.Name</td>
                }
                <td>
                    @if (_editingId != customer.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(customer)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(customer.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newCustomer" OnValidSubmit="CreateCustomerAsync">
    <div class="row g-3 align-items-end">
        <div class="col-auto">
            <label class="form-label">מזהה לקוח חיצוני</label>
            <InputText @bind-Value="_newCustomer.ExternalCustomerId" required maxlength="200" class="form-control" aria-label="מזהה לקוח חיצוני" />
        </div>
        <div class="col-auto">
            <label class="form-label">שם</label>
            <InputText @bind-Value="_newCustomer.Name" required maxlength="200" class="form-control" aria-label="שם" />
        </div>
        <div class="col-auto">
            <button type="submit" class="btn btn-primary">הוסף לקוח</button>
        </div>
    </div>
</EditForm>
@if (_createError is not null)
{
    <div class="alert alert-danger mt-3">@_createError</div>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<Customer> _customers = new();
    private readonly NewCustomerModel _newCustomer = new();
    private string? _createError;

    private int? _editingId;
    private EditCustomerModel? _editModel;

    private bool _isSuperAdmin;
    private int _currentPlatformUserId;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthenticationStateTask!;
        _isSuperAdmin = authState.User.FindFirstValue("IsAdmin") == "true";
        _currentPlatformUserId = int.Parse(authState.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        _customers = (await CustomerRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateCustomerAsync()
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        try
        {
            var customer = await CustomerRepository.AddAsync(new Customer
            {
                ApplicationId = ApplicationId,
                ExternalCustomerId = _newCustomer.ExternalCustomerId,
                Name = _newCustomer.Name
            });

            await Audit.RecordAsync(_currentPlatformUserId, "Customer", customer.Id.ToString(), "Create", $"Created customer '{customer.Name}' (external id '{customer.ExternalCustomerId}') in application {ApplicationId}");

            _newCustomer.ExternalCustomerId = string.Empty;
            _newCustomer.Name = string.Empty;
            _customers = (await CustomerRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"לקוח עם מזהה חיצוני '{_newCustomer.ExternalCustomerId}' כבר קיים.";
        }
    }

    private void StartEdit(Customer customer)
    {
        _editingId = customer.Id;
        _editModel = new EditCustomerModel { Name = customer.Name };
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
    }

    private async Task SaveRenameAsync(int customerId)
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        await CustomerRepository.RenameAsync(customerId, _editModel!.Name);

        await Audit.RecordAsync(_currentPlatformUserId, "Customer", customerId.ToString(), "Update", $"Renamed customer {customerId} to '{_editModel!.Name}' in application {ApplicationId}");

        _editingId = null;
        _editModel = null;
        _customers = (await CustomerRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task DeactivateAsync(int customerId)
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        await CustomerRepository.DeactivateAsync(customerId);

        await Audit.RecordAsync(_currentPlatformUserId, "Customer", customerId.ToString(), "Deactivate", $"Deactivated customer {customerId} in application {ApplicationId}");

        _customers = (await CustomerRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private class NewCustomerModel
    {
        public string ExternalCustomerId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    private class EditCustomerModel
    {
        public string Name { get; set; } = string.Empty;
    }
}
```

- [ ] **Step 4: Run the build**

Run: `dotnet build`
Expected: 0 errors — confirms Steps 1–3 compile before continuing to the remaining 4 nested components in Steps 5–8.

- [ ] **Step 5: Update DeploymentsSection.razor**

Replace the full contents of `src/LogsPlatform.Web/Components/Shared/DeploymentsSection.razor`:

```razor
@* src/LogsPlatform.Web/Components/Shared/DeploymentsSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web.Services
@using Microsoft.AspNetCore.Components.Authorization
@using System.Security.Claims
@inject IDeploymentRepository DeploymentRepository
@inject IAppEnvironmentRepository EnvironmentRepository
@inject IAppVersionRepository VersionRepository
@inject AuditLogger Audit
@inject ApplicationAccessService Access

<h4>פריסות</h4>
<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th>סביבה</th>
            <th>גרסה</th>
            <th>תאריך פריסה</th>
            <th>הערות</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var deployment in _deployments)
        {
            <tr>
                @if (_editingId == deployment.Id)
                {
                    <td colspan="4">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(deployment.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Notes" class="form-control form-control-sm" aria-label="הערות" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                    </td>
                }
                else
                {
                    <td>@EnvironmentName(deployment.EnvironmentId)</td>
                    <td>@VersionNumber(deployment.VersionId)</td>
                    <td>@deployment.DeployedAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) UTC</td>
                    <td>@deployment.Notes</td>
                }
                <td>
                    @if (_editingId != deployment.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(deployment)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(deployment.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newDeployment" OnValidSubmit="CreateDeploymentAsync">
    <div class="row g-3 align-items-end">
        <div class="col-auto">
            <label class="form-label">סביבה</label>
            <InputSelect @bind-Value="_newDeployment.EnvironmentId" class="form-select" aria-label="סביבה">
                <option value="0">-- בחר/י --</option>
                @foreach (var environment in _environments)
                {
                    <option value="@environment.Id">@environment.Name</option>
                }
            </InputSelect>
        </div>
        <div class="col-auto">
            <label class="form-label">גרסה</label>
            <InputSelect @bind-Value="_newDeployment.VersionId" class="form-select" aria-label="גרסה">
                <option value="0">-- בחר/י --</option>
                @foreach (var version in _versions)
                {
                    <option value="@version.Id">@version.VersionNumber</option>
                }
            </InputSelect>
        </div>
        <div class="col-auto">
            <label class="form-label">תאריך פריסה (UTC)</label>
            <InputDate @bind-Value="_newDeployment.DeployedAt" Type="InputDateType.DateTimeLocal" class="form-control" aria-label="תאריך פריסה (UTC)" />
        </div>
        <div class="col-auto">
            <label class="form-label">הערות</label>
            <InputText @bind-Value="_newDeployment.Notes" class="form-control" aria-label="הערות" />
        </div>
        <div class="col-auto">
            <button type="submit" class="btn btn-primary">הוסף פריסה</button>
        </div>
    </div>
</EditForm>
@if (_createError is not null)
{
    <div class="alert alert-danger mt-3">@_createError</div>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<Deployment> _deployments = new();
    private List<AppEnvironment> _environments = new();
    private List<AppVersion> _versions = new();
    private readonly NewDeploymentModel _newDeployment = new();
    private string? _createError;

    private int? _editingId;
    private EditDeploymentModel? _editModel;
    private int? _lastLoadedApplicationId;

    private bool _isSuperAdmin;
    private int _currentPlatformUserId;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected override async Task OnParametersSetAsync()
    {
        if (_lastLoadedApplicationId == ApplicationId)
        {
            return;
        }

        var authState = await AuthenticationStateTask!;
        _isSuperAdmin = authState.User.FindFirstValue("IsAdmin") == "true";
        _currentPlatformUserId = int.Parse(authState.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        _lastLoadedApplicationId = ApplicationId;
        _createError = null;
        _editingId = null;
        _editModel = null;
        _deployments = (await DeploymentRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        _environments = (await EnvironmentRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        _versions = (await VersionRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private string EnvironmentName(int environmentId) =>
        _environments.FirstOrDefault(e => e.Id == environmentId)?.Name ?? $"#{environmentId}";

    private string VersionNumber(int versionId) =>
        _versions.FirstOrDefault(v => v.Id == versionId)?.VersionNumber ?? $"#{versionId}";

    private async Task CreateDeploymentAsync()
    {
        _createError = null;
        if (_newDeployment.EnvironmentId == 0 || _newDeployment.VersionId == 0)
        {
            _createError = "יש לבחור סביבה וגרסה.";
            return;
        }

        if (!_environments.Any(e => e.Id == _newDeployment.EnvironmentId) || !_versions.Any(v => v.Id == _newDeployment.VersionId))
        {
            _createError = "יש לבחור סביבה וגרסה תקינות עבור אפליקציה זו.";
            return;
        }

        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        var deployment = await DeploymentRepository.AddAsync(new Deployment
        {
            ApplicationId = ApplicationId,
            EnvironmentId = _newDeployment.EnvironmentId,
            VersionId = _newDeployment.VersionId,
            DeployedAt = _newDeployment.DeployedAt,
            Notes = _newDeployment.Notes
        });

        await Audit.RecordAsync(_currentPlatformUserId, "Deployment", deployment.Id.ToString(), "Create", $"Created deployment {deployment.Id} (environment {_newDeployment.EnvironmentId}, version {_newDeployment.VersionId}) in application {ApplicationId}");

        _newDeployment.EnvironmentId = 0;
        _newDeployment.VersionId = 0;
        _newDeployment.Notes = null;
        _deployments = (await DeploymentRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private void StartEdit(Deployment deployment)
    {
        _editingId = deployment.Id;
        _editModel = new EditDeploymentModel { Notes = deployment.Notes };
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
    }

    private async Task SaveRenameAsync(int deploymentId)
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        await DeploymentRepository.RenameAsync(deploymentId, _editModel!.Notes);

        await Audit.RecordAsync(_currentPlatformUserId, "Deployment", deploymentId.ToString(), "Update", $"Updated deployment {deploymentId} notes in application {ApplicationId}");

        _editingId = null;
        _editModel = null;
        _deployments = (await DeploymentRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task DeactivateAsync(int deploymentId)
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        await DeploymentRepository.DeactivateAsync(deploymentId);

        await Audit.RecordAsync(_currentPlatformUserId, "Deployment", deploymentId.ToString(), "Deactivate", $"Deactivated deployment {deploymentId} in application {ApplicationId}");

        _deployments = (await DeploymentRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private class NewDeploymentModel
    {
        public int EnvironmentId { get; set; }
        public int VersionId { get; set; }
        public DateTime DeployedAt { get; set; } = DateTime.UtcNow;
        public string? Notes { get; set; }
    }

    private class EditDeploymentModel
    {
        public string? Notes { get; set; }
    }
}
```

- [ ] **Step 6: Update LogSourcesSection.razor**

Replace the full contents of `src/LogsPlatform.Web/Components/Shared/LogSourcesSection.razor`:

```razor
@* src/LogsPlatform.Web/Components/Shared/LogSourcesSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using LogsPlatform.Web.Services
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.EntityFrameworkCore
@using System.Security.Claims
@inject ILogSourceRepository LogSourceRepository
@inject AuditLogger Audit
@inject ApplicationAccessService Access

<h4>מקורות לוגים</h4>
<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th>שם</th>
            <th>תיאור</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var logSource in _logSources)
        {
            <tr>
                @if (_editingId == logSource.Id)
                {
                    <td colspan="2">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(logSource.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Name" required maxlength="200" class="form-control form-control-sm" aria-label="שם" />
                                </div>
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Description" class="form-control form-control-sm" aria-label="תיאור" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <div class="alert alert-danger mt-2 mb-0 py-1">@_editError</div>
                        }
                    </td>
                }
                else
                {
                    <td>@logSource.Name</td>
                    <td>@logSource.Description</td>
                }
                <td>
                    @if (_editingId != logSource.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(logSource)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(logSource.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newLogSource" OnValidSubmit="CreateLogSourceAsync">
    <div class="row g-3 align-items-end">
        <div class="col-auto">
            <label class="form-label">שם</label>
            <InputText @bind-Value="_newLogSource.Name" required maxlength="200" class="form-control" aria-label="שם" />
        </div>
        <div class="col-auto">
            <label class="form-label">תיאור</label>
            <InputText @bind-Value="_newLogSource.Description" class="form-control" aria-label="תיאור" />
        </div>
        <div class="col-auto">
            <button type="submit" class="btn btn-primary">הוסף מקור לוגים</button>
        </div>
    </div>
</EditForm>
@if (_createError is not null)
{
    <div class="alert alert-danger mt-3">@_createError</div>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<LogSource> _logSources = new();
    private readonly NewLogSourceModel _newLogSource = new();
    private string? _createError;

    private int? _editingId;
    private EditLogSourceModel? _editModel;
    private string? _editError;

    private bool _isSuperAdmin;
    private int _currentPlatformUserId;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthenticationStateTask!;
        _isSuperAdmin = authState.User.FindFirstValue("IsAdmin") == "true";
        _currentPlatformUserId = int.Parse(authState.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        _logSources = (await LogSourceRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateLogSourceAsync()
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        try
        {
            var logSource = await LogSourceRepository.AddAsync(new LogSource
            {
                ApplicationId = ApplicationId,
                Name = _newLogSource.Name,
                Description = _newLogSource.Description
            });

            await Audit.RecordAsync(_currentPlatformUserId, "LogSource", logSource.Id.ToString(), "Create", $"Created log source '{logSource.Name}' in application {ApplicationId}");

            _newLogSource.Name = string.Empty;
            _newLogSource.Description = null;
            _logSources = (await LogSourceRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"מקור לוגים בשם '{_newLogSource.Name}' כבר קיים.";
        }
    }

    private void StartEdit(LogSource logSource)
    {
        _editingId = logSource.Id;
        _editModel = new EditLogSourceModel { Name = logSource.Name, Description = logSource.Description };
        _editError = null;
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
        _editError = null;
    }

    private async Task SaveRenameAsync(int logSourceId)
    {
        _editError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _editError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        try
        {
            await LogSourceRepository.RenameAsync(logSourceId, _editModel!.Name, _editModel!.Description);

            await Audit.RecordAsync(_currentPlatformUserId, "LogSource", logSourceId.ToString(), "Update", $"Renamed log source {logSourceId} to '{_editModel!.Name}' in application {ApplicationId}");

            _editingId = null;
            _editModel = null;
            _logSources = (await LogSourceRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _editError = $"מקור לוגים בשם '{_editModel!.Name}' כבר קיים.";
        }
    }

    private async Task DeactivateAsync(int logSourceId)
    {
        _editError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _editError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        await LogSourceRepository.DeactivateAsync(logSourceId);

        await Audit.RecordAsync(_currentPlatformUserId, "LogSource", logSourceId.ToString(), "Deactivate", $"Deactivated log source {logSourceId} in application {ApplicationId}");

        _logSources = (await LogSourceRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private class NewLogSourceModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    private class EditLogSourceModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
```

- [ ] **Step 7: Update UsersSection.razor**

Replace the full contents of `src/LogsPlatform.Web/Components/Shared/UsersSection.razor`:

```razor
@* src/LogsPlatform.Web/Components/Shared/UsersSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using LogsPlatform.Web.Services
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.EntityFrameworkCore
@using System.Security.Claims
@inject IAppUserRepository AppUserRepository
@inject AuditLogger Audit
@inject ApplicationAccessService Access

<h4>משתמשים</h4>
<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th>מזהה משתמש חיצוני</th>
            <th>שם תצוגה</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var user in _users)
        {
            <tr>
                @if (_editingId == user.Id)
                {
                    <td colspan="2">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(user.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.DisplayName" required maxlength="200" class="form-control form-control-sm" aria-label="שם תצוגה" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                    </td>
                }
                else
                {
                    <td>@user.ExternalUserId</td>
                    <td>@user.DisplayName</td>
                }
                <td>
                    @if (_editingId != user.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(user)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(user.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newUser" OnValidSubmit="CreateUserAsync">
    <div class="row g-3 align-items-end">
        <div class="col-auto">
            <label class="form-label">מזהה משתמש חיצוני</label>
            <InputText @bind-Value="_newUser.ExternalUserId" required maxlength="200" class="form-control" aria-label="מזהה משתמש חיצוני" />
        </div>
        <div class="col-auto">
            <label class="form-label">שם תצוגה</label>
            <InputText @bind-Value="_newUser.DisplayName" required maxlength="200" class="form-control" aria-label="שם תצוגה" />
        </div>
        <div class="col-auto">
            <button type="submit" class="btn btn-primary">הוסף משתמש</button>
        </div>
    </div>
</EditForm>
@if (_createError is not null)
{
    <div class="alert alert-danger mt-3">@_createError</div>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<AppUser> _users = new();
    private readonly NewUserModel _newUser = new();
    private string? _createError;

    private int? _editingId;
    private EditUserModel? _editModel;

    private bool _isSuperAdmin;
    private int _currentPlatformUserId;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthenticationStateTask!;
        _isSuperAdmin = authState.User.FindFirstValue("IsAdmin") == "true";
        _currentPlatformUserId = int.Parse(authState.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        _users = (await AppUserRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateUserAsync()
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        try
        {
            var user = await AppUserRepository.AddAsync(new AppUser
            {
                ApplicationId = ApplicationId,
                ExternalUserId = _newUser.ExternalUserId,
                DisplayName = _newUser.DisplayName
            });

            await Audit.RecordAsync(_currentPlatformUserId, "AppUser", user.Id.ToString(), "Create", $"Created user '{user.DisplayName}' (external id '{user.ExternalUserId}') in application {ApplicationId}");

            _newUser.ExternalUserId = string.Empty;
            _newUser.DisplayName = string.Empty;
            _users = (await AppUserRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"משתמש עם מזהה חיצוני '{_newUser.ExternalUserId}' כבר קיים.";
        }
    }

    private void StartEdit(AppUser user)
    {
        _editingId = user.Id;
        _editModel = new EditUserModel { DisplayName = user.DisplayName };
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
    }

    private async Task SaveRenameAsync(int userId)
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        await AppUserRepository.RenameAsync(userId, _editModel!.DisplayName);

        await Audit.RecordAsync(_currentPlatformUserId, "AppUser", userId.ToString(), "Update", $"Renamed user {userId} to '{_editModel!.DisplayName}' in application {ApplicationId}");

        _editingId = null;
        _editModel = null;
        _users = (await AppUserRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task DeactivateAsync(int userId)
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        await AppUserRepository.DeactivateAsync(userId);

        await Audit.RecordAsync(_currentPlatformUserId, "AppUser", userId.ToString(), "Deactivate", $"Deactivated user {userId} in application {ApplicationId}");

        _users = (await AppUserRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private class NewUserModel
    {
        public string ExternalUserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    private class EditUserModel
    {
        public string DisplayName { get; set; } = string.Empty;
    }
}
```

- [ ] **Step 8: Update VersionsSection.razor**

Replace the full contents of `src/LogsPlatform.Web/Components/Shared/VersionsSection.razor`:

```razor
@* src/LogsPlatform.Web/Components/Shared/VersionsSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using LogsPlatform.Web.Services
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.EntityFrameworkCore
@using System.Security.Claims
@inject IAppVersionRepository VersionRepository
@inject AuditLogger Audit
@inject ApplicationAccessService Access

<h4>גרסאות</h4>
<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th>מספר גרסה</th>
            <th>הערות גרסה</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var version in _versions)
        {
            <tr>
                @if (_editingId == version.Id)
                {
                    <td colspan="2">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(version.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.ReleaseNotes" class="form-control form-control-sm" aria-label="הערות גרסה" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                    </td>
                }
                else
                {
                    <td>@version.VersionNumber</td>
                    <td>@version.ReleaseNotes</td>
                }
                <td>
                    @if (_editingId != version.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(version)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(version.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newVersion" OnValidSubmit="CreateVersionAsync">
    <div class="row g-3 align-items-end">
        <div class="col-auto">
            <label class="form-label">מספר גרסה</label>
            <InputText @bind-Value="_newVersion.VersionNumber" required maxlength="200" class="form-control" aria-label="מספר גרסה" />
        </div>
        <div class="col-auto">
            <label class="form-label">הערות גרסה</label>
            <InputText @bind-Value="_newVersion.ReleaseNotes" class="form-control" aria-label="הערות גרסה" />
        </div>
        <div class="col-auto">
            <button type="submit" class="btn btn-primary">הוסף גרסה</button>
        </div>
    </div>
</EditForm>
@if (_createError is not null)
{
    <div class="alert alert-danger mt-3">@_createError</div>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<AppVersion> _versions = new();
    private readonly NewVersionModel _newVersion = new();
    private string? _createError;

    private int? _editingId;
    private EditVersionModel? _editModel;
    private int? _lastLoadedApplicationId;

    private bool _isSuperAdmin;
    private int _currentPlatformUserId;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected override async Task OnParametersSetAsync()
    {
        if (_lastLoadedApplicationId == ApplicationId)
        {
            return;
        }

        var authState = await AuthenticationStateTask!;
        _isSuperAdmin = authState.User.FindFirstValue("IsAdmin") == "true";
        _currentPlatformUserId = int.Parse(authState.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        _lastLoadedApplicationId = ApplicationId;
        _createError = null;
        _editingId = null;
        _editModel = null;
        _versions = (await VersionRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateVersionAsync()
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        try
        {
            var version = await VersionRepository.AddAsync(new AppVersion
            {
                ApplicationId = ApplicationId,
                VersionNumber = _newVersion.VersionNumber,
                ReleaseNotes = _newVersion.ReleaseNotes,
                CreatedAt = DateTime.UtcNow
            });

            await Audit.RecordAsync(_currentPlatformUserId, "AppVersion", version.Id.ToString(), "Create", $"Created version '{version.VersionNumber}' in application {ApplicationId}");

            _newVersion.VersionNumber = string.Empty;
            _newVersion.ReleaseNotes = null;
            _versions = (await VersionRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"גרסה '{_newVersion.VersionNumber}' כבר קיימת.";
        }
    }

    private void StartEdit(AppVersion version)
    {
        _editingId = version.Id;
        _editModel = new EditVersionModel { ReleaseNotes = version.ReleaseNotes };
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
    }

    private async Task SaveRenameAsync(int versionId)
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        await VersionRepository.RenameAsync(versionId, _editModel!.ReleaseNotes);

        await Audit.RecordAsync(_currentPlatformUserId, "AppVersion", versionId.ToString(), "Update", $"Updated version {versionId} release notes in application {ApplicationId}");

        _editingId = null;
        _editModel = null;
        _versions = (await VersionRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task DeactivateAsync(int versionId)
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, ApplicationId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        await VersionRepository.DeactivateAsync(versionId);

        await Audit.RecordAsync(_currentPlatformUserId, "AppVersion", versionId.ToString(), "Deactivate", $"Deactivated version {versionId} in application {ApplicationId}");

        _versions = (await VersionRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private class NewVersionModel
    {
        public string VersionNumber { get; set; } = string.Empty;
        public string? ReleaseNotes { get; set; }
    }

    private class EditVersionModel
    {
        public string? ReleaseNotes { get; set; }
    }
}
```

- [ ] **Step 9: Build and run the full suite**

Run: `dotnet build` then `dotnet test`
Expected: 0 build errors, all tests still green (these are Blazor-only changes; no test file references these components directly, so no test count change is expected here).

- [ ] **Step 10: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor \
        src/LogsPlatform.Web/Components/Shared/ApiKeysSection.razor \
        src/LogsPlatform.Web/Components/Shared/CustomersSection.razor \
        src/LogsPlatform.Web/Components/Shared/DeploymentsSection.razor \
        src/LogsPlatform.Web/Components/Shared/LogSourcesSection.razor \
        src/LogsPlatform.Web/Components/Shared/UsersSection.razor \
        src/LogsPlatform.Web/Components/Shared/VersionsSection.razor
git commit -m "feat: wire per-application access grants into ApplicationsAdmin and its nested sections"
```

---

## Task 7: ModulesAdmin, ScreenServicesAdmin, ProcessesAdmin, OperationsAdmin pages

All four already carry `AppId` as a page route parameter directly (confirmed by reading each fresh) — no chain-walking needed here, unlike their controller counterparts in Task 5.

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Pages/ModulesAdmin.razor`
- Modify: `src/LogsPlatform.Web/Components/Pages/ScreenServicesAdmin.razor`
- Modify: `src/LogsPlatform.Web/Components/Pages/ProcessesAdmin.razor`
- Modify: `src/LogsPlatform.Web/Components/Pages/OperationsAdmin.razor`

**Interfaces:**
- Consumes: `ApplicationAccessService.CanManageApplicationAsync(bool isSuperAdmin, int platformUserId, int applicationId) : Task<bool>` (Task 1).
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Update ModulesAdmin.razor**

Replace the full contents of `src/LogsPlatform.Web/Components/Pages/ModulesAdmin.razor`:

```razor
@* src/LogsPlatform.Web/Components/Pages/ModulesAdmin.razor *@
@page "/admin/applications/{AppId:int}/modules"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using LogsPlatform.Web.Services
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.EntityFrameworkCore
@using System.Security.Claims
@inject IAppModuleRepository ModuleRepository
@inject BreadcrumbBuilder BreadcrumbBuilder
@inject AuditLogger Audit
@inject ApplicationAccessService Access
@rendermode InteractiveServer

<nav aria-label="breadcrumb">
    <ol class="breadcrumb">
        @for (var i = 0; i < _breadcrumb.Count; i++)
        {
            var segment = _breadcrumb[i];
            if (i == _breadcrumb.Count - 1)
            {
                <li class="breadcrumb-item active" aria-current="page">@segment.Label</li>
            }
            else
            {
                <li class="breadcrumb-item"><a href="@segment.Url">@segment.Label</a></li>
            }
        }
    </ol>
</nav>

<h1>מודולים</h1>

<div class="card mb-4">
    <div class="card-header">הוספת מודול</div>
    <div class="card-body">
        <EditForm Model="_newModule" OnValidSubmit="CreateModuleAsync">
            <div class="row g-3 align-items-end">
                <div class="col-auto">
                    <label class="form-label">שם</label>
                    <InputText @bind-Value="_newModule.Name" required maxlength="200" class="form-control" aria-label="שם" />
                </div>
                <div class="col-auto">
                    <label class="form-label">תיאור</label>
                    <InputText @bind-Value="_newModule.Description" class="form-control" aria-label="תיאור" />
                </div>
                <div class="col-auto">
                    <button type="submit" class="btn btn-primary">צור</button>
                </div>
            </div>
        </EditForm>
        @if (_createError is not null)
        {
            <div class="alert alert-danger mt-3 mb-0">@_createError</div>
        }
    </div>
</div>

<table class="table table-striped table-hover align-middle">
    <thead>
        <tr>
            <th>שם</th>
            <th>תיאור</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var module in _modules)
        {
            <tr>
                @if (_editingId == module.Id)
                {
                    <td colspan="2">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(module.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Name" required maxlength="200" class="form-control form-control-sm" aria-label="שם" />
                                </div>
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Description" class="form-control form-control-sm" aria-label="תיאור" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <div class="alert alert-danger mt-2 mb-0 py-1">@_editError</div>
                        }
                    </td>
                }
                else
                {
                    <td><a href="/admin/applications/@AppId/modules/@module.Id/screen-services">@module.Name</a></td>
                    <td>@module.Description</td>
                }
                <td>
                    @if (_editingId != module.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(module)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(module.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

@code {
    [Parameter] public int AppId { get; set; }

    private List<AppModule> _modules = new();
    private List<BreadcrumbSegment> _breadcrumb = new();
    private readonly NewModuleModel _newModule = new();
    private string? _createError;

    private int? _editingId;
    private EditModuleModel? _editModel;
    private string? _editError;

    private bool _isSuperAdmin;
    private int _currentPlatformUserId;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthenticationStateTask!;
        _isSuperAdmin = authState.User.FindFirstValue("IsAdmin") == "true";
        _currentPlatformUserId = int.Parse(authState.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        _breadcrumb = await BreadcrumbBuilder.BuildAsync(AppId);
        _modules = (await ModuleRepository.GetByApplicationIdAsync(AppId)).ToList();
    }

    private async Task CreateModuleAsync()
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, AppId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        try
        {
            var module = await ModuleRepository.AddAsync(new AppModule
            {
                ApplicationId = AppId,
                Name = _newModule.Name,
                Description = _newModule.Description
            });

            await Audit.RecordAsync(_currentPlatformUserId, "AppModule", module.Id.ToString(), "Create", $"Created module '{module.Name}' in application {AppId}");

            _newModule.Name = string.Empty;
            _newModule.Description = null;
            _modules = (await ModuleRepository.GetByApplicationIdAsync(AppId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"מודול בשם '{_newModule.Name}' כבר קיים.";
        }
    }

    private void StartEdit(AppModule module)
    {
        _editingId = module.Id;
        _editModel = new EditModuleModel { Name = module.Name, Description = module.Description };
        _editError = null;
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
        _editError = null;
    }

    private async Task SaveRenameAsync(int moduleId)
    {
        _editError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, AppId))
        {
            _editError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        try
        {
            await ModuleRepository.RenameAsync(moduleId, _editModel!.Name, _editModel!.Description);

            await Audit.RecordAsync(_currentPlatformUserId, "AppModule", moduleId.ToString(), "Update", $"Renamed module {moduleId} to '{_editModel!.Name}' in application {AppId}");

            _editingId = null;
            _editModel = null;
            _modules = (await ModuleRepository.GetByApplicationIdAsync(AppId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _editError = $"מודול בשם '{_editModel!.Name}' כבר קיים.";
        }
    }

    private async Task DeactivateAsync(int moduleId)
    {
        _editError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, AppId))
        {
            _editError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        await ModuleRepository.DeactivateAsync(moduleId);

        await Audit.RecordAsync(_currentPlatformUserId, "AppModule", moduleId.ToString(), "Deactivate", $"Deactivated module {moduleId} in application {AppId}");

        _modules = (await ModuleRepository.GetByApplicationIdAsync(AppId)).ToList();
    }

    private class NewModuleModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    private class EditModuleModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
```

- [ ] **Step 2: Update ScreenServicesAdmin.razor**

Replace the full contents of `src/LogsPlatform.Web/Components/Pages/ScreenServicesAdmin.razor`:

```razor
@* src/LogsPlatform.Web/Components/Pages/ScreenServicesAdmin.razor *@
@page "/admin/applications/{AppId:int}/modules/{ModuleId:int}/screen-services"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using LogsPlatform.Web.Services
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.EntityFrameworkCore
@using System.Security.Claims
@inject IScreenServiceRepository ScreenServiceRepository
@inject BreadcrumbBuilder BreadcrumbBuilder
@inject AuditLogger Audit
@inject ApplicationAccessService Access
@rendermode InteractiveServer

<nav aria-label="breadcrumb">
    <ol class="breadcrumb">
        @for (var i = 0; i < _breadcrumb.Count; i++)
        {
            var segment = _breadcrumb[i];
            if (i == _breadcrumb.Count - 1)
            {
                <li class="breadcrumb-item active" aria-current="page">@segment.Label</li>
            }
            else
            {
                <li class="breadcrumb-item"><a href="@segment.Url">@segment.Label</a></li>
            }
        }
    </ol>
</nav>

<h1>מסכים/שירותים</h1>

<div class="card mb-4">
    <div class="card-header">הוספת מסך/שירות</div>
    <div class="card-body">
        <EditForm Model="_newScreenService" OnValidSubmit="CreateScreenServiceAsync">
            <div class="row g-3 align-items-end">
                <div class="col-auto">
                    <label class="form-label">שם</label>
                    <InputText @bind-Value="_newScreenService.Name" required maxlength="200" class="form-control" aria-label="שם" />
                </div>
                <div class="col-auto">
                    <label class="form-label">סוג</label>
                    <InputSelect @bind-Value="_newScreenService.Type" class="form-select" aria-label="סוג">
                        <option value="@ScreenServiceType.Screen">מסך</option>
                        <option value="@ScreenServiceType.Service">שירות</option>
                    </InputSelect>
                </div>
                <div class="col-auto">
                    <label class="form-label">תיאור</label>
                    <InputText @bind-Value="_newScreenService.Description" class="form-control" aria-label="תיאור" />
                </div>
                <div class="col-auto">
                    <button type="submit" class="btn btn-primary">צור</button>
                </div>
            </div>
        </EditForm>
        @if (_createError is not null)
        {
            <div class="alert alert-danger mt-3 mb-0">@_createError</div>
        }
    </div>
</div>

<table class="table table-striped table-hover align-middle">
    <thead>
        <tr>
            <th>שם</th>
            <th>סוג</th>
            <th>תיאור</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var screenService in _screenServices)
        {
            <tr>
                @if (_editingId == screenService.Id)
                {
                    <td colspan="3">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(screenService.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Name" required maxlength="200" class="form-control form-control-sm" aria-label="שם" />
                                </div>
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Description" class="form-control form-control-sm" aria-label="תיאור" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <div class="alert alert-danger mt-2 mb-0 py-1">@_editError</div>
                        }
                    </td>
                }
                else
                {
                    <td>
                        <a href="/admin/applications/@AppId/modules/@ModuleId/screen-services/@screenService.Id/processes">@screenService.Name</a>
                    </td>
                    <td>@TypeLabel(screenService.Type)</td>
                    <td>@screenService.Description</td>
                }
                <td>
                    @if (_editingId != screenService.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(screenService)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(screenService.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

@code {
    [Parameter] public int AppId { get; set; }
    [Parameter] public int ModuleId { get; set; }

    private List<ScreenService> _screenServices = new();
    private List<BreadcrumbSegment> _breadcrumb = new();
    private readonly NewScreenServiceModel _newScreenService = new();
    private string? _createError;

    private int? _editingId;
    private EditScreenServiceModel? _editModel;
    private string? _editError;

    private bool _isSuperAdmin;
    private int _currentPlatformUserId;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthenticationStateTask!;
        _isSuperAdmin = authState.User.FindFirstValue("IsAdmin") == "true";
        _currentPlatformUserId = int.Parse(authState.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        _breadcrumb = await BreadcrumbBuilder.BuildAsync(AppId, ModuleId);
        _screenServices = (await ScreenServiceRepository.GetByModuleIdAsync(ModuleId)).ToList();
    }

    private async Task CreateScreenServiceAsync()
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, AppId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        try
        {
            var screenService = await ScreenServiceRepository.AddAsync(new ScreenService
            {
                ModuleId = ModuleId,
                Name = _newScreenService.Name,
                Type = _newScreenService.Type,
                Description = _newScreenService.Description
            });

            await Audit.RecordAsync(_currentPlatformUserId, "ScreenService", screenService.Id.ToString(), "Create", $"Created screen/service '{screenService.Name}' in module {ModuleId}");

            _newScreenService.Name = string.Empty;
            _newScreenService.Type = ScreenServiceType.Screen;
            _newScreenService.Description = null;
            _screenServices = (await ScreenServiceRepository.GetByModuleIdAsync(ModuleId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"מסך/שירות בשם '{_newScreenService.Name}' כבר קיים.";
        }
    }

    private static string TypeLabel(ScreenServiceType type) => type switch
    {
        ScreenServiceType.Screen => "מסך",
        ScreenServiceType.Service => "שירות",
        _ => type.ToString()
    };

    private void StartEdit(ScreenService screenService)
    {
        _editingId = screenService.Id;
        _editModel = new EditScreenServiceModel { Name = screenService.Name, Description = screenService.Description };
        _editError = null;
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
        _editError = null;
    }

    private async Task SaveRenameAsync(int screenServiceId)
    {
        _editError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, AppId))
        {
            _editError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        try
        {
            await ScreenServiceRepository.RenameAsync(screenServiceId, _editModel!.Name, _editModel!.Description);

            await Audit.RecordAsync(_currentPlatformUserId, "ScreenService", screenServiceId.ToString(), "Update", $"Renamed screen/service {screenServiceId} to '{_editModel!.Name}' in module {ModuleId}");

            _editingId = null;
            _editModel = null;
            _screenServices = (await ScreenServiceRepository.GetByModuleIdAsync(ModuleId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _editError = $"מסך/שירות בשם '{_editModel!.Name}' כבר קיים.";
        }
    }

    private async Task DeactivateAsync(int screenServiceId)
    {
        _editError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, AppId))
        {
            _editError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        await ScreenServiceRepository.DeactivateAsync(screenServiceId);

        await Audit.RecordAsync(_currentPlatformUserId, "ScreenService", screenServiceId.ToString(), "Deactivate", $"Deactivated screen/service {screenServiceId} in module {ModuleId}");

        _screenServices = (await ScreenServiceRepository.GetByModuleIdAsync(ModuleId)).ToList();
    }

    private class NewScreenServiceModel
    {
        public string Name { get; set; } = string.Empty;
        public ScreenServiceType Type { get; set; } = ScreenServiceType.Screen;
        public string? Description { get; set; }
    }

    private class EditScreenServiceModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: 0 errors — confirms Steps 1–2 compile before continuing to Steps 4–5.

- [ ] **Step 4: Update ProcessesAdmin.razor**

Replace the full contents of `src/LogsPlatform.Web/Components/Pages/ProcessesAdmin.razor`:

```razor
@* src/LogsPlatform.Web/Components/Pages/ProcessesAdmin.razor *@
@page "/admin/applications/{AppId:int}/modules/{ModuleId:int}/screen-services/{ScreenServiceId:int}/processes"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using LogsPlatform.Web.Services
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.EntityFrameworkCore
@using System.Security.Claims
@inject IProcessNodeRepository ProcessRepository
@inject BreadcrumbBuilder BreadcrumbBuilder
@inject AuditLogger Audit
@inject ApplicationAccessService Access
@rendermode InteractiveServer

<nav aria-label="breadcrumb">
    <ol class="breadcrumb">
        @for (var i = 0; i < _breadcrumb.Count; i++)
        {
            var segment = _breadcrumb[i];
            if (i == _breadcrumb.Count - 1)
            {
                <li class="breadcrumb-item active" aria-current="page">@segment.Label</li>
            }
            else
            {
                <li class="breadcrumb-item"><a href="@segment.Url">@segment.Label</a></li>
            }
        }
    </ol>
</nav>

<h1>תהליכים</h1>

<div class="card mb-4">
    <div class="card-header">הוספת תהליך</div>
    <div class="card-body">
        <EditForm Model="_newProcess" OnValidSubmit="CreateProcessAsync">
            <div class="row g-3 align-items-end">
                <div class="col-auto">
                    <label class="form-label">שם</label>
                    <InputText @bind-Value="_newProcess.Name" required maxlength="200" class="form-control" aria-label="שם" />
                </div>
                <div class="col-auto">
                    <label class="form-label">תיאור</label>
                    <InputText @bind-Value="_newProcess.Description" class="form-control" aria-label="תיאור" />
                </div>
                <div class="col-auto">
                    <button type="submit" class="btn btn-primary">צור</button>
                </div>
            </div>
        </EditForm>
        @if (_createError is not null)
        {
            <div class="alert alert-danger mt-3 mb-0">@_createError</div>
        }
    </div>
</div>

<table class="table table-striped table-hover align-middle">
    <thead>
        <tr>
            <th>שם</th>
            <th>תיאור</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var process in _processes)
        {
            <tr>
                @if (_editingId == process.Id)
                {
                    <td colspan="2">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(process.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Name" required maxlength="200" class="form-control form-control-sm" aria-label="שם" />
                                </div>
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Description" class="form-control form-control-sm" aria-label="תיאור" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <div class="alert alert-danger mt-2 mb-0 py-1">@_editError</div>
                        }
                    </td>
                }
                else
                {
                    <td>
                        <a href="/admin/applications/@AppId/modules/@ModuleId/screen-services/@ScreenServiceId/processes/@process.Id/operations">@process.Name</a>
                    </td>
                    <td>@process.Description</td>
                }
                <td>
                    @if (_editingId != process.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(process)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(process.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

@code {
    [Parameter] public int AppId { get; set; }
    [Parameter] public int ModuleId { get; set; }
    [Parameter] public int ScreenServiceId { get; set; }

    private List<ProcessNode> _processes = new();
    private List<BreadcrumbSegment> _breadcrumb = new();
    private readonly NewProcessModel _newProcess = new();
    private string? _createError;

    private int? _editingId;
    private EditProcessModel? _editModel;
    private string? _editError;

    private bool _isSuperAdmin;
    private int _currentPlatformUserId;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthenticationStateTask!;
        _isSuperAdmin = authState.User.FindFirstValue("IsAdmin") == "true";
        _currentPlatformUserId = int.Parse(authState.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        _breadcrumb = await BreadcrumbBuilder.BuildAsync(AppId, ModuleId, ScreenServiceId);
        _processes = (await ProcessRepository.GetByScreenServiceIdAsync(ScreenServiceId)).ToList();
    }

    private async Task CreateProcessAsync()
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, AppId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        try
        {
            var process = await ProcessRepository.AddAsync(new ProcessNode
            {
                ScreenServiceId = ScreenServiceId,
                Name = _newProcess.Name,
                Description = _newProcess.Description
            });

            await Audit.RecordAsync(_currentPlatformUserId, "ProcessNode", process.Id.ToString(), "Create", $"Created process '{process.Name}' in screen/service {ScreenServiceId}");

            _newProcess.Name = string.Empty;
            _newProcess.Description = null;
            _processes = (await ProcessRepository.GetByScreenServiceIdAsync(ScreenServiceId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"תהליך בשם '{_newProcess.Name}' כבר קיים.";
        }
    }

    private void StartEdit(ProcessNode process)
    {
        _editingId = process.Id;
        _editModel = new EditProcessModel { Name = process.Name, Description = process.Description };
        _editError = null;
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
        _editError = null;
    }

    private async Task SaveRenameAsync(int processId)
    {
        _editError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, AppId))
        {
            _editError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        try
        {
            await ProcessRepository.RenameAsync(processId, _editModel!.Name, _editModel!.Description);

            await Audit.RecordAsync(_currentPlatformUserId, "ProcessNode", processId.ToString(), "Update", $"Renamed process {processId} to '{_editModel!.Name}' in screen/service {ScreenServiceId}");

            _editingId = null;
            _editModel = null;
            _processes = (await ProcessRepository.GetByScreenServiceIdAsync(ScreenServiceId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _editError = $"תהליך בשם '{_editModel!.Name}' כבר קיים.";
        }
    }

    private async Task DeactivateAsync(int processId)
    {
        _editError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, AppId))
        {
            _editError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        await ProcessRepository.DeactivateAsync(processId);

        await Audit.RecordAsync(_currentPlatformUserId, "ProcessNode", processId.ToString(), "Deactivate", $"Deactivated process {processId} in screen/service {ScreenServiceId}");

        _processes = (await ProcessRepository.GetByScreenServiceIdAsync(ScreenServiceId)).ToList();
    }

    private class NewProcessModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    private class EditProcessModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
```

- [ ] **Step 5: Update OperationsAdmin.razor**

Replace the full contents of `src/LogsPlatform.Web/Components/Pages/OperationsAdmin.razor`:

```razor
@* src/LogsPlatform.Web/Components/Pages/OperationsAdmin.razor *@
@page "/admin/applications/{AppId:int}/modules/{ModuleId:int}/screen-services/{ScreenServiceId:int}/processes/{ProcessId:int}/operations"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using LogsPlatform.Web.Services
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.EntityFrameworkCore
@using System.Security.Claims
@inject IOperationRepository OperationRepository
@inject BreadcrumbBuilder BreadcrumbBuilder
@inject AuditLogger Audit
@inject ApplicationAccessService Access
@rendermode InteractiveServer

<nav aria-label="breadcrumb">
    <ol class="breadcrumb">
        @for (var i = 0; i < _breadcrumb.Count; i++)
        {
            var segment = _breadcrumb[i];
            if (i == _breadcrumb.Count - 1)
            {
                <li class="breadcrumb-item active" aria-current="page">@segment.Label</li>
            }
            else
            {
                <li class="breadcrumb-item"><a href="@segment.Url">@segment.Label</a></li>
            }
        }
    </ol>
</nav>

<h1>פעולות</h1>

<div class="card mb-4">
    <div class="card-header">הוספת פעולה</div>
    <div class="card-body">
        <EditForm Model="_newOperation" OnValidSubmit="CreateOperationAsync">
            <div class="row g-3 align-items-end">
                <div class="col-auto">
                    <label class="form-label">שם</label>
                    <InputText @bind-Value="_newOperation.Name" required maxlength="200" class="form-control" aria-label="שם" />
                </div>
                <div class="col-auto">
                    <label class="form-label">תיאור</label>
                    <InputText @bind-Value="_newOperation.Description" class="form-control" aria-label="תיאור" />
                </div>
                <div class="col-auto">
                    <button type="submit" class="btn btn-primary">צור</button>
                </div>
            </div>
        </EditForm>
        @if (_createError is not null)
        {
            <div class="alert alert-danger mt-3 mb-0">@_createError</div>
        }
    </div>
</div>

<table class="table table-striped table-hover align-middle">
    <thead>
        <tr>
            <th>שם</th>
            <th>תיאור</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var operation in _operations)
        {
            <tr>
                @if (_editingId == operation.Id)
                {
                    <td colspan="2">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(operation.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Name" required maxlength="200" class="form-control form-control-sm" aria-label="שם" />
                                </div>
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Description" class="form-control form-control-sm" aria-label="תיאור" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <div class="alert alert-danger mt-2 mb-0 py-1">@_editError</div>
                        }
                    </td>
                }
                else
                {
                    <td>@operation.Name</td>
                    <td>@operation.Description</td>
                }
                <td>
                    @if (_editingId != operation.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(operation)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(operation.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

@code {
    [Parameter] public int AppId { get; set; }
    [Parameter] public int ModuleId { get; set; }
    [Parameter] public int ScreenServiceId { get; set; }
    [Parameter] public int ProcessId { get; set; }

    private List<Operation> _operations = new();
    private List<BreadcrumbSegment> _breadcrumb = new();
    private readonly NewOperationModel _newOperation = new();
    private string? _createError;

    private int? _editingId;
    private EditOperationModel? _editModel;
    private string? _editError;

    private bool _isSuperAdmin;
    private int _currentPlatformUserId;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthenticationStateTask!;
        _isSuperAdmin = authState.User.FindFirstValue("IsAdmin") == "true";
        _currentPlatformUserId = int.Parse(authState.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        _breadcrumb = await BreadcrumbBuilder.BuildAsync(AppId, ModuleId, ScreenServiceId, ProcessId);
        _operations = (await OperationRepository.GetByProcessIdAsync(ProcessId)).ToList();
    }

    private async Task CreateOperationAsync()
    {
        _createError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, AppId))
        {
            _createError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        try
        {
            var operation = await OperationRepository.AddAsync(new Operation
            {
                ProcessId = ProcessId,
                Name = _newOperation.Name,
                Description = _newOperation.Description
            });

            await Audit.RecordAsync(_currentPlatformUserId, "Operation", operation.Id.ToString(), "Create", $"Created operation '{operation.Name}' in process {ProcessId}");

            _newOperation.Name = string.Empty;
            _newOperation.Description = null;
            _operations = (await OperationRepository.GetByProcessIdAsync(ProcessId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"פעולה בשם '{_newOperation.Name}' כבר קיימת.";
        }
    }

    private void StartEdit(Operation operation)
    {
        _editingId = operation.Id;
        _editModel = new EditOperationModel { Name = operation.Name, Description = operation.Description };
        _editError = null;
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
        _editError = null;
    }

    private async Task SaveRenameAsync(int operationId)
    {
        _editError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, AppId))
        {
            _editError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        try
        {
            await OperationRepository.RenameAsync(operationId, _editModel!.Name, _editModel!.Description);

            await Audit.RecordAsync(_currentPlatformUserId, "Operation", operationId.ToString(), "Update", $"Renamed operation {operationId} to '{_editModel!.Name}' in process {ProcessId}");

            _editingId = null;
            _editModel = null;
            _operations = (await OperationRepository.GetByProcessIdAsync(ProcessId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _editError = $"פעולה בשם '{_editModel!.Name}' כבר קיימת.";
        }
    }

    private async Task DeactivateAsync(int operationId)
    {
        _editError = null;
        if (!await Access.CanManageApplicationAsync(_isSuperAdmin, _currentPlatformUserId, AppId))
        {
            _editError = "אין לך הרשאת ניהול לאפליקציה זו.";
            return;
        }

        await OperationRepository.DeactivateAsync(operationId);

        await Audit.RecordAsync(_currentPlatformUserId, "Operation", operationId.ToString(), "Deactivate", $"Deactivated operation {operationId} in process {ProcessId}");

        _operations = (await OperationRepository.GetByProcessIdAsync(ProcessId)).ToList();
    }

    private class NewOperationModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    private class EditOperationModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
```

- [ ] **Step 6: Build and run the full suite**

Run: `dotnet build` then `dotnet test`
Expected: 0 build errors, all tests still green.

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/ModulesAdmin.razor \
        src/LogsPlatform.Web/Components/Pages/ScreenServicesAdmin.razor \
        src/LogsPlatform.Web/Components/Pages/ProcessesAdmin.razor \
        src/LogsPlatform.Web/Components/Pages/OperationsAdmin.razor
git commit -m "feat: wire per-application access grants into Module/ScreenService/Process/Operation admin pages"
```

---

## Task 8: NavMenu visibility + grant-management UI + manual verification

`NavMenu.razor`'s "ניהול" link must become visible to grant-holders, not just Super Admins (its "משתמשי מערכת"/"Audit Log" links stay Super-Admin-only, unchanged). `PlatformUsersSection.razor` gains the grant-management UI the design doc calls for (§3.3) — no new API endpoint, direct repository calls, matching every other admin component in this project.

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Layout/NavMenu.razor`
- Modify: `src/LogsPlatform.Web/Components/Shared/PlatformUsersSection.razor`

**Interfaces:**
- Consumes: `IApplicationAccessGrantRepository.HasAnyGrantAsync(int platformUserId) : Task<bool>`, `.GetGrantedApplicationIdsAsync(int platformUserId) : Task<IReadOnlyList<int>>`, `.GrantAsync(int platformUserId, int applicationId) : Task`, `.RevokeAsync(int platformUserId, int applicationId) : Task` (all Task 1). `IApplicationRepository.GetAllAsync() : Task<IReadOnlyList<Application>>` (existing).
- Produces: nothing — this is the last task.

- [ ] **Step 1: Update NavMenu.razor**

Replace the full contents of `src/LogsPlatform.Web/Components/Layout/NavMenu.razor`:

```razor
@* src/LogsPlatform.Web/Components/Layout/NavMenu.razor *@
@using LogsPlatform.Domain.Repositories
@using Microsoft.AspNetCore.Components.Authorization
@using System.Security.Claims
@inject IApplicationAccessGrantRepository AccessGrants
<nav class="navbar navbar-expand navbar-dark bg-dark mb-4">
    <div class="container-fluid">
        <a class="navbar-brand" href="/">LogsPlatform</a>
        <ul class="navbar-nav me-auto">
            <li class="nav-item">
                <NavLink class="nav-link" href="/" Match="NavLinkMatch.All">
                    מה חריג
                </NavLink>
            </li>
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
            @if (_canSeeManagement)
            {
                <li class="nav-item">
                    <NavLink class="nav-link" href="/admin/applications" Match="NavLinkMatch.Prefix">
                        ניהול
                    </NavLink>
                </li>
            }
            <AuthorizeView Policy="RequireAdmin">
                <Authorized>
                    <li class="nav-item">
                        <NavLink class="nav-link" href="/admin/platform-users" Match="NavLinkMatch.Prefix">
                            משתמשי מערכת
                        </NavLink>
                    </li>
                    <li class="nav-item">
                        <NavLink class="nav-link" href="/admin/audit-log" Match="NavLinkMatch.Prefix">
                            Audit Log
                        </NavLink>
                    </li>
                </Authorized>
            </AuthorizeView>
        </ul>
        <AuthorizeView>
            <Authorized>
                <span class="navbar-text text-light me-3">@context.User.Identity?.Name</span>
                <form method="post" action="/api/v1/auth/logout">
                    <button type="submit" class="btn btn-sm btn-outline-light">התנתק/י</button>
                </form>
            </Authorized>
        </AuthorizeView>
    </div>
</nav>

@code {
    private bool _canSeeManagement;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected override async Task OnInitializedAsync()
    {
        if (AuthenticationStateTask is null) return;

        var authState = await AuthenticationStateTask;
        var user = authState.User;
        if (user.Identity?.IsAuthenticated != true) return;

        if (user.FindFirstValue("IsAdmin") == "true")
        {
            _canSeeManagement = true;
            return;
        }

        var platformUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        _canSeeManagement = await AccessGrants.HasAnyGrantAsync(platformUserId);
    }
}
```

- [ ] **Step 2: Update PlatformUsersSection.razor**

Replace the full contents of `src/LogsPlatform.Web/Components/Shared/PlatformUsersSection.razor`:

```razor
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Infrastructure
@using LogsPlatform.Web
@using LogsPlatform.Web.Services
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.EntityFrameworkCore
@using System.Security.Claims
@inject IPlatformUserRepository PlatformUserRepository
@inject IApplicationRepository ApplicationRepository
@inject IApplicationAccessGrantRepository AccessGrants
@inject AuditLogger Audit

<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th></th>
            <th>שם משתמש</th>
            <th>מנהל/ת מערכת</th>
            <th>פעיל/ה</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var user in _users)
        {
            <tr @key="user.Id">
                <td>
                    <button class="btn btn-sm btn-outline-secondary" @onclick="() => ToggleExpandAsync(user.Id)">
                        @(_expandedUserIds.Contains(user.Id) ? "-" : "+")
                    </button>
                </td>
                <td>@user.Username</td>
                <td>@(user.IsAdmin ? "כן" : "לא")</td>
                <td>@(user.IsActive ? "כן" : "לא")</td>
                <td>
                    @if (user.IsActive)
                    {
                        <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(user.Id)">השבת/י</button>
                    }
                </td>
            </tr>
            @if (_expandedUserIds.Contains(user.Id))
            {
                <tr>
                    <td colspan="5">
                        <h5>אפליקציות מוענקות</h5>
                        @if (user.IsAdmin)
                        {
                            <p class="text-muted">משתמש/ת זו הוא/היא מנהל/ת-על ורואה/ה את כל האפליקציות ללא צורך בהענקה.</p>
                        }
                        @if (_grantedAppIdsByUserId.TryGetValue(user.Id, out var grantedAppIds))
                        {
                            @foreach (var application in _applications)
                            {
                                <div class="form-check">
                                    <input type="checkbox" class="form-check-input" id="@($"grant-{user.Id}-{application.Id}")"
                                           checked="@grantedAppIds.Contains(application.Id)"
                                           @onchange="@(e => ToggleGrantAsync(user.Id, application.Id, (bool)e.Value!))" />
                                    <label class="form-check-label" for="@($"grant-{user.Id}-{application.Id}")">@application.Name</label>
                                </div>
                            }
                        }
                    </td>
                </tr>
            }
        }
    </tbody>
</table>

<EditForm Model="_newUser" OnValidSubmit="CreateUserAsync">
    <div class="row g-3 align-items-end">
        <div class="col-auto">
            <label class="form-label">שם משתמש</label>
            <InputText @bind-Value="_newUser.Username" required maxlength="200" class="form-control" aria-label="שם משתמש" />
        </div>
        <div class="col-auto">
            <label class="form-label">סיסמה</label>
            <InputText @bind-Value="_newUser.Password" type="password" required class="form-control" aria-label="סיסמה" />
        </div>
        <div class="col-auto form-check mb-2">
            <InputCheckbox @bind-Value="_newUser.IsAdmin" class="form-check-input" id="new-user-is-admin" />
            <label class="form-check-label" for="new-user-is-admin">מנהל/ת מערכת</label>
        </div>
        <div class="col-auto">
            <button type="submit" class="btn btn-primary">הוסף משתמש</button>
        </div>
    </div>
</EditForm>
@if (_createError is not null)
{
    <div class="alert alert-danger mt-3">@_createError</div>
}

@code {
    private List<PlatformUser> _users = new();
    private List<Application> _applications = new();
    private readonly NewUserModel _newUser = new();
    private string? _createError;

    private readonly HashSet<int> _expandedUserIds = new();
    private readonly Dictionary<int, HashSet<int>> _grantedAppIdsByUserId = new();

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    private async Task<int> CurrentPlatformUserIdAsync()
    {
        var authState = await AuthenticationStateTask!;
        return int.Parse(authState.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    }

    protected override async Task OnInitializedAsync()
    {
        _users = (await PlatformUserRepository.GetAllAsync()).ToList();
        _applications = (await ApplicationRepository.GetAllAsync()).ToList();
    }

    private async Task ToggleExpandAsync(int userId)
    {
        if (_expandedUserIds.Contains(userId))
        {
            _expandedUserIds.Remove(userId);
            return;
        }

        _expandedUserIds.Add(userId);

        if (!_grantedAppIdsByUserId.ContainsKey(userId))
        {
            var grantedIds = await AccessGrants.GetGrantedApplicationIdsAsync(userId);
            _grantedAppIdsByUserId[userId] = new HashSet<int>(grantedIds);
        }
    }

    private async Task ToggleGrantAsync(int userId, int applicationId, bool isGranted)
    {
        if (isGranted)
        {
            await AccessGrants.GrantAsync(userId, applicationId);
            _grantedAppIdsByUserId[userId].Add(applicationId);

            var currentPlatformUserId = await CurrentPlatformUserIdAsync();
            var application = _applications.First(a => a.Id == applicationId);
            await Audit.RecordAsync(currentPlatformUserId, "PlatformUserApplicationGrant", $"{userId}:{applicationId}", "Create", $"Granted platform user {userId} management access to application '{application.Name}' ({applicationId})");
        }
        else
        {
            await AccessGrants.RevokeAsync(userId, applicationId);
            _grantedAppIdsByUserId[userId].Remove(applicationId);

            var currentPlatformUserId = await CurrentPlatformUserIdAsync();
            var application = _applications.First(a => a.Id == applicationId);
            await Audit.RecordAsync(currentPlatformUserId, "PlatformUserApplicationGrant", $"{userId}:{applicationId}", "Deactivate", $"Revoked platform user {userId}'s management access to application '{application.Name}' ({applicationId})");
        }
    }

    private async Task CreateUserAsync()
    {
        _createError = null;
        try
        {
            var newUser = await PlatformUserRepository.AddAsync(new PlatformUser
            {
                Username = _newUser.Username,
                PasswordHash = PasswordHasher.Hash(_newUser.Password),
                IsAdmin = _newUser.IsAdmin,
                CreatedAt = DateTime.UtcNow
            });

            var currentPlatformUserId = await CurrentPlatformUserIdAsync();
            await Audit.RecordAsync(currentPlatformUserId, "PlatformUser", newUser.Id.ToString(), "Create", $"Created platform user '{newUser.Username}' (admin: {newUser.IsAdmin})");

            _newUser.Username = string.Empty;
            _newUser.Password = string.Empty;
            _newUser.IsAdmin = false;
            _users = (await PlatformUserRepository.GetAllAsync()).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"משתמש בשם '{_newUser.Username}' כבר קיים.";
        }
    }

    private async Task DeactivateAsync(int id)
    {
        await PlatformUserRepository.DeactivateAsync(id);

        var currentPlatformUserId = await CurrentPlatformUserIdAsync();
        await Audit.RecordAsync(currentPlatformUserId, "PlatformUser", id.ToString(), "Deactivate", $"Deactivated platform user {id}");

        _users = (await PlatformUserRepository.GetAllAsync()).ToList();
    }

    private class NewUserModel
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public bool IsAdmin { get; set; }
    }
}
```

- [ ] **Step 3: Build and run the full suite**

Run: `dotnet build` then `dotnet test`
Expected: 0 build errors, all tests (foundation + 4 controller groups + everything pre-existing) still green.

- [ ] **Step 4: Manual Playwright verification**

This project has no bUnit harness (see Global Constraints) — verify the grant flow end to end through the real running app, the same way the B1 UI-wiring gap was caught and re-verified. Start the app (`dotnet run --project src/LogsPlatform.Web`), then run a script equivalent to:

```javascript
// scratchpad script — not part of the repo
const { chromium } = require('C:\\playwright-tests\\node_modules\\@playwright\\test');

(async () => {
  const browser = await chromium.launch();
  const adminPage = await browser.newPage();

  // 1. Log in as the seeded Super Admin, create a test Application and a non-admin PlatformUser.
  await adminPage.goto('http://localhost:5201/login');
  await adminPage.fill('#username', 'admin');
  await adminPage.fill('#password', '<the seeded password from the console output>');
  await adminPage.click('button[type="submit"]');

  await adminPage.goto('http://localhost:5201/admin/applications');
  await adminPage.fill('input[aria-label="שם"]', 'RbacManualTestApp');
  await adminPage.click('button:has-text("צור")');

  await adminPage.goto('http://localhost:5201/admin/platform-users');
  // fill and submit the "add user" form with IsAdmin unchecked, e.g. username "rbac-manual-test-user"
  // expand that user's row and check the "RbacManualTestApp" checkbox to grant access

  // 2. In a second browser context, log in as the non-admin user and confirm:
  //    - "ניהול" now appears in the nav
  //    - the granted application's environment/module/etc. forms succeed
  //    - a DIFFERENT, non-granted application's forms show the Hebrew "אין לך הרשאת ניהול..." error

  await browser.close();
})();
```

Expected: the granted user can create/rename/deactivate entities under the granted application and is blocked (with the Hebrew error message, not a silent failure or exception) under a different, non-granted application; the "ניהול" nav link is visible for the granted user and would be absent for a non-admin user with zero grants.

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Web/Components/Layout/NavMenu.razor \
        src/LogsPlatform.Web/Components/Shared/PlatformUsersSection.razor
git commit -m "feat: add grant-management UI to PlatformUsersSection and gate NavMenu's ניהול link on grants"
```

---

## Final Verification

- [ ] Run `dotnet build` — 0 errors.
- [ ] Run `dotnet test` — full suite green, including all 4 new `ApplicationAccessGroup[A-D]Tests` files, `ApplicationAccessGrantRepositoryTests`, and `ApplicationAccessServiceTests`.
- [ ] Confirm via `grep -rl "@inject I.*Repository" src/LogsPlatform.Web/Components/` (the same check used to discover B1's UI-wiring gap) that every Shared/Pages component touched in Tasks 6–8 also now injects `ApplicationAccessService` — a component with a direct repository inject but no `ApplicationAccessService` inject is a missed mutation surface.
- [ ] Confirm `ApplicationsController`, `AuditLogController`, and the `PlatformUsersAdmin.razor` page's own attribute were NOT modified (`git diff main --stat` should not list them).
- [ ] Run the manual Playwright script from Task 8, Step 4, against the live app.
- [ ] Invoke `superpowers:finishing-a-development-branch`.
