# Group B3: AppVersion + Deployment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Admin management (Create/List/Rename/Deactivate) for `AppVersion` and `Deployment` — a linked pair of release-record entities, matching the shape specified in `docs/superpowers/specs/2026-08-19-group-b3-appversion-deployment-design.md`. Backend + UI in one plan, same posture as B1/B2. This is the final plan in Group B — once merged, M1's own acceptance criterion ("fully define RetailPulse+FieldOps via the API/UI") is met.

**Architecture:** Same Modular Monolith layering as every prior Group A/B plan (`LogsPlatform.Domain` entities/interfaces → `LogsPlatform.Infrastructure` EF Core/repositories → `LogsPlatform.Web` controllers), plus two new Blazor child components extending `ApplicationsAdmin.razor`'s existing per-row expansion.

**Tech Stack:** Same as the current solution — .NET 10, EF Core 10.0.11, SQL Server, xUnit + `Microsoft.AspNetCore.Mvc.Testing`, Blazor Server. No new packages.

## Global Constraints

- **`AppVersion.VersionNumber` is immutable after creation** — Create sets it, Rename never touches it (only `ReleaseNotes` changes). Its uniqueness index is `(ApplicationId, VersionNumber)`. Consequently: `AppVersionRepository.RenameAsync` still wraps `SaveChangesAsync` in `try`/`catch`/detach (defensive symmetry, not because a conflict is reachable there), but **`VersionsController.Rename` and the UI's Rename handler do NOT wrap the call in an `IsUniqueViolation()` catch** — there is no reachable `409` on Rename. `Create` (and the UI's create handler) DOES need the `IsUniqueViolation()` catch, since `VersionNumber` is user-supplied and its uniqueness is reachable there. This is the same asymmetry `Customer`/`AppUser` already have in B1 — not a new pattern, a repeat of an established one.
- **`Deployment.EnvironmentId`/`VersionId`/`DeployedAt` are ALL immutable after creation** — Create sets them, Rename never touches them (only `Notes` changes). **`Deployment` has NO uniqueness constraint at all** — redeploying the same version to the same environment is legitimate (hotfix redeploy, rollback-then-redeploy), not a duplicate-input error. Consequently: **no `IsUniqueViolation()`/`DbUpdateException` catch appears anywhere in `Deployment`'s stack — not in `DeploymentRepository`, not in `DeploymentsController` (neither `Create` nor `Rename`), not in `DeploymentsSection.razor`.** This mirrors B2's `ApiKey` exactly (the first entity with this shape) — `Deployment` is the second.
- **Detach-on-failure in every `AddAsync`, `RenameAsync`, and `DeactivateAsync` for BOTH entities** — `try`/`catch`, `_context.Entry(entity).State = EntityState.Detached`, re-throw. Present from this plan's first draft for all three methods on both repositories, including `DeactivateAsync` — correctly inheriting the lesson B1's plan initially missed (and had to retrofit after its own final review) and B2 got right from day one. Do not repeat B1's gap a third time.
- **IDOR protection**: `GetById`/`Rename`/`Deactivate` controller actions on both `VersionsController` and `DeploymentsController` verify the loaded entity's `ApplicationId` equals the `appId` in the route, returning `404` if it doesn't.
- **Parent-existence guard on every `Create`**: verify `appId` exists via `IApplicationRepository.GetByIdAsync` before inserting, returning `404` if it doesn't — same as every prior entity.
- **`DeploymentsController.Create` has TWO EXTRA guards beyond the standard parent-existence one** — this is new territory none of the prior plans' entities needed, since none of them referenced two sibling foreign keys at once: after confirming `appId` exists, verify the request's `environmentId` resolves to an `AppEnvironment` whose own `ApplicationId` equals `appId` (`404` if it doesn't exist at all, or belongs to a different application — a `500`/exception here is a mistake, not an acceptable fallback), then the same check for `versionId` against `AppVersion`. Both checks reuse `IAppEnvironmentRepository.GetByIdAsync`/`IAppVersionRepository.GetByIdAsync` — no new repository methods needed for this.
- **Soft-delete only, `IsActive` on both entities** — `05-מודל-נתונים.md`'s schema originally omitted `IsActive` from these two tables; this was a confirmed spec gap (see the design doc) flagged back when B1 was designed and deferred to this plan. `DeactivateAsync` always sets `IsActive = false`, never hard-deletes (the `Event` table doesn't exist until M2, same reasoning as every prior entity).
- **EF Core foreign-key delete behavior on `Deployment` — read this carefully before writing `OnModelCreating`:** `Deployment.ApplicationId → Application` uses `DeleteBehavior.Cascade`, matching every prior entity. `Deployment.EnvironmentId → AppEnvironment` and `Deployment.VersionId → AppVersion` **must both use `DeleteBehavior.Restrict`, NOT `Cascade`.** Reason: `AppEnvironment` and `AppVersion` are themselves cascade-children of `Application` (via their own `ApplicationId` FK). If `Deployment`'s `EnvironmentId`/`VersionId` FKs were ALSO set to `Cascade`, SQL Server would have two different cascade paths into the `Deployments` table when an `Application` row is deleted (`Application → Deployment` directly, and `Application → AppEnvironment → Deployment`) — SQL Server explicitly forbids this ("may cause cycles or multiple cascade paths") and `dotnet ef database update` / the migration's `Up()` would fail outright. Using `Restrict` on these two FKs avoids the conflict. In current practice this is defensive rather than load-bearing (nothing in this codebase ever hard-deletes an `AppEnvironment` or `AppVersion` row — only `Application` cascades ever fire), but it is required for the migration to apply at all, and it's also the semantically correct choice: a `Deployment` record is a historical fact that should survive even if a referenced dimension were ever hard-deleted.
- **`Deployment` gets a non-unique index on `(EnvironmentId, VersionId, DeployedAt)`** — not used by anything in this plan, but matches exactly the query shape `05-מודל-נתונים.md` describes the future Analysis Engine will need for Deployment→Error-Spike correlation (M4). Cheap to add now, matching the same forward-looking-index precedent `ApiKey.KeyHash` set in B2.
- **`maxlength="200"` on `VersionNumber`'s `InputText`** (bound to a `HasMaxLength(200)` property) — matches every prior identity field. `ReleaseNotes` and `Deployment.Notes` have **no** `HasMaxLength` on their EF property and **no** `maxlength` attribute on their `InputText` — both are free-text fields with no length cap (`nvarchar(max)`), matching `LogSource.Description`'s existing precedent.
- **Reuse `DbUpdateExceptionExtensions.IsUniqueViolation()`** (`src/LogsPlatform.Web/DbUpdateExceptionExtensions.cs`, already merged) wherever a duplicate-conflict case is actually reachable (`AppVersion.Create` only — see above).
- **UI: two new child Razor components**, `src/LogsPlatform.Web/Components/Shared/VersionsSection.razor` and `DeploymentsSection.razor`, each self-contained (`[Parameter] public int ApplicationId`, its own repository injection(s), its own local state) — no per-application dictionary state needed, same reasoning as every prior Group B section. `DeploymentsSection` additionally injects `IAppEnvironmentRepository` and `IAppVersionRepository` (alongside its own `IDeploymentRepository`) purely to populate its create form's two `<select>` dropdowns — it does not write through either of those two repositories. Added to `ApplicationsAdmin.razor` directly after the existing `<ApiKeysSection ApplicationId="application.Id" />` line, in the order `<VersionsSection>` then `<DeploymentsSection>` (Versions before Deployments, matching the plan's own task order and the natural "you need a version to exist before you can deploy it" reading order — though note in Global Constraints below that neither section refreshes the other's data live).
- **No live cross-section refresh**: if an admin creates a new `AppVersion` and then immediately opens `DeploymentsSection`'s create form in the same expanded row, the new version will not appear in the dropdown until the row collapses and re-expands. This is not a bug to fix in this plan — it matches the existing precedent that no sibling sections in this project coordinate with each other (verified true for all of B1's and B2's sections).
- **Manual smoke-check steps use code-inspection, not curl.** Both new sections live inside `ApplicationsAdmin.razor`'s `_expandedAppIds.Contains(...)` conditional, `false` on every cold page load — a `curl` request can never find their content in the response, regardless of whether the components are correctly wired. This plan's UI tasks copy the already-corrected guidance from B1's fixed plan doc (`docs/superpowers/plans/2026-08-19-group-b1-customer-appuser-logsource.md`, Tasks 10-12) rather than reintroducing the mistake B1 originally made.
- Target framework `net10.0`, EF Core packages pinned at `10.0.11` everywhere (already the case — this plan adds no new package references).

