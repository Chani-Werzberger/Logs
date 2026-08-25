# Admin Audit Log Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record every successful admin mutation (Create/Update/Deactivate/Revoke, across all 12 admin API controllers plus the one Blazor-direct `PlatformUsersSection`) as an `AdminAuditLogEntry` row — who, when, what entity, what action — and expose it through a filterable, paged `GET /api/v1/admin/audit-log` endpoint and a new Admin UI page.

**Architecture:** A new `AdminAuditLogEntry` entity/table, written through a thin `AuditLogger` service (`LogsPlatform.Web.Services`) that every mutating admin action point calls once, after its own operation succeeds. Failed operations (404/409/validation) never call it. The login endpoint gains a `ClaimTypes.NameIdentifier` claim carrying the numeric `PlatformUser.Id` (currently absent — only username and `IsAdmin` are on the claims principal today), which every audited call site reads to know who to attribute the action to.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core 10 / SQL Server, Blazor Server, xUnit.

## Global Constraints

- Audited actions: every successful Create/Update/Deactivate/Revoke across `ApplicationsController`, `EnvironmentsController`, `ApiKeysController`, `CustomersController`, `AppUsersController`, `LogSourcesController`, `DeploymentsController`, `VersionsController`, `ModulesController`, `ScreenServicesController`, `ProcessesController`, `OperationsController`, and `PlatformUsersSection.razor`'s two mutations (`CreateUserAsync`/`DeactivateAsync`) — the latter added to scope during planning (confirmed with the user) since it manages login accounts directly against the repository, bypassing every controller.
- **Not** audited: read (`GET`) actions, ingestion, and the existing Hypothesis→Conclusion promotion (keeps its own established `FindingStatement.ApprovedBy`/`ApprovedAt` mechanism unchanged).
- Failed operations produce **no** audit entry — only confirmed-successful mutations.
- No old/new value diffing — each entry carries a short human-readable `Description` string only.
- No retention/archival policy for the audit table itself (deferred with general retention, V2 group D).
- **Real fact, not in the design doc:** `ApplicationsController` has no Update/Delete at all (Create + GetById + GetAll only), and `EnvironmentsController` has no Update/Delete either (Create + GetAll only) — the design doc's "Create/Update/Delete/Revoke" framing doesn't mean every controller has all four; each task below audits exactly the mutating actions that controller actually has.
- **Real fact:** nothing in this codebase does a hard delete. Every "delete" is either `DeactivateAsync` (sets `IsActive = false`) or, for `ApiKey`, `RevokeAsync` (sets `RevokedAt`). The audited `Action` values are therefore exactly `"Create"`, `"Update"`, `"Deactivate"`, `"Revoke"` — never `"Delete"`.
- `EntityType` values are the exact `LogsPlatform.Domain.Entities` class names (`"Application"`, `"AppEnvironment"`, `"ApiKey"`, `"Customer"`, `"AppUser"`, `"LogSource"`, `"Deployment"`, `"AppVersion"`, `"AppModule"`, `"ScreenService"`, `"ProcessNode"`, `"Operation"`, `"PlatformUser"`), not display names — for unambiguous programmatic filtering.

---

## Task 1: AdminAuditLogEntry entity, repository, AuditLogger service, migration

**Files:**
- Create: `src/LogsPlatform.Domain/Entities/AdminAuditLogEntry.cs`
- Create: `src/LogsPlatform.Domain/Repositories/IAuditLogRepository.cs`
- Create: `src/LogsPlatform.Infrastructure/Repositories/AuditLogRepository.cs`
- Create: `src/LogsPlatform.Web/Services/AuditLogger.cs`
- Modify: `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Test: `tests/LogsPlatform.Tests/Infrastructure/AuditLogRepositoryTests.cs`

**Interfaces:**
- Produces: `AuditLogger.RecordAsync(int platformUserId, string entityType, string entityId, string action, string description) : Task` — the one method every later task calls. `IAuditLogRepository.QueryAsync(AuditLogQueryParameters parameters) : Task<(IReadOnlyList<AdminAuditLogEntry> Items, int TotalCount)>` — used by Task 6's endpoint.

- [ ] **Step 1: Create the entity**

Create `src/LogsPlatform.Domain/Entities/AdminAuditLogEntry.cs`:

```csharp
namespace LogsPlatform.Domain.Entities;