---

### Task 1: Domain entities (`AppVersion`, `Deployment`) + repository interfaces

**Files:**
- Create: `src/LogsPlatform.Domain/Entities/AppVersion.cs`
- Create: `src/LogsPlatform.Domain/Entities/Deployment.cs`
- Modify: `src/LogsPlatform.Domain/Entities/Application.cs` (add `Versions`, `Deployments` navigation collections)
- Create: `src/LogsPlatform.Domain/Repositories/IAppVersionRepository.cs`
- Create: `src/LogsPlatform.Domain/Repositories/IDeploymentRepository.cs`

**Interfaces:**
- Consumes: `Application` entity (existing), `AppEnvironment` entity (existing, for `Deployment.Environment`).
- Produces: `AppVersion`, `Deployment` entity classes and `IAppVersionRepository`, `IDeploymentRepository` interfaces that Tasks 3-4 implement against.

- [ ] **Step 1: Write the two entities**

```csharp
// src/LogsPlatform.Domain/Entities/AppVersion.cs
namespace LogsPlatform.Domain.Entities;

public class AppVersion
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public string VersionNumber { get; set; } = string.Empty;
    public string? ReleaseNotes { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; } = true;
}
```

```csharp
// src/LogsPlatform.Domain/Entities/Deployment.cs
namespace LogsPlatform.Domain.Entities;

public class Deployment
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public int EnvironmentId { get; set; }
    public AppEnvironment Environment { get; set; } = null!;
    public int VersionId { get; set; }
    public AppVersion Version { get; set; } = null!;
    public DateTime DeployedAt { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}
```

Note `Deployment.Environment` (type `AppEnvironment`) and `Deployment.Version` (type `AppVersion`) — navigation properties use the short, natural word even though the entity type itself carries the BCL-collision-avoidance prefix, exactly matching the existing precedent `ScreenService.Module` (type `AppModule`).

- [ ] **Step 2: Add the navigation collections to `Application`**

```csharp
// src/LogsPlatform.Domain/Entities/Application.cs — full file content
namespace LogsPlatform.Domain.Entities;

public class Application
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<AppEnvironment> Environments { get; set; } = new List<AppEnvironment>();
    public ICollection<AppModule> Modules { get; set; } = new List<AppModule>();
    public ICollection<Customer> Customers { get; set; } = new List<Customer>();
    public ICollection<AppUser> Users { get; set; } = new List<AppUser>();
    public ICollection<LogSource> LogSources { get; set; } = new List<LogSource>();
    public ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();
    public ICollection<AppVersion> Versions { get; set; } = new List<AppVersion>();
    public ICollection<Deployment> Deployments { get; set; } = new List<Deployment>();
}
```

- [ ] **Step 3: Write the repository interfaces**

```csharp
// src/LogsPlatform.Domain/Repositories/IAppVersionRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IAppVersionRepository
{
    Task<AppVersion?> GetByIdAsync(int id);
    Task<IReadOnlyList<AppVersion>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<AppVersion> AddAsync(AppVersion version);
    Task<AppVersion> RenameAsync(int id, string? releaseNotes);
    Task DeactivateAsync(int id);
}
```

```csharp
// src/LogsPlatform.Domain/Repositories/IDeploymentRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IDeploymentRepository
{
    Task<Deployment?> GetByIdAsync(int id);
    Task<IReadOnlyList<Deployment>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<Deployment> AddAsync(Deployment deployment);
    Task<Deployment> RenameAsync(int id, string? notes);
    Task DeactivateAsync(int id);
}
```

Both `AddAsync(TEntity)` signatures take a constructed entity — back to the standard Group A/B1 shape (unlike B2's `ApiKey.AddAsync(int, string)` tuple-returning shape, which was specific to needing to generate and return a transient raw secret; neither `AppVersion` nor `Deployment` has anything like that). `RenameAsync`'s single parameter is nullable (`string?`) on both interfaces, matching the underlying nullable `ReleaseNotes`/`Notes` properties — an admin must be able to clear release notes or deployment notes, not just set them.

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Domain/Entities/AppVersion.cs src/LogsPlatform.Domain/Entities/Deployment.cs src/LogsPlatform.Domain/Entities/Application.cs src/LogsPlatform.Domain/Repositories/IAppVersionRepository.cs src/LogsPlatform.Domain/Repositories/IDeploymentRepository.cs
git commit -m "Add AppVersion, Deployment domain entities + repository interfaces"
```

---

### Task 2: `LogsPlatformDbContext` mapping + migration

**Files:**
- Modify: `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs`
- Modify: `tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs`

**Interfaces:**
- Consumes: `AppVersion`, `Deployment` from Task 1.
- Produces: `DbSet<AppVersion> Versions`, `DbSet<Deployment> Deployments` on `LogsPlatformDbContext`, plus the migration that creates both tables — used by Tasks 3-4's repositories.

- [ ] **Step 1: Write the failing test**

```csharp
// Add to tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs
[Fact]
public async Task CanInsertAndRetrieveAppVersionAndDeployment()
{
    using var context = TestDatabase.CreateContext();

    var application = new Application { Name = "B3DbContextTestApp", CreatedAt = DateTime.UtcNow };
    var environment = new AppEnvironment { Name = "Production", IsProduction = true };
    application.Environments.Add(environment);
    var version = new AppVersion { VersionNumber = "1.0.0", ReleaseNotes = "Initial release", CreatedAt = DateTime.UtcNow };
    application.Versions.Add(version);

    context.Applications.Add(application);
    await context.SaveChangesAsync();

    application.Deployments.Add(new Deployment
    {
        ApplicationId = application.Id,
        EnvironmentId = environment.Id,
        VersionId = version.Id,
        DeployedAt = DateTime.UtcNow,
        Notes = "First deploy"
    });
    await context.SaveChangesAsync();

    using var readContext = new LogsPlatformDbContext(
        new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    var loadedApp = await readContext.Applications
        .Include(a => a.Versions)
        .Include(a => a.Deployments)
        .FirstAsync(a => a.Name == "B3DbContextTestApp");

    Assert.Single(loadedApp.Versions);
    Assert.Equal("1.0.0", loadedApp.Versions.First().VersionNumber);
    Assert.True(loadedApp.Versions.First().IsActive);
    Assert.Single(loadedApp.Deployments);
    Assert.Equal("First deploy", loadedApp.Deployments.First().Notes);
    Assert.True(loadedApp.Deployments.First().IsActive);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter CanInsertAndRetrieveAppVersionAndDeployment`
Expected: FAIL — build error, `context.Versions`/`DbSet<AppVersion>` etc. do not exist yet.

- [ ] **Step 3: Add the `DbSet`s and `OnModelCreating` configuration**

Modify `LogsPlatformDbContext.cs` — add two `DbSet` properties and extend `OnModelCreating` (do not remove any existing configuration block — only add to them):

```csharp
// src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs — full file content
using LogsPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure;

public class LogsPlatformDbContext : DbContext
{
    public LogsPlatformDbContext(DbContextOptions<LogsPlatformDbContext> options) : base(options)
    {
    }

    public DbSet<Application> Applications => Set<Application>();
    public DbSet<AppEnvironment> AppEnvironments => Set<AppEnvironment>();
    public DbSet<AppModule> Modules => Set<AppModule>();
    public DbSet<ScreenService> ScreenServices => Set<ScreenService>();
    public DbSet<ProcessNode> Processes => Set<ProcessNode>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<LogSource> LogSources => Set<LogSource>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<AppVersion> Versions => Set<AppVersion>();
    public DbSet<Deployment> Deployments => Set<Deployment>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Application>(entity =>
        {
            entity.Property(a => a.Name).HasMaxLength(200).IsRequired();
            entity.HasIndex(a => a.Name).IsUnique();
        });

        modelBuilder.Entity<AppEnvironment>(entity =>
        {
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.HasOne(e => e.Application)
                .WithMany(a => a.Environments)
                .HasForeignKey(e => e.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.ApplicationId, e.Name }).IsUnique();
        });

        modelBuilder.Entity<AppModule>(entity =>
        {
            entity.Property(m => m.Name).HasMaxLength(200).IsRequired();
            entity.HasOne(m => m.Application)
                .WithMany(a => a.Modules)
                .HasForeignKey(m => m.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(m => new { m.ApplicationId, m.Name }).IsUnique();
        });

        modelBuilder.Entity<ScreenService>(entity =>
        {
            entity.Property(s => s.Name).HasMaxLength(200).IsRequired();
            entity.HasOne(s => s.Module)
                .WithMany(m => m.ScreenServices)
                .HasForeignKey(s => s.ModuleId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(s => new { s.ModuleId, s.Name }).IsUnique();
        });

        modelBuilder.Entity<ProcessNode>(entity =>
        {
            entity.Property(p => p.Name).HasMaxLength(200).IsRequired();
            entity.HasOne(p => p.ScreenService)
                .WithMany(s => s.Processes)
                .HasForeignKey(p => p.ScreenServiceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(p => new { p.ScreenServiceId, p.Name }).IsUnique();
        });

        modelBuilder.Entity<Operation>(entity =>
        {
            entity.Property(o => o.Name).HasMaxLength(200).IsRequired();
            entity.HasOne(o => o.Process)
                .WithMany(p => p.Operations)
                .HasForeignKey(o => o.ProcessId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(o => new { o.ProcessId, o.Name }).IsUnique();
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.Property(c => c.ExternalCustomerId).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();
            entity.HasOne(c => c.Application)
                .WithMany(a => a.Customers)
                .HasForeignKey(c => c.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(c => new { c.ApplicationId, c.ExternalCustomerId }).IsUnique();
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.Property(u => u.ExternalUserId).HasMaxLength(200).IsRequired();
            entity.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            entity.HasOne(u => u.Application)
                .WithMany(a => a.Users)
                .HasForeignKey(u => u.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(u => new { u.ApplicationId, u.ExternalUserId }).IsUnique();
        });

        modelBuilder.Entity<LogSource>(entity =>
        {
            entity.Property(l => l.Name).HasMaxLength(200).IsRequired();
            entity.HasOne(l => l.Application)
                .WithMany(a => a.LogSources)
                .HasForeignKey(l => l.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => new { l.ApplicationId, l.Name }).IsUnique();
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.Property(k => k.KeyHash).HasMaxLength(64).IsRequired();
            entity.Property(k => k.Label).HasMaxLength(200).IsRequired();
            entity.HasOne(k => k.Application)
                .WithMany(a => a.ApiKeys)
                .HasForeignKey(k => k.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(k => k.KeyHash);
        });

        modelBuilder.Entity<AppVersion>(entity =>
        {
            entity.Property(v => v.VersionNumber).HasMaxLength(200).IsRequired();
            entity.HasOne(v => v.Application)
                .WithMany(a => a.Versions)
                .HasForeignKey(v => v.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(v => new { v.ApplicationId, v.VersionNumber }).IsUnique();
        });

        modelBuilder.Entity<Deployment>(entity =>
        {
            entity.HasOne(d => d.Application)
                .WithMany(a => a.Deployments)
                .HasForeignKey(d => d.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(d => d.Environment)
                .WithMany()
                .HasForeignKey(d => d.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(d => d.Version)
                .WithMany()
                .HasForeignKey(d => d.VersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(d => new { d.EnvironmentId, d.VersionId, d.DeployedAt });
        });
    }
}
```

Note `Deployment`'s two `.WithMany()` calls (for `Environment` and `Version`) take no argument — neither `AppEnvironment` nor `AppVersion` has (or needs) an inverse `ICollection<Deployment>` navigation collection back to `Deployment`. Note also the `DeleteBehavior.Restrict` on both — see Global Constraints for why `Cascade` here would make the migration fail outright with a SQL Server "multiple cascade paths" error.

- [ ] **Step 4: Generate the migration**

This is an **additive** migration — no existing migration's history may be touched or regenerated:

```bash
dotnet ef migrations add AddAppVersionAndDeployment \
  --project src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj \
  --startup-project src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj
```

Expected: creates `src/LogsPlatform.Infrastructure/Migrations/<timestamp>_AddAppVersionAndDeployment.cs` and updates `LogsPlatformDbContextModelSnapshot.cs` — creating two new tables (`Versions`, `Deployments`) with the FK/cascade/index shape from Step 3. Verify the generated migration's `Up()` only adds the two new tables and their indexes/FKs — it must not contain any `DropTable`/`DropColumn` against any existing table (`Applications`, `AppEnvironments`, `Modules`, `ScreenServices`, `Processes`, `Operations`, `Customers`, `Users`, `LogSources`, `ApiKeys`) or their existing indexes. If it does, something in Step 3 changed the existing model unintentionally — STOP and investigate before proceeding. If migration generation itself fails with an error mentioning "cycles or multiple cascade paths," re-check that both of `Deployment`'s `Environment`/`Version` foreign keys use `DeleteBehavior.Restrict`, not `Cascade` — this is the exact failure Global Constraints warned about.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter CanInsertAndRetrieveAppVersionAndDeployment`
Expected: PASS.

- [ ] **Step 6: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 140, Skipped: 0, Total: 140` (the 139 tests that exist on `main` before this task, plus this task's new one).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs src/LogsPlatform.Infrastructure/Migrations/ tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs
git commit -m "Add AppVersion, Deployment EF Core mapping + migration"
```

---

### Task 3: `AppVersionRepository` implementation + tests

**Files:**
- Create: `src/LogsPlatform.Infrastructure/Repositories/AppVersionRepository.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/AppVersionRepositoryTests.cs`

**Interfaces:**
- Consumes: `IAppVersionRepository` (Task 1), `LogsPlatformDbContext` (Task 2).
- Produces: `AppVersionRepository` — registered in DI by Task 5, consumed by Task 6's controller and Task 8's UI component.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/AppVersionRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class AppVersionRepositoryTests
{
    private static async Task<int> CreateTestApplicationAsync(LogsPlatformDbContext context, string name)
    {
        var application = new Application { Name = name, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsVersion_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppVersionAddTestApp");
        var repository = new AppVersionRepository(context);

        var created = await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0", ReleaseNotes = "Initial release", CreatedAt = DateTime.UtcNow });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("1.0.0", loaded!.VersionNumber);
        Assert.Equal("Initial release", loaded.ReleaseNotes);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByApplicationIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppVersionFilterTestApp");
        var repository = new AppVersionRepository(context);

        var active = await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0", CreatedAt = DateTime.UtcNow });
        var toDeactivate = await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "0.9.0", CreatedAt = DateTime.UtcNow });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByApplicationIdAsync(appId);
        var withInactive = await repository.GetByApplicationIdAsync(appId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesReleaseNotes_LeavesVersionNumberUnchanged()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppVersionRenameTestApp");
        var repository = new AppVersionRepository(context);
        var created = await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0", ReleaseNotes = "OldNotes", CreatedAt = DateTime.UtcNow });

        var renamed = await repository.RenameAsync(created.Id, "NewNotes");

        Assert.Equal("NewNotes", renamed.ReleaseNotes);
        Assert.Equal("1.0.0", renamed.VersionNumber);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppVersionDeactivateTestApp");
        var repository = new AppVersionRepository(context);
        var created = await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0", CreatedAt = DateTime.UtcNow });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateVersionNumberFailure_SubsequentUniqueVersionNumberStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppVersionCircuitTestApp");
        var repository = new AppVersionRepository(context);

        await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0-dup", CreatedAt = DateTime.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0-dup", CreatedAt = DateTime.UtcNow }));

        var created = await repository.AddAsync(new AppVersion { ApplicationId = appId, VersionNumber = "1.0.0-unique", CreatedAt = DateTime.UtcNow });

        Assert.Equal("1.0.0-unique", created.VersionNumber);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter AppVersionRepositoryTests`
Expected: FAIL — `AppVersionRepository` does not exist yet.

- [ ] **Step 3: Implement `AppVersionRepository`**

```csharp
// src/LogsPlatform.Infrastructure/Repositories/AppVersionRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class AppVersionRepository : IAppVersionRepository
{
    private readonly LogsPlatformDbContext _context;

    public AppVersionRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<AppVersion?> GetByIdAsync(int id) =>
        await _context.Versions.FindAsync(id);

    public async Task<IReadOnlyList<AppVersion>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        var query = _context.Versions.AsNoTracking().Where(v => v.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(v => v.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<AppVersion> AddAsync(AppVersion version)
    {
        _context.Versions.Add(version);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(version).State = EntityState.Detached;
            throw;
        }
        return version;
    }

    public async Task<AppVersion> RenameAsync(int id, string? releaseNotes)
    {
        var version = await _context.Versions.FindAsync(id)
            ?? throw new InvalidOperationException($"AppVersion {id} not found.");
        version.ReleaseNotes = releaseNotes;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(version).State = EntityState.Detached;
            throw;
        }
        return version;
    }

    public async Task DeactivateAsync(int id)
    {
        var version = await _context.Versions.FindAsync(id)
            ?? throw new InvalidOperationException($"AppVersion {id} not found.");
        version.IsActive = false;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(version).State = EntityState.Detached;
            throw;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter AppVersionRepositoryTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Infrastructure/Repositories/AppVersionRepository.cs tests/LogsPlatform.Tests/Infrastructure/AppVersionRepositoryTests.cs
git commit -m "Implement AppVersionRepository with detach-on-failure handling"
```

---

### Task 4: `DeploymentRepository` implementation + tests

**Files:**
- Create: `src/LogsPlatform.Infrastructure/Repositories/DeploymentRepository.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/DeploymentRepositoryTests.cs`

**Interfaces:**
- Consumes: `IDeploymentRepository` (Task 1), `LogsPlatformDbContext` (Task 2).
- Produces: `DeploymentRepository` — registered in DI by Task 5, consumed by Task 7's controller and Task 9's UI component.

**Note:** unlike `AppVersionRepository`, `DeploymentRepository.AddAsync` has **no** `IsUniqueViolation()` catch anywhere — there is no unique index on `Deployment` at all (see Global Constraints). The test below proves the same `(EnvironmentId, VersionId)` pair can be used twice, rather than proving a conflict throws.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/DeploymentRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class DeploymentRepositoryTests
{
    private static async Task<(int ApplicationId, int EnvironmentId, int VersionId)> CreateTestFixtureAsync(LogsPlatformDbContext context, string appName)
    {
        var application = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        var environment = new AppEnvironment { Name = "Production", IsProduction = true };
        application.Environments.Add(environment);
        var version = new AppVersion { VersionNumber = "1.0.0", CreatedAt = DateTime.UtcNow };
        application.Versions.Add(version);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return (application.Id, environment.Id, version.Id);
    }

    [Fact]
    public async Task AddAsync_PersistsDeployment_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, versionId) = await CreateTestFixtureAsync(context, "DeploymentAddTestApp");
        var repository = new DeploymentRepository(context);

        var created = await repository.AddAsync(new Deployment
        {
            ApplicationId = appId,
            EnvironmentId = envId,
            VersionId = versionId,
            DeployedAt = DateTime.UtcNow,
            Notes = "Initial deploy"
        });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal(envId, loaded!.EnvironmentId);
        Assert.Equal(versionId, loaded.VersionId);
        Assert.Equal("Initial deploy", loaded.Notes);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByApplicationIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, versionId) = await CreateTestFixtureAsync(context, "DeploymentFilterTestApp");
        var repository = new DeploymentRepository(context);

        var active = await repository.AddAsync(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = versionId, DeployedAt = DateTime.UtcNow });
        var toDeactivate = await repository.AddAsync(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = versionId, DeployedAt = DateTime.UtcNow });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByApplicationIdAsync(appId);
        var withInactive = await repository.GetByApplicationIdAsync(appId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesNotes_LeavesOtherFieldsUnchanged()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, versionId) = await CreateTestFixtureAsync(context, "DeploymentRenameTestApp");
        var repository = new DeploymentRepository(context);
        var created = await repository.AddAsync(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = versionId, DeployedAt = DateTime.UtcNow, Notes = "OldNotes" });

        var renamed = await repository.RenameAsync(created.Id, "NewNotes");

        Assert.Equal("NewNotes", renamed.Notes);
        Assert.Equal(envId, renamed.EnvironmentId);
        Assert.Equal(versionId, renamed.VersionId);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, versionId) = await CreateTestFixtureAsync(context, "DeploymentDeactivateTestApp");
        var repository = new DeploymentRepository(context);
        var created = await repository.AddAsync(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = versionId, DeployedAt = DateTime.UtcNow });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_SameEnvironmentAndVersionTwice_BothSucceed()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, versionId) = await CreateTestFixtureAsync(context, "DeploymentRedeployTestApp");
        var repository = new DeploymentRepository(context);

        var first = await repository.AddAsync(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = versionId, DeployedAt = DateTime.UtcNow });
        var second = await repository.AddAsync(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = versionId, DeployedAt = DateTime.UtcNow });

        Assert.NotEqual(first.Id, second.Id);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter DeploymentRepositoryTests`
Expected: FAIL — `DeploymentRepository` does not exist yet.

- [ ] **Step 3: Implement `DeploymentRepository`**

```csharp
// src/LogsPlatform.Infrastructure/Repositories/DeploymentRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class DeploymentRepository : IDeploymentRepository
{
    private readonly LogsPlatformDbContext _context;

    public DeploymentRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<Deployment?> GetByIdAsync(int id) =>
        await _context.Deployments.FindAsync(id);

    public async Task<IReadOnlyList<Deployment>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        var query = _context.Deployments.AsNoTracking().Where(d => d.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(d => d.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<Deployment> AddAsync(Deployment deployment)
    {
        _context.Deployments.Add(deployment);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(deployment).State = EntityState.Detached;
            throw;
        }
        return deployment;
    }

    public async Task<Deployment> RenameAsync(int id, string? notes)
    {
        var deployment = await _context.Deployments.FindAsync(id)
            ?? throw new InvalidOperationException($"Deployment {id} not found.");
        deployment.Notes = notes;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(deployment).State = EntityState.Detached;
            throw;
        }
        return deployment;
    }

    public async Task DeactivateAsync(int id)
    {
        var deployment = await _context.Deployments.FindAsync(id)
            ?? throw new InvalidOperationException($"Deployment {id} not found.");
        deployment.IsActive = false;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(deployment).State = EntityState.Detached;
            throw;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter DeploymentRepositoryTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 150, Skipped: 0, Total: 150` (140 from Tasks 1-2 + 5 from Task 3 + 5 from this task).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Infrastructure/Repositories/DeploymentRepository.cs tests/LogsPlatform.Tests/Infrastructure/DeploymentRepositoryTests.cs
git commit -m "Implement DeploymentRepository with detach-on-failure handling, no uniqueness constraint"
```

---

### Task 5: Wire up DI registrations in `Program.cs`

**Files:**
- Modify: `src/LogsPlatform.Web/Program.cs`

**Interfaces:**
- Consumes: `IAppVersionRepository`/`AppVersionRepository`, `IDeploymentRepository`/`DeploymentRepository` (Tasks 1/3/4).
- Produces: DI registrations that make Tasks 6-7's controllers and Tasks 8-9's UI components resolvable.

- [ ] **Step 1: Add the two new DI registrations**

Modify `Program.cs` — add these two lines directly after the existing `AddScoped<IApiKeyRepository, ApiKeyRepository>();` line:

```csharp
builder.Services.AddScoped<IAppVersionRepository, AppVersionRepository>();
builder.Services.AddScoped<IDeploymentRepository, DeploymentRepository>();
```

The full `Program.cs` after this change:

```csharp
// src/LogsPlatform.Web/Program.cs — full file content
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Web.Components;
using LogsPlatform.Web.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContext<LogsPlatformDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("LogsPlatformDb")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:LogsPlatformDb configuration.")));

builder.Services.AddScoped<IApplicationRepository, ApplicationRepository>();
builder.Services.AddScoped<IAppEnvironmentRepository, AppEnvironmentRepository>();
builder.Services.AddScoped<IAppModuleRepository, AppModuleRepository>();
builder.Services.AddScoped<IScreenServiceRepository, ScreenServiceRepository>();
builder.Services.AddScoped<IProcessNodeRepository, ProcessNodeRepository>();
builder.Services.AddScoped<IOperationRepository, OperationRepository>();
builder.Services.AddScoped<BreadcrumbBuilder>();
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<IAppUserRepository, AppUserRepository>();
builder.Services.AddScoped<ILogSourceRepository, LogSourceRepository>();
builder.Services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
builder.Services.AddScoped<IAppVersionRepository, AppVersionRepository>();
builder.Services.AddScoped<IDeploymentRepository, DeploymentRepository>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program
{
} // exposes Program for WebApplicationFactory<Program> in tests
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 150, Skipped: 0, Total: 150` — unchanged.

- [ ] **Step 4: Commit**

```bash
git add src/LogsPlatform.Web/Program.cs
git commit -m "Wire up DI for AppVersionRepository, DeploymentRepository"
```

---

### Task 6: `VersionsController` + tests

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/AppVersionContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/VersionsController.cs`
- Create: `tests/LogsPlatform.Tests/Web/VersionsControllerTests.cs`

**Interfaces:**
- Consumes: `IAppVersionRepository` (Task 1/3), `IApplicationRepository` (existing, for the parent-existence guard), DI wiring (Task 5).
- Produces: `POST/GET/PUT/DELETE /api/v1/admin/applications/{appId}/versions[/{id}]`.

**Note:** `Create` wraps `AddAsync` in a try/catch for `IsUniqueViolation()` (reachable — `VersionNumber` is unique). `Rename` does **not** — `ReleaseNotes` isn't part of the unique index, so no `409` case is reachable there (see Global Constraints; this is the same asymmetry `Customer`/`AppUser` already have).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/VersionsControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class VersionsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public VersionsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<int> CreateApplicationAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(name, null));
        var created = await response.Content.ReadFromJsonAsync<ApplicationResponse>();
        return created!.Id;
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsVersion()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "VersionControllerTestApp1");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/versions",
            new CreateVersionRequest("1.0.0", "Initial release"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<VersionResponse>();
        Assert.NotNull(created);
        Assert.Equal("1.0.0", created!.VersionNumber);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/versions/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateVersionNumber_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "VersionControllerTestApp2");
        var request = new CreateVersionRequest("1.0.0-dup", null);

        var first = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/versions", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/versions",
            new CreateVersionRequest("1.0.0-dup", null));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_VersionBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "VersionIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "VersionIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/versions",
            new CreateVersionRequest("1.0.0", null));
        var created = await createResponse.Content.ReadFromJsonAsync<VersionResponse>();

        var crossAppGet = await client.GetAsync($"/api/v1/admin/applications/{appId2}/versions/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossAppGet.StatusCode);
    }

    [Fact]
    public async Task Rename_VersionBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "VersionRenameIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "VersionRenameIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/versions",
            new CreateVersionRequest("1.0.0", null));
        var created = await createResponse.Content.ReadFromJsonAsync<VersionResponse>();

        var crossAppRename = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId2}/versions/{created!.Id}",
            new RenameVersionRequest("Hijacked"));

        Assert.Equal(HttpStatusCode.NotFound, crossAppRename.StatusCode);
    }

    [Fact]
    public async Task Deactivate_VersionBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "VersionDeactivateIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "VersionDeactivateIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/versions",
            new CreateVersionRequest("1.0.0", null));
        var created = await createResponse.Content.ReadFromJsonAsync<VersionResponse>();

        var crossAppDeactivate = await client.DeleteAsync($"/api/v1/admin/applications/{appId2}/versions/{created!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, crossAppDeactivate.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesReleaseNotes_LeavesVersionNumberUnchanged()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "VersionRenameControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/versions",
            new CreateVersionRequest("1.0.0", "OldNotes"));
        var created = await createResponse.Content.ReadFromJsonAsync<VersionResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/versions/{created!.Id}",
            new RenameVersionRequest("NewNotes"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<VersionResponse>();
        Assert.Equal("NewNotes", renamed!.ReleaseNotes);
        Assert.Equal("1.0.0", renamed.VersionNumber);
    }

    [Fact]
    public async Task Create_UnknownApplicationId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications/999999/versions",
            new CreateVersionRequest("1.0.0", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "VersionDeactivateControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/versions",
            new CreateVersionRequest("1.0.0", null));
        var created = await createResponse.Content.ReadFromJsonAsync<VersionResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/versions/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<VersionResponse>>($"/api/v1/admin/applications/{appId}/versions");
        Assert.DoesNotContain(listResponse!, v => v.Id == created.Id);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VersionsControllerTests`
Expected: FAIL — `CreateVersionRequest`/`VersionResponse`/`VersionsController` do not exist yet.

- [ ] **Step 3: Write the request/response contracts**

```csharp
// src/LogsPlatform.Web/Contracts/AppVersionContracts.cs
namespace LogsPlatform.Web.Contracts;

public record CreateVersionRequest(string VersionNumber, string? ReleaseNotes);

public record VersionResponse(int Id, int ApplicationId, string VersionNumber, string? ReleaseNotes, DateTime CreatedAt, bool IsActive);

public record RenameVersionRequest(string? ReleaseNotes);
```

- [ ] **Step 4: Implement `VersionsController`**

```csharp
// src/LogsPlatform.Web/Controllers/VersionsController.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/versions")]
public class VersionsController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppVersionRepository _versions;

    public VersionsController(IApplicationRepository applications, IAppVersionRepository versions)
    {
        _applications = applications;
        _versions = versions;
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
        return ToResponse(version);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _versions.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _versions.DeactivateAsync(id);
        return NoContent();
    }

    private static VersionResponse ToResponse(AppVersion version) =>
        new(version.Id, version.ApplicationId, version.VersionNumber, version.ReleaseNotes, version.CreatedAt, version.IsActive);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter VersionsControllerTests`
Expected: PASS (8 tests).

- [ ] **Step 6: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 158, Skipped: 0, Total: 158` (150 from Tasks 1-5 + 8 from this task).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/AppVersionContracts.cs src/LogsPlatform.Web/Controllers/VersionsController.cs tests/LogsPlatform.Tests/Web/VersionsControllerTests.cs
git commit -m "Add VersionsController with create/list/get/rename/deactivate"
```

---

### Task 7: `DeploymentsController` + tests

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/DeploymentContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/DeploymentsController.cs`
- Create: `tests/LogsPlatform.Tests/Web/DeploymentsControllerTests.cs`

**Interfaces:**
- Consumes: `IDeploymentRepository` (Task 1/4), `IApplicationRepository` (existing, parent-existence guard), `IAppEnvironmentRepository` (existing, for the `environmentId`-ownership guard), `IAppVersionRepository` (Task 1/3, for the `versionId`-ownership guard), DI wiring (Task 5).
- Produces: `POST/GET/PUT/DELETE /api/v1/admin/applications/{appId}/deployments[/{id}]`.

**Note:** no `IsUniqueViolation()` catch anywhere in this controller (neither `Create` nor `Rename`) — see Global Constraints. `Create` has THREE guards, not the usual one — read Global Constraints' `DeploymentsController.Create` note carefully before implementing.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/DeploymentsControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class DeploymentsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public DeploymentsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<int> CreateApplicationAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(name, null));
        var created = await response.Content.ReadFromJsonAsync<ApplicationResponse>();
        return created!.Id;
    }

    private static async Task<(int EnvironmentId, int VersionId)> CreateFixtureAsync(HttpClient client, int appId)
    {
        var envResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/environments",
            new CreateEnvironmentRequest("Production", true));
        var env = await envResponse.Content.ReadFromJsonAsync<EnvironmentResponse>();

        var versionResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/versions",
            new CreateVersionRequest("1.0.0", null));
        var version = await versionResponse.Content.ReadFromJsonAsync<VersionResponse>();

        return (env!.Id, version!.Id);
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsDeployment()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "DeploymentControllerTestApp1");
        var (envId, versionId) = await CreateFixtureAsync(client, appId);
        var deployedAt = DateTime.UtcNow;

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/deployments",
            new CreateDeploymentRequest(envId, versionId, deployedAt, "First deploy"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<DeploymentResponse>();
        Assert.NotNull(created);
        Assert.Equal(envId, created!.EnvironmentId);
        Assert.Equal(versionId, created.VersionId);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/deployments/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_EnvironmentBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "DeploymentEnvIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "DeploymentEnvIdorTestApp2");
        var (envIdFromApp1, _) = await CreateFixtureAsync(client, appId1);
        var (_, versionIdFromApp2) = await CreateFixtureAsync(client, appId2);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId2}/deployments",
            new CreateDeploymentRequest(envIdFromApp1, versionIdFromApp2, DateTime.UtcNow, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_VersionBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "DeploymentVersionIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "DeploymentVersionIdorTestApp2");
        var (_, versionIdFromApp1) = await CreateFixtureAsync(client, appId1);
        var (envIdFromApp2, _) = await CreateFixtureAsync(client, appId2);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId2}/deployments",
            new CreateDeploymentRequest(envIdFromApp2, versionIdFromApp1, DateTime.UtcNow, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownApplicationId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications/999999/deployments",
            new CreateDeploymentRequest(1, 1, DateTime.UtcNow, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetById_DeploymentBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "DeploymentGetIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "DeploymentGetIdorTestApp2");
        var (envId, versionId) = await CreateFixtureAsync(client, appId1);
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/deployments",
            new CreateDeploymentRequest(envId, versionId, DateTime.UtcNow, null));
        var created = await createResponse.Content.ReadFromJsonAsync<DeploymentResponse>();

        var crossAppGet = await client.GetAsync($"/api/v1/admin/applications/{appId2}/deployments/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossAppGet.StatusCode);
    }

    [Fact]
    public async Task Rename_DeploymentBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "DeploymentRenameIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "DeploymentRenameIdorTestApp2");
        var (envId, versionId) = await CreateFixtureAsync(client, appId1);
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/deployments",
            new CreateDeploymentRequest(envId, versionId, DateTime.UtcNow, null));
        var created = await createResponse.Content.ReadFromJsonAsync<DeploymentResponse>();

        var crossAppRename = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId2}/deployments/{created!.Id}",
            new RenameDeploymentRequest("Hijacked"));

        Assert.Equal(HttpStatusCode.NotFound, crossAppRename.StatusCode);
    }

    [Fact]
    public async Task Deactivate_DeploymentBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "DeploymentDeactivateIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "DeploymentDeactivateIdorTestApp2");
        var (envId, versionId) = await CreateFixtureAsync(client, appId1);
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/deployments",
            new CreateDeploymentRequest(envId, versionId, DateTime.UtcNow, null));
        var created = await createResponse.Content.ReadFromJsonAsync<DeploymentResponse>();

        var crossAppDeactivate = await client.DeleteAsync($"/api/v1/admin/applications/{appId2}/deployments/{created!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, crossAppDeactivate.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesNotes()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "DeploymentRenameControllerTestApp");
        var (envId, versionId) = await CreateFixtureAsync(client, appId);
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/deployments",
            new CreateDeploymentRequest(envId, versionId, DateTime.UtcNow, "OldNotes"));
        var created = await createResponse.Content.ReadFromJsonAsync<DeploymentResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/deployments/{created!.Id}",
            new RenameDeploymentRequest("NewNotes"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<DeploymentResponse>();
        Assert.Equal("NewNotes", renamed!.Notes);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "DeploymentDeactivateControllerTestApp");
        var (envId, versionId) = await CreateFixtureAsync(client, appId);
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/deployments",
            new CreateDeploymentRequest(envId, versionId, DateTime.UtcNow, null));
        var created = await createResponse.Content.ReadFromJsonAsync<DeploymentResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/deployments/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<DeploymentResponse>>($"/api/v1/admin/applications/{appId}/deployments");
        Assert.DoesNotContain(listResponse!, d => d.Id == created.Id);
    }

    [Fact]
    public async Task Create_SameEnvironmentAndVersionTwice_BothSucceed()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "DeploymentRedeployControllerTestApp");
        var (envId, versionId) = await CreateFixtureAsync(client, appId);

        var first = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/deployments",
            new CreateDeploymentRequest(envId, versionId, DateTime.UtcNow, "First"));
        var second = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/deployments",
            new CreateDeploymentRequest(envId, versionId, DateTime.UtcNow, "Redeploy"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }
}
```

Note this test file references `CreateEnvironmentRequest(string Name, bool IsProduction)`/`EnvironmentResponse(int Id, int ApplicationId, string Name, bool IsProduction)` from the existing (already-merged) `src/LogsPlatform.Web/Contracts/ApplicationContracts.cs` and posts to the existing (already-merged) `EnvironmentsController`'s route, `/api/v1/admin/applications/{appId}/environments` — both already exist on `main` before this plan starts; this test only exercises them as a fixture, it does not create or modify either.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter DeploymentsControllerTests`
Expected: FAIL — `CreateDeploymentRequest`/`DeploymentResponse`/`DeploymentsController` do not exist yet.