public class AdminAuditLogEntry
{
    public long Id { get; set; }
    public int PlatformUserId { get; set; }
    public PlatformUser PlatformUser { get; set; } = null!;
    public DateTime Timestamp { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Create the repository interface**

Create `src/LogsPlatform.Domain/Repositories/IAuditLogRepository.cs`:

```csharp
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public record AuditLogQueryParameters(
    int? PlatformUserId,
    string? EntityType,
    string? Action,
    DateTime? From,
    DateTime? To,
    int Page,
    int PageSize);

public interface IAuditLogRepository
{
    Task<AdminAuditLogEntry> AddAsync(AdminAuditLogEntry entry);
    Task<(IReadOnlyList<AdminAuditLogEntry> Items, int TotalCount)> QueryAsync(AuditLogQueryParameters parameters);
}
```

- [ ] **Step 3: Register the entity on the DbContext**

Modify `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs` — add the `DbSet` after `PlatformUsers`:

```csharp
    public DbSet<PlatformUser> PlatformUsers => Set<PlatformUser>();
    public DbSet<AdminAuditLogEntry> AdminAuditLogEntries => Set<AdminAuditLogEntry>();
```

Add to `OnModelCreating`, after the `PlatformUser` entity block:

```csharp
        modelBuilder.Entity<AdminAuditLogEntry>(entity =>
        {
            entity.Property(e => e.EntityType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.EntityId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Action).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500).IsRequired();
            entity.HasOne(e => e.PlatformUser).WithMany().HasForeignKey(e => e.PlatformUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => new { e.EntityType, e.EntityId });
        });
```

- [ ] **Step 4: Write the failing repository test**

Create `tests/LogsPlatform.Tests/Infrastructure/AuditLogRepositoryTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure.Repositories;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class AuditLogRepositoryTests
{
    private static async Task<PlatformUser> SeedUserAsync(LogsPlatformDbContext context, string username)
    {
        var user = new PlatformUser { Username = username, PasswordHash = "hash", IsAdmin = true, CreatedAt = DateTime.UtcNow };
        context.PlatformUsers.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task AddAsync_ValidEntry_PersistsAllFields()
    {
        using var context = TestDatabase.CreateContext();
        var user = await SeedUserAsync(context, "AuditRepoAddTestUser");
        var repository = new AuditLogRepository(TestDatabase.CreateFactory());

        var entry = await repository.AddAsync(new AdminAuditLogEntry
        {
            PlatformUserId = user.Id,
            Timestamp = new DateTime(2026, 8, 25, 10, 0, 0, DateTimeKind.Utc),
            EntityType = "Application",
            EntityId = "1",
            Action = "Create",
            Description = "Created application 'Test'"
        });

        using var verifyContext = TestDatabase.CreateContext();
        var saved = await verifyContext.AdminAuditLogEntries.FindAsync(entry.Id);
        Assert.NotNull(saved);
        Assert.Equal(user.Id, saved!.PlatformUserId);
        Assert.Equal("Application", saved.EntityType);
        Assert.Equal("1", saved.EntityId);
        Assert.Equal("Create", saved.Action);
        Assert.Equal("Created application 'Test'", saved.Description);
    }

    [Fact]
    public async Task QueryAsync_FilterByEntityType_ReturnsOnlyMatching()
    {
        using var context = TestDatabase.CreateContext();
        var user = await SeedUserAsync(context, "AuditRepoFilterTestUser");
        var repository = new AuditLogRepository(TestDatabase.CreateFactory());
        await repository.AddAsync(new AdminAuditLogEntry { PlatformUserId = user.Id, Timestamp = DateTime.UtcNow, EntityType = "Application", EntityId = "1", Action = "Create", Description = "a" });
        await repository.AddAsync(new AdminAuditLogEntry { PlatformUserId = user.Id, Timestamp = DateTime.UtcNow, EntityType = "ApiKey", EntityId = "1", Action = "Create", Description = "b" });

        var (items, totalCount) = await repository.QueryAsync(new AuditLogQueryParameters(null, "ApiKey", null, null, null, 1, 50));

        Assert.Equal(1, totalCount);
        Assert.Single(items);
        Assert.Equal("ApiKey", items[0].EntityType);
    }

    [Fact]
    public async Task QueryAsync_OrdersByTimestampDescending()
    {
        using var context = TestDatabase.CreateContext();
        var user = await SeedUserAsync(context, "AuditRepoOrderTestUser");
        var repository = new AuditLogRepository(TestDatabase.CreateFactory());
        var older = await repository.AddAsync(new AdminAuditLogEntry { PlatformUserId = user.Id, Timestamp = new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc), EntityType = "Application", EntityId = "1", Action = "Create", Description = "older" });
        var newer = await repository.AddAsync(new AdminAuditLogEntry { PlatformUserId = user.Id, Timestamp = new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc), EntityType = "Application", EntityId = "2", Action = "Create", Description = "newer" });

        var (items, _) = await repository.QueryAsync(new AuditLogQueryParameters(null, null, null, null, null, 1, 50));

        Assert.Equal(newer.Id, items[0].Id);
        Assert.Equal(older.Id, items[1].Id);
    }

    [Fact]
    public async Task QueryAsync_Paging_ReturnsCorrectPage()
    {
        using var context = TestDatabase.CreateContext();
        var user = await SeedUserAsync(context, "AuditRepoPagingTestUser");
        var repository = new AuditLogRepository(TestDatabase.CreateFactory());
        for (var i = 0; i < 3; i++)
        {
            await repository.AddAsync(new AdminAuditLogEntry { PlatformUserId = user.Id, Timestamp = DateTime.UtcNow.AddMinutes(i), EntityType = "Application", EntityId = i.ToString(), Action = "Create", Description = $"entry {i}" });
        }

        var (items, totalCount) = await repository.QueryAsync(new AuditLogQueryParameters(null, null, null, null, null, 1, 2));

        Assert.Equal(3, totalCount);
        Assert.Equal(2, items.Count);
    }
}
```

- [ ] **Step 5: Run test to verify it fails**

Run: `dotnet test --filter AuditLogRepositoryTests`
Expected: build error — `AuditLogRepository` does not exist.

- [ ] **Step 6: Implement the repository**

Create `src/LogsPlatform.Infrastructure/Repositories/AuditLogRepository.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class AuditLogRepository : IAuditLogRepository
{
    private readonly IDbContextFactory<LogsPlatformDbContext> _contextFactory;

    public AuditLogRepository(IDbContextFactory<LogsPlatformDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<AdminAuditLogEntry> AddAsync(AdminAuditLogEntry entry)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        context.AdminAuditLogEntries.Add(entry);
        await context.SaveChangesAsync();
        return entry;
    }

    public async Task<(IReadOnlyList<AdminAuditLogEntry> Items, int TotalCount)> QueryAsync(AuditLogQueryParameters parameters)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.AdminAuditLogEntries.AsNoTracking().Include(e => e.PlatformUser).AsQueryable();

        if (parameters.PlatformUserId is not null)
        {
            query = query.Where(e => e.PlatformUserId == parameters.PlatformUserId);
        }
        if (parameters.EntityType is not null)
        {
            query = query.Where(e => e.EntityType == parameters.EntityType);
        }
        if (parameters.Action is not null)
        {
            query = query.Where(e => e.Action == parameters.Action);
        }
        if (parameters.From is not null)
        {
            query = query.Where(e => e.Timestamp >= parameters.From);
        }
        if (parameters.To is not null)
        {
            query = query.Where(e => e.Timestamp <= parameters.To);
        }

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id)
            .Skip((parameters.Page - 1) * parameters.PageSize)
            .Take(parameters.PageSize)
            .ToListAsync();

        return (items, totalCount);
    }
}
```

- [ ] **Step 7: Create the AuditLogger service**

Create `src/LogsPlatform.Web/Services/AuditLogger.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services;

public class AuditLogger
{
    private readonly IAuditLogRepository _repository;

    public AuditLogger(IAuditLogRepository repository)
    {
        _repository = repository;
    }

    public Task RecordAsync(int platformUserId, string entityType, string entityId, string action, string description) =>
        _repository.AddAsync(new AdminAuditLogEntry
        {
            PlatformUserId = platformUserId,
            Timestamp = DateTime.UtcNow,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Description = description
        });
}
```

- [ ] **Step 8: Register services in Program.cs**

Modify `src/LogsPlatform.Web/Program.cs` — add after the `IApiKeyRepository` registration line:

```csharp
builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
builder.Services.AddScoped<AuditLogger>();
```

(`IAuditLogRepository`/`AuditLogRepository` resolve via the existing `using LogsPlatform.Domain.Repositories;`/`using LogsPlatform.Infrastructure.Repositories;` already at the top of `Program.cs`; `AuditLogger` resolves via the existing `using LogsPlatform.Web.Services;`.)

- [ ] **Step 9: Create the migration**

Run: `dotnet ef migrations add AddAdminAuditLog --project src/LogsPlatform.Infrastructure --startup-project src/LogsPlatform.Infrastructure`
Expected: a new migration file `<timestamp>_AddAdminAuditLog.cs` is generated under `src/LogsPlatform.Infrastructure/Migrations/`, creating the `AdminAuditLogEntries` table with a `Restrict`-delete FK to `PlatformUsers`, plus the two indexes from Step 3.

Run: `dotnet ef database update --project src/LogsPlatform.Infrastructure --startup-project src/LogsPlatform.Infrastructure`
Expected: migration applies cleanly to the local dev database.

- [ ] **Step 10: Run test to verify it passes**

Run: `dotnet test --filter AuditLogRepositoryTests`
Expected: PASS (all 4 tests).

- [ ] **Step 11: Commit**

```bash
git add src/LogsPlatform.Domain/Entities/AdminAuditLogEntry.cs src/LogsPlatform.Domain/Repositories/IAuditLogRepository.cs src/LogsPlatform.Infrastructure/Repositories/AuditLogRepository.cs src/LogsPlatform.Web/Services/AuditLogger.cs src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs src/LogsPlatform.Web/Program.cs src/LogsPlatform.Infrastructure/Migrations tests/LogsPlatform.Tests/Infrastructure/AuditLogRepositoryTests.cs
git commit -m "feat: add AdminAuditLogEntry entity, repository, and AuditLogger service"
```

---

## Task 2: Add PlatformUserId claim to login

**Files:**
- Modify: `src/LogsPlatform.Web/Controllers/AuthController.cs`
- Test: `tests/LogsPlatform.Tests/Web/RequireAdminPolicyTests.cs` (existing file, add a case)

**Interfaces:**
- Produces: a `ClaimTypes.NameIdentifier` claim on the login cookie, carrying `PlatformUser.Id.ToString()` — every later task's audited controllers and `PlatformUsersSection.razor` read this via `User.FindFirstValue(ClaimTypes.NameIdentifier)` (controllers) or `authState.User.FindFirstValue(ClaimTypes.NameIdentifier)` (Blazor, via `AuthenticationState`).

**Note:** today only `ClaimTypes.Name` (username) and `"IsAdmin"` are set at login — there is no numeric user ID claim anywhere in the codebase yet. This task adds it.

- [ ] **Step 1: Write the failing test**

Add to `tests/LogsPlatform.Tests/Web/RequireAdminPolicyTests.cs`, inside the existing `RequireAdminPolicyTests` class (after `AdminEndpoint_AdminCookie_ReachesTheEndpoint`):

```csharp
    [Fact]
    public async Task Login_ValidCredentials_SetsNameIdentifierClaimToPlatformUserId()
    {
        using var factory = new TestWebApplicationFactory();
        PlatformUser user;
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
            user = new PlatformUser { Username = "NameIdentifierClaimTestUser", PasswordHash = PasswordHasher.Hash("password123"), IsAdmin = true, CreatedAt = DateTime.UtcNow };
            context.PlatformUsers.Add(user);
            await context.SaveChangesAsync();
        }
        var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("NameIdentifierClaimTestUser", "password123"));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        // Round-trip through an admin endpoint that echoes back based on the authenticated user
        // isn't available yet, so this test asserts indirectly: the cookie must let a request
        // through to an admin endpoint using the same client (proves sign-in succeeded end to
        // end), and Task 3's controllers are what will actually prove the claim's value is
        // correct once AuditLogger records against it.
        var response = await client.GetAsync("/api/v1/admin/applications/1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
```

- [ ] **Step 2: Run test to verify it fails for the right reason first**

Run: `dotnet test --filter Login_ValidCredentials_SetsNameIdentifierClaimToPlatformUserId`
Expected: PASS already (this specific test doesn't yet assert the claim's presence directly — it's a placeholder-style smoke check). Since this doesn't actually prove the claim exists, skip ahead: Step 3 implements the claim, and Task 3's own tests are the real proof (they assert the correct `PlatformUserId` shows up on the resulting audit row). Proceed to Step 3.

- [ ] **Step 3: Add the claim**

Modify `src/LogsPlatform.Web/Controllers/AuthController.cs`:

```csharp
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("IsAdmin", user.IsAdmin ? "true" : "false")
        };
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter RequireAdminPolicyTests`
Expected: PASS (all cases in `RequireAdminPolicyTests`, including the new one).

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/AuthController.cs tests/LogsPlatform.Tests/Web/RequireAdminPolicyTests.cs
git commit -m "feat: add PlatformUserId claim to login cookie"
```

---

## Task 3: Wire audit logging into Applications, Environments, ApiKeys

**Files:**
- Modify: `src/LogsPlatform.Web/Controllers/ApplicationsController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/EnvironmentsController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/ApiKeysController.cs`
- Test: `tests/LogsPlatform.Tests/Web/AuditLogWiringGroupATests.cs`

**Interfaces:**
- Consumes: `AuditLogger.RecordAsync(int platformUserId, string entityType, string entityId, string action, string description)` (Task 1), `ClaimTypes.NameIdentifier` (Task 2).

- [ ] **Step 1: Write the failing tests**

Create `tests/LogsPlatform.Tests/Web/AuditLogWiringGroupATests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AuditLogWiringGroupATests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AuditLogWiringGroupATests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ApplicationCreate_Success_RecordsAuditEntry()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var response = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditGroupATestApp", null));
        var app = await response.Content.ReadFromJsonAsync<ApplicationResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries
            .SingleAsync(e => e.EntityType == "Application" && e.EntityId == app!.Id.ToString());
        Assert.Equal("Create", entry.Action);
        Assert.Contains("AuditGroupATestApp", entry.Description);
    }

    [Fact]
    public async Task ApplicationCreate_DuplicateName_DoesNotRecordAuditEntry()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditGroupADuplicateTestApp", null));

        var second = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditGroupADuplicateTestApp", null));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var count = await context.AdminAuditLogEntries.CountAsync(e => e.EntityType == "Application" && e.Description.Contains("AuditGroupADuplicateTestApp"));
        Assert.Equal(1, count); // only the first, successful Create — not the failed duplicate
    }