- [ ] **Step 3: Write the request/response contracts**

```csharp
// src/LogsPlatform.Web/Contracts/DeploymentContracts.cs
namespace LogsPlatform.Web.Contracts;

public record CreateDeploymentRequest(int EnvironmentId, int VersionId, DateTime DeployedAt, string? Notes);

public record DeploymentResponse(int Id, int ApplicationId, int EnvironmentId, int VersionId, DateTime DeployedAt, string? Notes, bool IsActive);

public record RenameDeploymentRequest(string? Notes);
```

- [ ] **Step 4: Implement `DeploymentsController`**

```csharp
// src/LogsPlatform.Web/Controllers/DeploymentsController.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
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

    public DeploymentsController(
        IApplicationRepository applications,
        IAppEnvironmentRepository environments,
        IAppVersionRepository versions,
        IDeploymentRepository deployments)
    {
        _applications = applications;
        _environments = environments;
        _versions = versions;
        _deployments = deployments;
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
        return ToResponse(deployment);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _deployments.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _deployments.DeactivateAsync(id);
        return NoContent();
    }

    private static DeploymentResponse ToResponse(Deployment deployment) =>
        new(deployment.Id, deployment.ApplicationId, deployment.EnvironmentId, deployment.VersionId, deployment.DeployedAt, deployment.Notes, deployment.IsActive);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter DeploymentsControllerTests`