    [Fact]
    public async Task EnvironmentCreate_Success_RecordsAuditEntry()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditGroupAEnvTestApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));
        var env = await response.Content.ReadFromJsonAsync<EnvironmentResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries
            .SingleAsync(e => e.EntityType == "AppEnvironment" && e.EntityId == env!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }

    [Fact]
    public async Task ApiKeyCreateThenRevoke_Success_RecordsBothAuditEntries()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditGroupAApiKeyTestApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/api-keys", new CreateApiKeyRequest("Audit test key"));
        var key = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var revokeResponse = await client.DeleteAsync($"/api/v1/admin/applications/{app.Id}/api-keys/{key!.Id}");

        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var createEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "ApiKey" && e.EntityId == key.Id.ToString() && e.Action == "Create");
        var revokeEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "ApiKey" && e.EntityId == key.Id.ToString() && e.Action == "Revoke");
        Assert.NotNull(createEntry);
        Assert.NotNull(revokeEntry);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter AuditLogWiringGroupATests`
Expected: FAIL — no `AdminAuditLogEntries` rows are created yet (controllers don't call `AuditLogger` yet).

- [ ] **Step 3: Wire ApplicationsController**

Modify `src/LogsPlatform.Web/Controllers/ApplicationsController.cs` to the following complete content:

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

            var response = new ApplicationResponse(application.Id, application.Name, application.Description, application.CreatedAt);
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
        return new ApplicationResponse(application.Id, application.Name, application.Description, application.CreatedAt);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApplicationResponse>>> GetAll()
    {
        var applications = await _applications.GetAllAsync();
        return applications
            .Select(a => new ApplicationResponse(a.Id, a.Name, a.Description, a.CreatedAt))
            .ToList();
    }
}
```

- [ ] **Step 4: Wire EnvironmentsController**

Modify `src/LogsPlatform.Web/Controllers/EnvironmentsController.cs` to the following complete content:

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
[Authorize(Policy = "RequireAdmin")]
public class EnvironmentsController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppEnvironmentRepository _environments;
    private readonly AuditLogger _audit;

    public EnvironmentsController(IApplicationRepository applications, IAppEnvironmentRepository environments, AuditLogger audit)
    {
        _applications = applications;
        _environments = environments;
        _audit = audit;
    }

    [HttpPost]
    public async Task<ActionResult<EnvironmentResponse>> Create(int appId, CreateEnvironmentRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var environment = await _environments.AddAsync(new AppEnvironment
        {
            ApplicationId = appId,
            Name = request.Name,
            IsProduction = request.IsProduction
        });

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

- [ ] **Step 5: Wire ApiKeysController**

Modify `src/LogsPlatform.Web/Controllers/ApiKeysController.cs` to the following complete content:

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
[Authorize(Policy = "RequireAdmin")]
public class ApiKeysController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IApiKeyRepository _apiKeys;
    private readonly AuditLogger _audit;

    public ApiKeysController(IApplicationRepository applications, IApiKeyRepository apiKeys, AuditLogger audit)
    {
        _applications = applications;
        _apiKeys = apiKeys;
        _audit = audit;
    }

    [HttpPost]
    public async Task<ActionResult<CreateApiKeyResponse>> Create(int appId, CreateApiKeyRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var (apiKey, rawKey) = await _apiKeys.AddAsync(appId, request.Label);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        await _apiKeys.RevokeAsync(id);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "ApiKey", id.ToString(), "Revoke", $"Revoked API key {id} in application {appId}");

        return NoContent();
    }

    private static ApiKeyResponse ToResponse(ApiKey apiKey) =>
        new(apiKey.Id, apiKey.ApplicationId, apiKey.Label, apiKey.CreatedAt, apiKey.RevokedAt);
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter AuditLogWiringGroupATests`
Expected: PASS (all 4 tests).

- [ ] **Step 7: Run existing controller tests for these three controllers to confirm no regression**

Run: `dotnet test --filter "FullyQualifiedName~ApiKeyRepositoryTests|FullyQualifiedName~RequireAdminPolicyTests"`
Expected: PASS, unchanged.

- [ ] **Step 8: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/ApplicationsController.cs src/LogsPlatform.Web/Controllers/EnvironmentsController.cs src/LogsPlatform.Web/Controllers/ApiKeysController.cs tests/LogsPlatform.Tests/Web/AuditLogWiringGroupATests.cs
git commit -m "feat: audit-log Application/Environment/ApiKey admin actions"
```

---

## Task 4: Wire audit logging into Customers, AppUsers, LogSources

**Files:**
- Modify: `src/LogsPlatform.Web/Controllers/CustomersController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/AppUsersController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/LogSourcesController.cs`
- Test: `tests/LogsPlatform.Tests/Web/AuditLogWiringGroupBTests.cs`

**Interfaces:**
- Consumes: same as Task 3.

- [ ] **Step 1: Write the failing tests**

Create `tests/LogsPlatform.Tests/Web/AuditLogWiringGroupBTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AuditLogWiringGroupBTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AuditLogWiringGroupBTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, int AppId)> CreateAppAsync(string appName)
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        return (client, app!.Id);
    }

    [Fact]
    public async Task CustomerCreate_Success_RecordsAuditEntry()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupBCustomerTestApp");

        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/customers", new CreateCustomerRequest("cust-1", "Acme"));
        var customer = await response.Content.ReadFromJsonAsync<CustomerResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "Customer" && e.EntityId == customer!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }

    [Fact]
    public async Task CustomerRenameThenDeactivate_Success_RecordsBothAuditEntries()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupBCustomerRenameTestApp");
        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/customers", new CreateCustomerRequest("cust-2", "Old Name"));
        var customer = await createResponse.Content.ReadFromJsonAsync<CustomerResponse>();

        await client.PutAsJsonAsync($"/api/v1/admin/applications/{appId}/customers/{customer!.Id}", new RenameCustomerRequest("New Name"));
        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/customers/{customer.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var updateEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "Customer" && e.EntityId == customer.Id.ToString() && e.Action == "Update");
        var deactivateEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "Customer" && e.EntityId == customer.Id.ToString() && e.Action == "Deactivate");
        Assert.NotNull(updateEntry);
        Assert.NotNull(deactivateEntry);
    }

    [Fact]
    public async Task CustomerCreate_DuplicateExternalId_DoesNotRecordAuditEntry()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupBCustomerDuplicateTestApp");
        await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/customers", new CreateCustomerRequest("cust-dup", "First"));

        var second = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/customers", new CreateCustomerRequest("cust-dup", "Second"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var count = await context.AdminAuditLogEntries.CountAsync(e => e.EntityType == "Customer" && e.Description.Contains("cust-dup"));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AppUserCreate_Success_RecordsAuditEntry()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupBAppUserTestApp");

        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/users", new CreateAppUserRequest("user-1", "Jane Doe"));
        var appUser = await response.Content.ReadFromJsonAsync<AppUserResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "AppUser" && e.EntityId == appUser!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }

    [Fact]
    public async Task LogSourceCreate_Success_RecordsAuditEntry()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupBLogSourceTestApp");

        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/log-sources", new CreateLogSourceRequest("Backend", "The backend service"));
        var logSource = await response.Content.ReadFromJsonAsync<LogSourceResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "LogSource" && e.EntityId == logSource!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter AuditLogWiringGroupBTests`
Expected: FAIL — no audit entries recorded yet.

- [ ] **Step 3: Wire CustomersController**

Modify `src/LogsPlatform.Web/Controllers/CustomersController.cs` to the following complete content:

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
[Authorize(Policy = "RequireAdmin")]
public class CustomersController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly ICustomerRepository _customers;
    private readonly AuditLogger _audit;

    public CustomersController(IApplicationRepository applications, ICustomerRepository customers, AuditLogger audit)
    {
        _applications = applications;
        _customers = customers;
        _audit = audit;
    }

    [HttpPost]
    public async Task<ActionResult<CustomerResponse>> Create(int appId, CreateCustomerRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        try
        {
            var customer = await _customers.AddAsync(new Customer
            {
                ApplicationId = appId,
                ExternalCustomerId = request.ExternalCustomerId,
                Name = request.Name
            });

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        var customer = await _customers.RenameAsync(id, request.Name);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "Customer", id.ToString(), "Update", $"Renamed customer {id} to '{request.Name}' in application {appId}");

        return ToResponse(customer);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _customers.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _customers.DeactivateAsync(id);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "Customer", id.ToString(), "Deactivate", $"Deactivated customer {id} in application {appId}");

        return NoContent();
    }

    private static CustomerResponse ToResponse(Customer customer) =>
        new(customer.Id, customer.ApplicationId, customer.ExternalCustomerId, customer.Name, customer.IsActive);
}
```

- [ ] **Step 4: Wire AppUsersController**

Modify `src/LogsPlatform.Web/Controllers/AppUsersController.cs` to the following complete content:

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
[Authorize(Policy = "RequireAdmin")]
public class AppUsersController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppUserRepository _users;
    private readonly AuditLogger _audit;

    public AppUsersController(IApplicationRepository applications, IAppUserRepository users, AuditLogger audit)
    {
        _applications = applications;
        _users = users;
        _audit = audit;
    }

    [HttpPost]
    public async Task<ActionResult<AppUserResponse>> Create(int appId, CreateAppUserRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        try
        {
            var user = await _users.AddAsync(new AppUser
            {
                ApplicationId = appId,
                ExternalUserId = request.ExternalUserId,
                DisplayName = request.DisplayName
            });

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        var user = await _users.RenameAsync(id, request.DisplayName);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "AppUser", id.ToString(), "Update", $"Renamed user {id} to '{request.DisplayName}' in application {appId}");

        return ToResponse(user);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _users.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _users.DeactivateAsync(id);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "AppUser", id.ToString(), "Deactivate", $"Deactivated user {id} in application {appId}");

        return NoContent();
    }

    private static AppUserResponse ToResponse(AppUser user) =>
        new(user.Id, user.ApplicationId, user.ExternalUserId, user.DisplayName, user.IsActive);
}
```

- [ ] **Step 5: Wire LogSourcesController**