Expected: PASS (10 tests).

- [ ] **Step 6: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 168, Skipped: 0, Total: 168` (158 from Tasks 1-6 + 10 from this task).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/DeploymentContracts.cs src/LogsPlatform.Web/Controllers/DeploymentsController.cs tests/LogsPlatform.Tests/Web/DeploymentsControllerTests.cs
git commit -m "Add DeploymentsController with three-guard create validation"
```

---

### Task 8: `VersionsSection.razor` UI component

**Files:**
- Create: `src/LogsPlatform.Web/Components/Shared/VersionsSection.razor`
- Modify: `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor`

**Interfaces:**
- Consumes: `IAppVersionRepository` (Task 1/3), DI wiring (Task 5).
- Produces: the "Versions" subsection on `ApplicationsAdmin.razor`'s per-row expansion.

- [ ] **Step 1: Create `VersionsSection.razor`**

```razor
@* src/LogsPlatform.Web/Components/Shared/VersionsSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using Microsoft.EntityFrameworkCore
@inject IAppVersionRepository VersionRepository

<h4>Versions</h4>
<table>
    <thead>
        <tr>
            <th>Version Number</th>
            <th>Release Notes</th>
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
                            <InputText @bind-Value="_editModel!.ReleaseNotes" />
                            <button type="submit">Save</button>
                            <button type="button" @onclick="CancelEdit">Cancel</button>
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
                        <button @onclick="() => StartEdit(version)">Edit</button>
                    }
                    <button @onclick="() => DeactivateAsync(version.Id)">Deactivate</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newVersion" OnValidSubmit="CreateVersionAsync">
    <label>
        Version Number:
        <InputText @bind-Value="_newVersion.VersionNumber" required maxlength="200" />
    </label>
    <label>
        Release Notes:
        <InputText @bind-Value="_newVersion.ReleaseNotes" />
    </label>
    <button type="submit">Add Version</button>
</EditForm>
@if (_createError is not null)
{
    <p style="color:red">@_createError</p>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<AppVersion> _versions = new();
    private readonly NewVersionModel _newVersion = new();
    private string? _createError;

    private int? _editingId;
    private EditVersionModel? _editModel;

    protected override async Task OnInitializedAsync()
    {
        _versions = (await VersionRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateVersionAsync()
    {
        _createError = null;
        try
        {
            await VersionRepository.AddAsync(new AppVersion
            {
                ApplicationId = ApplicationId,
                VersionNumber = _newVersion.VersionNumber,
                ReleaseNotes = _newVersion.ReleaseNotes,
                CreatedAt = DateTime.UtcNow
            });

            _newVersion.VersionNumber = string.Empty;
            _newVersion.ReleaseNotes = null;
            _versions = (await VersionRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"A version '{_newVersion.VersionNumber}' already exists.";
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
        await VersionRepository.RenameAsync(versionId, _editModel!.ReleaseNotes);
        _editingId = null;
        _editModel = null;
        _versions = (await VersionRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task DeactivateAsync(int versionId)
    {
        await VersionRepository.DeactivateAsync(versionId);
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

No inline-edit toggle touches `VersionNumber` anywhere — the edit form only ever binds `ReleaseNotes`.

- [ ] **Step 2: Wire `VersionsSection` into `ApplicationsAdmin.razor`**

Modify `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor` — add one line directly after the existing `<ApiKeysSection ApplicationId="application.Id" />` line:

```razor
<VersionsSection ApplicationId="application.Id" />
```

The `@using LogsPlatform.Web.Components.Shared` directive is already present from B1 — no other change to this file. Do not modify anything else in `ApplicationsAdmin.razor`.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 168, Skipped: 0, Total: 168` — unchanged (this task adds no automated tests, matching the established UI-task testing posture).

- [ ] **Step 5: Verify by code inspection (curl cannot reach this content)**

`VersionsSection` is nested inside `ApplicationsAdmin.razor`'s `_expandedAppIds.Contains(...)` conditional, which is `false` for every row on a cold page load. A `curl` request only ever sees the server's initial static render, so it can **never** find `Versions`/`<h4>Versions</h4>` in the response — not even when the component is correctly wired. `curl`-based smoke checks are unusable for any content behind this expand toggle; do not attempt one here or treat a failed grep as a defect. Instead confirm the component is correctly wired by inspection: `<VersionsSection ApplicationId="application.Id" />` is present inside `ApplicationsAdmin.razor`'s expanded-row block, directly after `<ApiKeysSection>`, and the build in Step 3 succeeded (a missing/misspelled component reference is a compile error, not a silent no-op). Full interactive confirmation happens once during the manual walkthrough after all of Group B3 merges (see the plan's closing verification section), not per-task.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/VersionsSection.razor src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor
git commit -m "Add VersionsSection UI component"
```

---

### Task 9: `DeploymentsSection.razor` UI component — completes Group B3

**Files:**
- Create: `src/LogsPlatform.Web/Components/Shared/DeploymentsSection.razor`
- Modify: `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor`

**Interfaces:**
- Consumes: `IDeploymentRepository` (Task 1/4), `IAppEnvironmentRepository` (existing, read-only, to populate the Environment dropdown), `IAppVersionRepository` (Task 1/3, read-only, to populate the Version dropdown), DI wiring (Task 5).
- Produces: the "Deployments" subsection on `ApplicationsAdmin.razor`'s per-row expansion.

- [ ] **Step 1: Create `DeploymentsSection.razor`**

```razor
@* src/LogsPlatform.Web/Components/Shared/DeploymentsSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@inject IDeploymentRepository DeploymentRepository
@inject IAppEnvironmentRepository EnvironmentRepository
@inject IAppVersionRepository VersionRepository