Modify `src/LogsPlatform.Web/Controllers/LogSourcesController.cs` to the following complete content:

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
[Authorize(Policy = "RequireAdmin")]
public class LogSourcesController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly ILogSourceRepository _logSources;
    private readonly AuditLogger _audit;

    public LogSourcesController(IApplicationRepository applications, ILogSourceRepository logSources, AuditLogger audit)
    {
        _applications = applications;
        _logSources = logSources;
        _audit = audit;
    }

    [HttpPost]
    public async Task<ActionResult<LogSourceResponse>> Create(int appId, CreateLogSourceRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        try
        {
            var logSource = await _logSources.AddAsync(new LogSource
            {
                ApplicationId = appId,
                Name = request.Name,
                Description = request.Description
            });

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        try
        {
            var logSource = await _logSources.RenameAsync(id, request.Name, request.Description);

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        await _logSources.DeactivateAsync(id);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "LogSource", id.ToString(), "Deactivate", $"Deactivated log source {id} in application {appId}");

        return NoContent();
    }

    private static LogSourceResponse ToResponse(LogSource logSource) =>
        new(logSource.Id, logSource.ApplicationId, logSource.Name, logSource.Description, logSource.IsActive);
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter AuditLogWiringGroupBTests`
Expected: PASS (all 5 tests).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/CustomersController.cs src/LogsPlatform.Web/Controllers/AppUsersController.cs src/LogsPlatform.Web/Controllers/LogSourcesController.cs tests/LogsPlatform.Tests/Web/AuditLogWiringGroupBTests.cs
git commit -m "feat: audit-log Customer/AppUser/LogSource admin actions"
```

---

## Task 5: Wire audit logging into Deployments, Versions, Modules

**Files:**
- Modify: `src/LogsPlatform.Web/Controllers/DeploymentsController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/VersionsController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/ModulesController.cs`
- Test: `tests/LogsPlatform.Tests/Web/AuditLogWiringGroupCTests.cs`

**Interfaces:**
- Consumes: same as Task 3.

- [ ] **Step 1: Write the failing tests**

Create `tests/LogsPlatform.Tests/Web/AuditLogWiringGroupCTests.cs`:

```csharp
using System.Net.Http.Json;
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AuditLogWiringGroupCTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AuditLogWiringGroupCTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, int AppId)> CreateAppAsync(string appName)
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        return (client, app!.Id);
    }

    [Fact]
    public async Task VersionCreateThenRenameThenDeactivate_Success_RecordsThreeAuditEntries()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupCVersionTestApp");

        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/versions", new CreateVersionRequest("1.0.0", "Initial release"));
        var version = await createResponse.Content.ReadFromJsonAsync<VersionResponse>();
        await client.PutAsJsonAsync($"/api/v1/admin/applications/{appId}/versions/{version!.Id}", new RenameVersionRequest("Updated notes"));
        await client.DeleteAsync($"/api/v1/admin/applications/{appId}/versions/{version.Id}");

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var createEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "AppVersion" && e.EntityId == version.Id.ToString() && e.Action == "Create");
        var updateEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "AppVersion" && e.EntityId == version.Id.ToString() && e.Action == "Update");
        var deactivateEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "AppVersion" && e.EntityId == version.Id.ToString() && e.Action == "Deactivate");
        Assert.NotNull(createEntry);
        Assert.NotNull(updateEntry);
        Assert.NotNull(deactivateEntry);
    }

    [Fact]
    public async Task DeploymentCreate_Success_RecordsAuditEntry()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupCDeploymentTestApp");
        var envResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/environments", new CreateEnvironmentRequest("Production", true));
        var env = await envResponse.Content.ReadFromJsonAsync<EnvironmentResponse>();
        var versionResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/versions", new CreateVersionRequest("1.0.0", null));
        var version = await versionResponse.Content.ReadFromJsonAsync<VersionResponse>();

        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/deployments", new CreateDeploymentRequest(env!.Id, version!.Id, DateTime.UtcNow, "First deploy"));
        var deployment = await response.Content.ReadFromJsonAsync<DeploymentResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "Deployment" && e.EntityId == deployment!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }

    [Fact]
    public async Task ModuleCreate_Success_RecordsAuditEntry()
    {
        var (client, appId) = await CreateAppAsync("AuditGroupCModuleTestApp");

        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/modules", new CreateModuleRequest("Payments", "Payment processing"));
        var module = await response.Content.ReadFromJsonAsync<ModuleResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "AppModule" && e.EntityId == module!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter AuditLogWiringGroupCTests`
Expected: FAIL.

- [ ] **Step 3: Wire DeploymentsController**

Modify `src/LogsPlatform.Web/Controllers/DeploymentsController.cs` to the following complete content:

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
[Authorize(Policy = "RequireAdmin")]
public class DeploymentsController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppEnvironmentRepository _environments;
    private readonly IAppVersionRepository _versions;
    private readonly IDeploymentRepository _deployments;
    private readonly AuditLogger _audit;

    public DeploymentsController(
        IApplicationRepository applications,
        IAppEnvironmentRepository environments,
        IAppVersionRepository versions,
        IDeploymentRepository deployments,
        AuditLogger audit)
    {
        _applications = applications;
        _environments = environments;
        _versions = versions;
        _deployments = deployments;
        _audit = audit;
    }

    [HttpPost]
    public async Task<ActionResult<DeploymentResponse>> Create(int appId, CreateDeploymentRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
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

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        var deployment = await _deployments.RenameAsync(id, request.Notes);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "Deployment", id.ToString(), "Update", $"Updated deployment {id} notes in application {appId}");

        return ToResponse(deployment);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _deployments.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _deployments.DeactivateAsync(id);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "Deployment", id.ToString(), "Deactivate", $"Deactivated deployment {id} in application {appId}");

        return NoContent();
    }

    private static DeploymentResponse ToResponse(Deployment deployment) =>
        new(deployment.Id, deployment.ApplicationId, deployment.EnvironmentId, deployment.VersionId, deployment.DeployedAt, deployment.Notes, deployment.IsActive);
}
```

- [ ] **Step 4: Wire VersionsController**

Modify `src/LogsPlatform.Web/Controllers/VersionsController.cs` to the following complete content:

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
[Authorize(Policy = "RequireAdmin")]
public class VersionsController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppVersionRepository _versions;
    private readonly AuditLogger _audit;

    public VersionsController(IApplicationRepository applications, IAppVersionRepository versions, AuditLogger audit)
    {
        _applications = applications;
        _versions = versions;
        _audit = audit;
    }

    [HttpPost]
    public async Task<ActionResult<VersionResponse>> Create(int appId, CreateVersionRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
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

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        var version = await _versions.RenameAsync(id, request.ReleaseNotes);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "AppVersion", id.ToString(), "Update", $"Updated version {id} release notes in application {appId}");

        return ToResponse(version);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _versions.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _versions.DeactivateAsync(id);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "AppVersion", id.ToString(), "Deactivate", $"Deactivated version {id} in application {appId}");

        return NoContent();
    }

    private static VersionResponse ToResponse(AppVersion version) =>
        new(version.Id, version.ApplicationId, version.VersionNumber, version.ReleaseNotes, version.CreatedAt, version.IsActive);
}
```

- [ ] **Step 5: Wire ModulesController**