<h4>Deployments</h4>
<table>
    <thead>
        <tr>
            <th>Environment</th>
            <th>Version</th>
            <th>Deployed At</th>
            <th>Notes</th>
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
                            <InputText @bind-Value="_editModel!.Notes" />
                            <button type="submit">Save</button>
                            <button type="button" @onclick="CancelEdit">Cancel</button>
                        </EditForm>
                    </td>
                }
                else
                {
                    <td>@EnvironmentName(deployment.EnvironmentId)</td>
                    <td>@VersionNumber(deployment.VersionId)</td>
                    <td>@deployment.DeployedAt</td>
                    <td>@deployment.Notes</td>
                }
                <td>
                    @if (_editingId != deployment.Id)
                    {
                        <button @onclick="() => StartEdit(deployment)">Edit</button>
                    }
                    <button @onclick="() => DeactivateAsync(deployment.Id)">Deactivate</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newDeployment" OnValidSubmit="CreateDeploymentAsync">
    <label>
        Environment:
        <InputSelect @bind-Value="_newDeployment.EnvironmentId">
            <option value="0">-- select --</option>
            @foreach (var environment in _environments)
            {
                <option value="@environment.Id">@environment.Name</option>
            }
        </InputSelect>
    </label>
    <label>
        Version:
        <InputSelect @bind-Value="_newDeployment.VersionId">
            <option value="0">-- select --</option>
            @foreach (var version in _versions)
            {
                <option value="@version.Id">@version.VersionNumber</option>
            }
        </InputSelect>
    </label>
    <label>
        Deployed At:
        <InputDate @bind-Value="_newDeployment.DeployedAt" Type="InputDateType.DateTimeLocal" />
    </label>
    <label>
        Notes:
        <InputText @bind-Value="_newDeployment.Notes" />
    </label>
    <button type="submit">Add Deployment</button>
</EditForm>
@if (_createError is not null)
{
    <p style="color:red">@_createError</p>
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

    protected override async Task OnInitializedAsync()
    {
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
            _createError = "Select both an environment and a version.";
            return;
        }

        await DeploymentRepository.AddAsync(new Deployment
        {
            ApplicationId = ApplicationId,
            EnvironmentId = _newDeployment.EnvironmentId,
            VersionId = _newDeployment.VersionId,
            DeployedAt = _newDeployment.DeployedAt,
            Notes = _newDeployment.Notes
        });

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
        await DeploymentRepository.RenameAsync(deploymentId, _editModel!.Notes);
        _editingId = null;
        _editModel = null;
        _deployments = (await DeploymentRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task DeactivateAsync(int deploymentId)
    {
        await DeploymentRepository.DeactivateAsync(deploymentId);
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

No `IsUniqueViolation()` catch anywhere in this component — see Global Constraints. No inline-edit toggle touches `Environment`/`Version`/`DeployedAt` — the edit form only ever binds `Notes`. `_environments`/`_versions` are loaded once in `OnInitializedAsync` purely to populate the two dropdowns — this component never writes through `EnvironmentRepository`/`VersionRepository`.

- [ ] **Step 2: Wire `DeploymentsSection` into `ApplicationsAdmin.razor`**

Modify `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor` — add one line directly after the `<VersionsSection ApplicationId="application.Id" />` line added in Task 8:

```razor
<DeploymentsSection ApplicationId="application.Id" />
```

No other change to this file.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 168, Skipped: 0, Total: 168` — unchanged (this task adds no automated tests).

- [ ] **Step 5: Verify by code inspection (curl cannot reach this content)**

Same reasoning as Task 8's Step 5 — `DeploymentsSection` lives behind the same `_expandedAppIds.Contains(...)` conditional, so `curl` can never observe it regardless of correctness. Confirm by inspection: `<DeploymentsSection ApplicationId="application.Id" />` is present directly after `<VersionsSection>`, and the build in Step 3 succeeded. Full interactive confirmation — creating an `AppVersion`, then creating a `Deployment` that references it via the dropdown, then deactivating both — happens once during the manual walkthrough below.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/DeploymentsSection.razor src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor
git commit -m "Add DeploymentsSection UI component — completes Group B3"
```

---

## Closing Verification (after all 9 tasks merge)

Run a full manual walkthrough in a real browser against the dev database:
1. Start the app (`dotnet run --project src/LogsPlatform.Web --launch-profile http`), navigate to `/admin/applications`.
2. Expand an application row. Confirm "Versions" and "Deployments" appear as new subsections, after API Keys.
3. Create a Version (e.g. `1.0.0`, with release notes). Confirm it appears in the Versions table.
4. Collapse and re-expand the row (so `DeploymentsSection` re-fetches). Confirm the new version now appears in the Deployments create form's Version dropdown.
5. Create a Deployment referencing that version and an existing Environment, with a `DeployedAt` timestamp and notes. Confirm it appears in the Deployments table, showing the environment name and version number (not raw ids).
6. Edit the Version's release notes via inline-edit; confirm the Version Number itself has no edit control. Edit the Deployment's notes via inline-edit; confirm Environment/Version/Deployed At have no edit control.
7. Deactivate the Deployment, then the Version. Confirm both disappear from their default lists.
8. Confirm no console/server errors during any of the above.

This is the last plan in Group B — once this walkthrough passes and the branch merges, M1's own acceptance criterion ("fully define RetailPulse+FieldOps via the API/UI") is met, and M2 (Ingestion) is next per the project's milestone roadmap.