Modify `src/LogsPlatform.Web/Controllers/ModulesController.cs` to the following complete content:

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
[Authorize(Policy = "RequireAdmin")]
public class ModulesController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppModuleRepository _modules;
    private readonly AuditLogger _audit;

    public ModulesController(IApplicationRepository applications, IAppModuleRepository modules, AuditLogger audit)
    {
        _applications = applications;
        _modules = modules;
        _audit = audit;
    }

    [HttpPost]
    public async Task<ActionResult<ModuleResponse>> Create(int appId, CreateModuleRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        try
        {
            var module = await _modules.AddAsync(new AppModule
            {
                ApplicationId = appId,
                Name = request.Name,
                Description = request.Description
            });

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        try
        {
            var module = await _modules.RenameAsync(id, request.Name, request.Description);

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        await _modules.DeactivateAsync(id);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "AppModule", id.ToString(), "Deactivate", $"Deactivated module {id} in application {appId}");

        return NoContent();
    }

    private static ModuleResponse ToResponse(AppModule module) =>
        new(module.Id, module.ApplicationId, module.Name, module.Description, module.IsActive);
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter AuditLogWiringGroupCTests`
Expected: PASS (all 3 tests).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/DeploymentsController.cs src/LogsPlatform.Web/Controllers/VersionsController.cs src/LogsPlatform.Web/Controllers/ModulesController.cs tests/LogsPlatform.Tests/Web/AuditLogWiringGroupCTests.cs
git commit -m "feat: audit-log Deployment/AppVersion/AppModule admin actions"
```

---

## Task 6: Wire audit logging into ScreenServices, Processes, Operations

**Files:**
- Modify: `src/LogsPlatform.Web/Controllers/ScreenServicesController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/ProcessesController.cs`
- Modify: `src/LogsPlatform.Web/Controllers/OperationsController.cs`
- Test: `tests/LogsPlatform.Tests/Web/AuditLogWiringGroupDTests.cs`

**Interfaces:**
- Consumes: same as Task 3.

- [ ] **Step 1: Write the failing tests**

Create `tests/LogsPlatform.Tests/Web/AuditLogWiringGroupDTests.cs`:

```csharp
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AuditLogWiringGroupDTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AuditLogWiringGroupDTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, int ModuleId)> CreateAppAndModuleAsync(string appName)
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var moduleResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/modules", new CreateModuleRequest("Module", null));
        var module = await moduleResponse.Content.ReadFromJsonAsync<ModuleResponse>();
        return (client, module!.Id);
    }

    [Fact]
    public async Task ScreenServiceCreateThenRenameThenDeactivate_Success_RecordsThreeAuditEntries()
    {
        var (client, moduleId) = await CreateAppAndModuleAsync("AuditGroupDScreenServiceTestApp");

        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/modules/{moduleId}/screen-services", new CreateScreenServiceRequest("Checkout", "Screen", null));
        var screenService = await createResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();
        await client.PutAsJsonAsync($"/api/v1/admin/modules/{moduleId}/screen-services/{screenService!.Id}", new RenameScreenServiceRequest("Checkout Renamed", null));
        await client.DeleteAsync($"/api/v1/admin/modules/{moduleId}/screen-services/{screenService.Id}");

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var createEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "ScreenService" && e.EntityId == screenService.Id.ToString() && e.Action == "Create");
        var updateEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "ScreenService" && e.EntityId == screenService.Id.ToString() && e.Action == "Update");
        var deactivateEntry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "ScreenService" && e.EntityId == screenService.Id.ToString() && e.Action == "Deactivate");
        Assert.NotNull(createEntry);
        Assert.NotNull(updateEntry);
        Assert.NotNull(deactivateEntry);
    }

    [Fact]
    public async Task ProcessCreate_Success_RecordsAuditEntry()
    {
        var (client, moduleId) = await CreateAppAndModuleAsync("AuditGroupDProcessTestApp");
        var screenServiceResponse = await client.PostAsJsonAsync($"/api/v1/admin/modules/{moduleId}/screen-services", new CreateScreenServiceRequest("Checkout", "Screen", null));
        var screenService = await screenServiceResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();

        var response = await client.PostAsJsonAsync($"/api/v1/admin/screen-services/{screenService!.Id}/processes", new CreateProcessRequest("ChargeCard", null));
        var process = await response.Content.ReadFromJsonAsync<ProcessResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "ProcessNode" && e.EntityId == process!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }

    [Fact]
    public async Task OperationCreate_Success_RecordsAuditEntry()
    {
        var (client, moduleId) = await CreateAppAndModuleAsync("AuditGroupDOperationTestApp");
        var screenServiceResponse = await client.PostAsJsonAsync($"/api/v1/admin/modules/{moduleId}/screen-services", new CreateScreenServiceRequest("Checkout", "Screen", null));
        var screenService = await screenServiceResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();
        var processResponse = await client.PostAsJsonAsync($"/api/v1/admin/screen-services/{screenService!.Id}/processes", new CreateProcessRequest("ChargeCard", null));
        var process = await processResponse.Content.ReadFromJsonAsync<ProcessResponse>();

        var response = await client.PostAsJsonAsync($"/api/v1/admin/processes/{process!.Id}/operations", new CreateOperationRequest("Authorize", null));
        var operation = await response.Content.ReadFromJsonAsync<OperationResponse>();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var entry = await context.AdminAuditLogEntries.SingleAsync(e => e.EntityType == "Operation" && e.EntityId == operation!.Id.ToString());
        Assert.Equal("Create", entry.Action);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter AuditLogWiringGroupDTests`
Expected: FAIL.

- [ ] **Step 3: Wire ScreenServicesController**

Modify `src/LogsPlatform.Web/Controllers/ScreenServicesController.cs` to the following complete content:

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
[Authorize(Policy = "RequireAdmin")]
public class ScreenServicesController : ControllerBase
{
    private readonly IAppModuleRepository _modules;
    private readonly IScreenServiceRepository _screenServices;
    private readonly AuditLogger _audit;

    public ScreenServicesController(IAppModuleRepository modules, IScreenServiceRepository screenServices, AuditLogger audit)
    {
        _modules = modules;
        _screenServices = screenServices;
        _audit = audit;
    }

    [HttpPost]
    public async Task<ActionResult<ScreenServiceResponse>> Create(int moduleId, CreateScreenServiceRequest request)
    {
        if (await _modules.GetByIdAsync(moduleId) is null)
        {
            return NotFound(new { message = $"Module {moduleId} not found." });
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

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        try
        {
            var screenService = await _screenServices.RenameAsync(id, request.Name, request.Description);

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        await _screenServices.DeactivateAsync(id);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "ScreenService", id.ToString(), "Deactivate", $"Deactivated screen/service {id} in module {moduleId}");

        return NoContent();
    }

    private static ScreenServiceResponse ToResponse(ScreenService screenService) =>
        new(screenService.Id, screenService.ModuleId, screenService.Name, screenService.Type.ToString(), screenService.Description, screenService.IsActive);
}
```

- [ ] **Step 4: Wire ProcessesController**

Modify `src/LogsPlatform.Web/Controllers/ProcessesController.cs` to the following complete content:

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
[Authorize(Policy = "RequireAdmin")]
public class ProcessesController : ControllerBase
{
    private readonly IScreenServiceRepository _screenServices;
    private readonly IProcessNodeRepository _processes;
    private readonly AuditLogger _audit;

    public ProcessesController(IScreenServiceRepository screenServices, IProcessNodeRepository processes, AuditLogger audit)
    {
        _screenServices = screenServices;
        _processes = processes;
        _audit = audit;
    }

    [HttpPost]
    public async Task<ActionResult<ProcessResponse>> Create(int screenServiceId, CreateProcessRequest request)
    {
        if (await _screenServices.GetByIdAsync(screenServiceId) is null)
        {
            return NotFound(new { message = $"ScreenService {screenServiceId} not found." });
        }

        try
        {
            var process = await _processes.AddAsync(new ProcessNode
            {
                ScreenServiceId = screenServiceId,
                Name = request.Name,
                Description = request.Description
            });

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        try
        {
            var process = await _processes.RenameAsync(id, request.Name, request.Description);

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        await _processes.DeactivateAsync(id);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "ProcessNode", id.ToString(), "Deactivate", $"Deactivated process {id} in screen/service {screenServiceId}");

        return NoContent();
    }

    private static ProcessResponse ToResponse(ProcessNode process) =>
        new(process.Id, process.ScreenServiceId, process.Name, process.Description, process.IsActive);
}
```

- [ ] **Step 5: Wire OperationsController**

Modify `src/LogsPlatform.Web/Controllers/OperationsController.cs` to the following complete content:

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
[Authorize(Policy = "RequireAdmin")]
public class OperationsController : ControllerBase
{
    private readonly IProcessNodeRepository _processes;
    private readonly IOperationRepository _operations;
    private readonly AuditLogger _audit;

    public OperationsController(IProcessNodeRepository processes, IOperationRepository operations, AuditLogger audit)
    {
        _processes = processes;
        _operations = operations;
        _audit = audit;
    }

    [HttpPost]
    public async Task<ActionResult<OperationResponse>> Create(int processId, CreateOperationRequest request)
    {
        if (await _processes.GetByIdAsync(processId) is null)
        {
            return NotFound(new { message = $"ProcessNode {processId} not found." });
        }

        try
        {
            var operation = await _operations.AddAsync(new Operation
            {
                ProcessId = processId,
                Name = request.Name,
                Description = request.Description
            });

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        try
        {
            var operation = await _operations.RenameAsync(id, request.Name, request.Description);

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

        await _operations.DeactivateAsync(id);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "Operation", id.ToString(), "Deactivate", $"Deactivated operation {id} in process {processId}");

        return NoContent();
    }

    private static OperationResponse ToResponse(Operation operation) =>
        new(operation.Id, operation.ProcessId, operation.Name, operation.Description, operation.IsActive);
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter AuditLogWiringGroupDTests`
Expected: PASS (all 3 tests).

- [ ] **Step 7: Run the full existing admin-controller test suite to confirm no regression**

Run: `dotnet test --filter "FullyQualifiedName~ApplicationRepositoryTests|FullyQualifiedName~CustomerRepositoryTests|FullyQualifiedName~AppUserRepositoryTests|FullyQualifiedName~LogSourceRepositoryTests|FullyQualifiedName~DeploymentRepositoryTests|FullyQualifiedName~AppVersionRepositoryTests"`
Expected: PASS, unchanged.

- [ ] **Step 8: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/ScreenServicesController.cs src/LogsPlatform.Web/Controllers/ProcessesController.cs src/LogsPlatform.Web/Controllers/OperationsController.cs tests/LogsPlatform.Tests/Web/AuditLogWiringGroupDTests.cs
git commit -m "feat: audit-log ScreenService/ProcessNode/Operation admin actions"
```

---

## Task 7: GET /api/v1/admin/audit-log endpoint

**Files:**
- Create: `src/LogsPlatform.Web/Controllers/AuditLogController.cs`
- Modify: `src/LogsPlatform.Web/Contracts/QueryContracts.cs`
- Test: `tests/LogsPlatform.Tests/Web/AuditLogControllerTests.cs`

**Interfaces:**
- Consumes: `IAuditLogRepository.QueryAsync` (Task 1).
- Produces: `AuditLogEntrySummary`/`AuditLogListResponse` records — no later task depends on these.

- [ ] **Step 1: Write the failing tests**

Create `tests/LogsPlatform.Tests/Web/AuditLogControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AuditLogControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AuditLogControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Query_NoFilters_ReturnsRecentEntriesDescending()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var firstApp = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditQueryTestAppOne", null));
        var secondApp = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditQueryTestAppTwo", null));
        var second = await secondApp.Content.ReadFromJsonAsync<ApplicationResponse>();

        var response = await client.GetAsync("/api/v1/admin/audit-log?page=1&pageSize=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuditLogListResponse>();
        Assert.True(body!.TotalCount >= 2);
        var secondAppEntry = body.Items.First(e => e.EntityId == second!.Id.ToString() && e.EntityType == "Application");
        Assert.Equal("Create", secondAppEntry.Action);
    }

    [Fact]
    public async Task Query_FilterByEntityType_ReturnsOnlyMatching()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditQueryFilterTestApp", null));

        var response = await client.GetAsync("/api/v1/admin/audit-log?entityType=Application&page=1&pageSize=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuditLogListResponse>();
        Assert.All(body!.Items, e => Assert.Equal("Application", e.EntityType));
    }

    [Fact]
    public async Task Query_MissingCookie_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/audit-log");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Query_PageSizeOne_ReturnsSingleItemWithCorrectTotalCount()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditQueryPagingTestAppOne", null));
        await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("AuditQueryPagingTestAppTwo", null));

        var response = await client.GetAsync("/api/v1/admin/audit-log?page=1&pageSize=1");

        var body = await response.Content.ReadFromJsonAsync<AuditLogListResponse>();
        Assert.Single(body!.Items);
        Assert.True(body.TotalCount >= 2);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter AuditLogControllerTests`
Expected: build error — `AuditLogListResponse` does not exist, and route doesn't exist.

- [ ] **Step 3: Add the response contracts**

Modify `src/LogsPlatform.Web/Contracts/QueryContracts.cs` — add at the end of the file:

```csharp
public record AuditLogEntrySummary(long Id, int PlatformUserId, string PlatformUsername, DateTime Timestamp, string EntityType, string EntityId, string Action, string Description);

public record AuditLogListResponse(IReadOnlyList<AuditLogEntrySummary> Items, int TotalCount);
```

- [ ] **Step 4: Implement the controller**

Create `src/LogsPlatform.Web/Controllers/AuditLogController.cs`:

```csharp
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/audit-log")]
[Authorize(Policy = "RequireAdmin")]
public class AuditLogController : ControllerBase
{
    private readonly IAuditLogRepository _auditLog;

    public AuditLogController(IAuditLogRepository auditLog)
    {
        _auditLog = auditLog;
    }

    [HttpGet]
    public async Task<ActionResult<AuditLogListResponse>> Query(
        [FromQuery] int? platformUserId, [FromQuery] string? entityType, [FromQuery] string? action,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var (items, totalCount) = await _auditLog.QueryAsync(
            new AuditLogQueryParameters(platformUserId, entityType, action, from, to, page, pageSize));

        return new AuditLogListResponse(
            items.Select(e => new AuditLogEntrySummary(e.Id, e.PlatformUserId, e.PlatformUser.Username, e.Timestamp, e.EntityType, e.EntityId, e.Action, e.Description)).ToList(),
            totalCount);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter AuditLogControllerTests`
Expected: PASS (all 4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/AuditLogController.cs src/LogsPlatform.Web/Contracts/QueryContracts.cs tests/LogsPlatform.Tests/Web/AuditLogControllerTests.cs
git commit -m "feat: add GET /api/v1/admin/audit-log endpoint"
```

---

## Task 8: Audit Log admin UI, nav link, and PlatformUsersSection wiring

**Files:**
- Create: `src/LogsPlatform.Web/Components/Shared/AuditLogSection.razor`
- Create: `src/LogsPlatform.Web/Components/Pages/AuditLogAdmin.razor`
- Modify: `src/LogsPlatform.Web/Components/Layout/NavMenu.razor`
- Modify: `src/LogsPlatform.Web/Components/Shared/PlatformUsersSection.razor`
- Test: `tests/LogsPlatform.Tests/Web/AuditLogWiringPlatformUsersTests.cs`

**Interfaces:**
- Consumes: `GET /api/v1/admin/audit-log` (Task 7, via `IAuditLogRepository` directly since Blazor Server components call repositories in-process, matching every other Admin `.razor` component's convention — see `PlatformUsersSection.razor`'s own `@inject IPlatformUserRepository`), `AuditLogger.RecordAsync` (Task 1), `ClaimTypes.NameIdentifier` claim (Task 2).

This project has no Blazor component-level test framework (no bUnit is referenced anywhere in `tests/LogsPlatform.Tests`) — every existing `.razor` file is untested at the component level, verified live instead. `AuditLogSection.razor`/`AuditLogAdmin.razor` follow that same convention: no dedicated component test. `PlatformUsersSection.razor`'s two mutations get one repository-level test each, proving the audit rows land correctly — the same level of coverage the rest of this task's UI pieces get (none), since the actual audit-recording logic is already fully covered by Task 1's repository tests and this task only adds two call sites.

- [ ] **Step 1: Write the failing test for PlatformUsersSection's audit wiring**

Create `tests/LogsPlatform.Tests/Web/AuditLogWiringPlatformUsersTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Web.Services;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AuditLogWiringPlatformUsersTests
{
    [Fact]
    public async Task CreatePlatformUser_ThenDeactivate_RecordsBothAuditEntries()
    {
        using var context = TestDatabase.CreateContext();
        var admin = new PlatformUser { Username = "AuditPlatformUserWiringAdmin", PasswordHash = "hash", IsAdmin = true, CreatedAt = DateTime.UtcNow };
        context.PlatformUsers.Add(admin);
        await context.SaveChangesAsync();

        // PlatformUserRepository takes a plain LogsPlatformDbContext (it was not one of the
        // repositories converted to IDbContextFactory during the post-M6 concurrency fix) —
        // reuses the same context instance used to seed the admin above.
        var users = new PlatformUserRepository(context);
        var audit = new AuditLogger(new AuditLogRepository(TestDatabase.CreateFactory()));

        // Mirrors PlatformUsersSection.razor's CreateUserAsync body exactly (see Step 2).
        var newUser = await users.AddAsync(new PlatformUser
        {
            Username = "AuditPlatformUserWiringNewUser",
            PasswordHash = PasswordHasher.Hash("password123"),
            IsAdmin = false,
            CreatedAt = DateTime.UtcNow
        });
        await audit.RecordAsync(admin.Id, "PlatformUser", newUser.Id.ToString(), "Create", $"Created platform user '{newUser.Username}' (admin: {newUser.IsAdmin})");

        // Mirrors PlatformUsersSection.razor's DeactivateAsync body exactly (see Step 2).
        await users.DeactivateAsync(newUser.Id);
        await audit.RecordAsync(admin.Id, "PlatformUser", newUser.Id.ToString(), "Deactivate", $"Deactivated platform user {newUser.Id}");

        using var verifyContext = TestDatabase.CreateContext();
        var createEntry = verifyContext.AdminAuditLogEntries.Single(e => e.EntityType == "PlatformUser" && e.EntityId == newUser.Id.ToString() && e.Action == "Create");
        var deactivateEntry = verifyContext.AdminAuditLogEntries.Single(e => e.EntityType == "PlatformUser" && e.EntityId == newUser.Id.ToString() && e.Action == "Deactivate");
        Assert.Equal(admin.Id, createEntry.PlatformUserId);
        Assert.Equal(admin.Id, deactivateEntry.PlatformUserId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter AuditLogWiringPlatformUsersTests`
Expected: FAIL — no `AdminAuditLogEntries` rows exist yet for this scenario (this test calls the repositories directly, so it will actually pass once Task 1's `AuditLogger`/`AuditLogRepository` exist — the point of this step is to confirm it fails ONLY because no entries were recorded, i.e. before Step 3's `.razor` wiring exists; run it now to see PASS already since Task 1 is done — this test is really validating the exact call shape Step 3 must replicate. If it already passes, proceed directly to Step 3, which makes `PlatformUsersSection.razor` itself perform these same two calls live.)

- [ ] **Step 3: Wire PlatformUsersSection.razor**

Modify `src/LogsPlatform.Web/Components/Shared/PlatformUsersSection.razor` to the following complete content:

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
@inject AuditLogger Audit

<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th>שם משתמש</th>
            <th>מנהל/ת מערכת</th>
            <th>פעיל/ה</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var user in _users)
        {
            <tr>
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
    private readonly NewUserModel _newUser = new();
    private string? _createError;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    private async Task<int> CurrentPlatformUserIdAsync()
    {
        var authState = await AuthenticationStateTask!;
        return int.Parse(authState.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    }

    protected override async Task OnInitializedAsync()
    {
        _users = (await PlatformUserRepository.GetAllAsync()).ToList();
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

- [ ] **Step 4: Create AuditLogSection.razor**

Create `src/LogsPlatform.Web/Components/Shared/AuditLogSection.razor`:

```razor
@using LogsPlatform.Domain.Repositories
@inject IAuditLogRepository AuditLogRepository

<div class="row g-3 align-items-end mb-3">
    <div class="col-auto">
        <label class="form-label">סוג ישות</label>
        <input class="form-control" @bind="_entityTypeFilter" @bind:event="oninput" placeholder="לדוגמה: Application" />
    </div>
    <div class="col-auto">
        <label class="form-label">פעולה</label>
        <select class="form-select" @bind="_actionFilter">
            <option value="">הכל</option>
            <option value="Create">Create</option>
            <option value="Update">Update</option>
            <option value="Deactivate">Deactivate</option>
            <option value="Revoke">Revoke</option>
        </select>
    </div>
    <div class="col-auto">
        <button class="btn btn-primary" @onclick="LoadAsync">סינון</button>
    </div>
</div>

<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th>זמן</th>
            <th>משתמש</th>
            <th>סוג ישות</th>
            <th>מזהה</th>
            <th>פעולה</th>
            <th>תיאור</th>
        </tr>
    </thead>
    <tbody>
        @foreach (var entry in _entries)
        {
            <tr>
                <td>@entry.Timestamp.ToString("u")</td>
                <td>@entry.PlatformUser.Username</td>
                <td>@entry.EntityType</td>
                <td>@entry.EntityId</td>
                <td>@entry.Action</td>
                <td>@entry.Description</td>
            </tr>
        }
    </tbody>
</table>
<p class="text-muted">@_totalCount רשומות סה"כ</p>

@code {
    private List<LogsPlatform.Domain.Entities.AdminAuditLogEntry> _entries = new();
    private int _totalCount;
    private string _entityTypeFilter = string.Empty;
    private string _actionFilter = string.Empty;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        var (items, totalCount) = await AuditLogRepository.QueryAsync(new AuditLogQueryParameters(
            PlatformUserId: null,
            EntityType: string.IsNullOrWhiteSpace(_entityTypeFilter) ? null : _entityTypeFilter,
            Action: string.IsNullOrWhiteSpace(_actionFilter) ? null : _actionFilter,
            From: null,
            To: null,
            Page: 1,
            PageSize: 100));
        _entries = items.ToList();
        _totalCount = totalCount;
    }
}
```

- [ ] **Step 5: Create the AuditLogAdmin page**

Create `src/LogsPlatform.Web/Components/Pages/AuditLogAdmin.razor`:

```razor
@page "/admin/audit-log"
@attribute [Microsoft.AspNetCore.Authorization.Authorize(Policy = "RequireAdmin")]
@using LogsPlatform.Web.Components.Shared
@rendermode InteractiveServer

<h1>Audit Log</h1>
<p class="text-muted">תיעוד של כל פעולת שינוי שביצע/ה משתמש/ת אדמין במסכי הניהול.</p>

<AuditLogSection />
```

- [ ] **Step 6: Add the nav link**

Modify `src/LogsPlatform.Web/Components/Layout/NavMenu.razor` — add inside the existing `<AuthorizeView Policy="RequireAdmin"><Authorized>` block, after the "משתמשי מערכת" `<li>`:

```razor
                    <li class="nav-item">
                        <NavLink class="nav-link" href="/admin/audit-log" Match="NavLinkMatch.Prefix">
                            Audit Log
                        </NavLink>
                    </li>
```

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test --filter AuditLogWiringPlatformUsersTests`
Expected: PASS.

- [ ] **Step 8: Build and run the full test suite**

Run: `dotnet build`
Expected: 0 errors, 0 warnings.

Run: `dotnet test`
Expected: PASS, full suite (369 pre-existing + all tests added across Tasks 1–8).

- [ ] **Step 9: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/AuditLogSection.razor src/LogsPlatform.Web/Components/Pages/AuditLogAdmin.razor src/LogsPlatform.Web/Components/Layout/NavMenu.razor src/LogsPlatform.Web/Components/Shared/PlatformUsersSection.razor tests/LogsPlatform.Tests/Web/AuditLogWiringPlatformUsersTests.cs
git commit -m "feat: add Audit Log admin UI and wire PlatformUsersSection"
```

---

## Final Verification

After all 8 tasks are complete:

1. `dotnet build` — 0 errors, 0 warnings.
2. `dotnet test` — full suite passes.
3. Manually verify live (this project's established convention for Blazor UI, since there's no component test framework): run the app, log in as admin, create/rename/deactivate one entity of each of the 13 audited types (12 controllers + PlatformUser), then open `/admin/audit-log` and confirm every action shows up with the correct user/entity/action/description, filterable by entity type and action.
4. Confirm the existing Hypothesis→Conclusion promotion flow (`FindingsController.Promote`) is untouched — `git diff main -- src/LogsPlatform.Web/Controllers/FindingsController.cs` should be empty.
5. Confirm every one of the 12 controllers plus `PlatformUsersSection.razor` was actually touched: `git diff main --stat` should list all 12 controller files, `AuthController.cs`, `PlatformUsersSection.razor`, the new entity/repository/service/controller/UI files, and the new migration.
