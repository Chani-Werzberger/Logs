# Hierarchy Spine A1: AppModule + ScreenService Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Full backend (entities, EF Core mapping, repositories, Admin API) for the first two levels of the Application hierarchy spine — `AppModule` (child of `Application`) and `ScreenService` (child of `AppModule`) — with Create/List/GetById/Rename/Deactivate, matching the shape specified in `docs/superpowers/specs/2026-08-18-application-hierarchy-spine-design.md`.

**Architecture:** Same Modular Monolith, same layering (`LogsPlatform.Domain` entities/interfaces → `LogsPlatform.Infrastructure` EF Core/repositories → `LogsPlatform.Web` controllers) already established by the `Application`/`AppEnvironment` slice. This plan replicates that exact pattern for two more entities, adding Rename and Deactivate (soft-delete) which the prior slice didn't need.

**Tech Stack:** Same as the current solution — .NET 10, EF Core 10.0.11, SQL Server, xUnit + `Microsoft.AspNetCore.Mvc.Testing`. No new packages.

## Global Constraints

- **Naming:** the C# entity class is `AppModule`, **never** `Module` — `System.Reflection.Module` is a real BCL type, and this project consistently avoids this class of collision (`AppEnvironment`, `ProcessNode` already follow this rule). The hierarchy level is still called "Module" in routes, UI text, and prose.
- **Detach-on-failure from the start, not discovered later:** every repository write method that can fail on a unique-constraint violation (`AddAsync`, `RenameAsync`) must wrap `SaveChangesAsync` in `try`/`catch`, set `_context.Entry(entity).State = EntityState.Detached` in the `catch`, and re-throw — **before** the first test is written, not as a fix applied after a bug report. This is the exact circuit-scoped-`DbContext` lesson the prior UI plan's final review found the hard way (a rejected insert/update staying tracked poisons the next save on the same `DbContext` instance) — it applies identically here since these repositories will eventually be called from Blazor pages too (Plan A3).
- **IDOR protection on every child-by-id endpoint:** this plan introduces `GET/PUT/DELETE .../{id}` routes that take a raw child ID — unlike the `AppEnvironment` slice, which never added a single-entity-by-id route. Every controller action that receives a child ID **must** verify that child actually belongs to the parent ID also present in the route (e.g., a `AppModule` with a given `id` must have `ApplicationId == appId`), returning `404` if it doesn't — otherwise an authenticated caller could read/rename/deactivate another application's module just by guessing a numeric ID. This is a direct, deliberate closure of a risk a prior review named explicitly ("the moment someone adds `GET /environments/{id}`, it's a trivial IDOR").
- **Soft-delete only — no hard-delete anywhere in this plan.** `DeactivateAsync` always sets `IsActive = false`. The spec's "hard-delete if no history" branch is not implementable yet (the `Event` table doesn't exist until M2) and is explicitly out of scope here — see the design doc's "Deactivate Semantics" section.
- **Name uniqueness is scoped to the immediate parent:** `AppModule.Name` unique within `ApplicationId`; `ScreenService.Name` unique within `ModuleId`. Both enforced via a composite unique index, matching the `AppEnvironment.(ApplicationId, Name)` pattern already in the codebase.
- **Routes are scoped by immediate parent only, not the full ancestor chain:** `AppModule` under `/api/v1/admin/applications/{appId}/modules` (matches the existing `AppEnvironment` convention), `ScreenService` under `/api/v1/admin/modules/{moduleId}/screen-services` (short form — not re-prefixed with `/applications/{appId}`), per the design doc.
- Target framework `net10.0`, EF Core packages pinned at `10.0.11` everywhere (already the case — this plan adds no new package references).

---

### Task 1: Domain entities (`AppModule`, `ScreenService`) + repository interfaces

**Files:**
- Create: `src/LogsPlatform.Domain/Entities/AppModule.cs`
- Create: `src/LogsPlatform.Domain/Entities/ScreenService.cs`
- Modify: `src/LogsPlatform.Domain/Entities/Application.cs` (add `Modules` navigation collection)
- Create: `src/LogsPlatform.Domain/Repositories/IModuleRepository.cs`
- Create: `src/LogsPlatform.Domain/Repositories/IScreenServiceRepository.cs`

**Interfaces:**
- Consumes: `Application` entity (existing).
- Produces: `AppModule`, `ScreenService` entity classes and `IModuleRepository`, `IScreenServiceRepository` interfaces that Task 3/4 implement against.

- [ ] **Step 1: Write the `AppModule` entity**

```csharp
// src/LogsPlatform.Domain/Entities/AppModule.cs
namespace LogsPlatform.Domain.Entities;

public class AppModule
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<ScreenService> ScreenServices { get; set; } = new List<ScreenService>();
}
```

- [ ] **Step 2: Write the `ScreenService` entity**

```csharp
// src/LogsPlatform.Domain/Entities/ScreenService.cs
namespace LogsPlatform.Domain.Entities;

public enum ScreenServiceType
{
    Screen,
    Service
}

public class ScreenService
{
    public int Id { get; set; }
    public int ModuleId { get; set; }
    public AppModule Module { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public ScreenServiceType Type { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}
```

- [ ] **Step 3: Add the `Modules` navigation collection to `Application`**

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
}
```

- [ ] **Step 4: Write the repository interfaces**

```csharp
// src/LogsPlatform.Domain/Repositories/IModuleRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IModuleRepository
{
    Task<AppModule?> GetByIdAsync(int id);
    Task<IReadOnlyList<AppModule>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<AppModule> AddAsync(AppModule module);
    Task<AppModule> RenameAsync(int id, string name, string? description);
    Task DeactivateAsync(int id);
}
```

```csharp
// src/LogsPlatform.Domain/Repositories/IScreenServiceRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IScreenServiceRepository
{
    Task<ScreenService?> GetByIdAsync(int id);
    Task<IReadOnlyList<ScreenService>> GetByModuleIdAsync(int moduleId, bool includeInactive = false);
    Task<ScreenService> AddAsync(ScreenService screenService);
    Task<ScreenService> RenameAsync(int id, string name, string? description);
    Task DeactivateAsync(int id);
}
```

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Domain/Entities/AppModule.cs src/LogsPlatform.Domain/Entities/ScreenService.cs src/LogsPlatform.Domain/Entities/Application.cs src/LogsPlatform.Domain/Repositories/IModuleRepository.cs src/LogsPlatform.Domain/Repositories/IScreenServiceRepository.cs
git commit -m "Add AppModule and ScreenService domain entities + repository interfaces"
```

---

### Task 2: `LogsPlatformDbContext` mapping + migration

**Files:**
- Modify: `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs`

**Interfaces:**
- Consumes: `AppModule`, `ScreenService` from Task 1.
- Produces: `DbSet<AppModule> Modules`, `DbSet<ScreenService> ScreenServices` on `LogsPlatformDbContext`, plus the migration that creates their tables — used by Task 3/4's repositories.

- [ ] **Step 1: Write the failing test**

```csharp
// Add to tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs
[Fact]
public async Task CanInsertAndRetrieveModuleWithScreenService()
{
    using var context = TestDatabase.CreateContext();

    var application = new Application { Name = "HierarchyDbContextTestApp", CreatedAt = DateTime.UtcNow };
    var module = new AppModule { Name = "Payments" };
    module.ScreenServices.Add(new ScreenService { Name = "PaymentGateway", Type = ScreenServiceType.Service });
    application.Modules.Add(module);

    context.Applications.Add(application);
    await context.SaveChangesAsync();

    using var readContext = new LogsPlatformDbContext(
        new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    var loaded = await readContext.Modules
        .Include(m => m.ScreenServices)
        .FirstAsync(m => m.Name == "Payments");

    Assert.True(loaded.IsActive);
    Assert.Single(loaded.ScreenServices);
    Assert.Equal("PaymentGateway", loaded.ScreenServices.First().Name);
    Assert.Equal(ScreenServiceType.Service, loaded.ScreenServices.First().Type);
    Assert.True(loaded.ScreenServices.First().IsActive);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter CanInsertAndRetrieveModuleWithScreenService`
Expected: FAIL — build error, `context.Modules`/`DbSet<AppModule>` does not exist yet.

- [ ] **Step 3: Add the `DbSet`s and `OnModelCreating` configuration**

Modify `LogsPlatformDbContext.cs` — add two `DbSet` properties and extend `OnModelCreating` (do not remove the existing `Application`/`AppEnvironment` configuration blocks or the `ConfigureConventions` override that wires up `UtcDateTimeConverter` — that override applies to `DateTime` properties globally, including the ones on `AppModule`/`ScreenService` added later in this same plan, so it must stay exactly as-is):

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
    }
}
```

- [ ] **Step 4: Generate the migration**

This is an **additive** migration (unlike the EF-version-upgrade migration regeneration from the prior plan) — the existing `InitialCreate` migration and its history must NOT be touched or regenerated:

```bash
dotnet ef migrations add AddModuleAndScreenService \
  --project src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj \
  --startup-project src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj
```

Expected: creates `src/LogsPlatform.Infrastructure/Migrations/<timestamp>_AddModuleAndScreenService.cs` and updates `LogsPlatformDbContextModelSnapshot.cs` — creating two new tables (`Modules`, `ScreenServices`) with the FK/cascade/unique-index shape from Step 3. Verify the generated migration's `Up()` only adds the two new tables and their indexes — it must not contain any `DropTable`/`DropColumn` against `Applications`/`AppEnvironments`/their existing indexes. If it does, something in Step 3 changed the existing model unintentionally — STOP and investigate before proceeding.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter CanInsertAndRetrieveModuleWithScreenService`
Expected: PASS.

- [ ] **Step 6: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 11, Skipped: 0, Total: 11` (the 10 tests that existed before this task, plus this task's new one).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs src/LogsPlatform.Infrastructure/Migrations/ tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs
git commit -m "Add AppModule and ScreenService EF Core mapping + migration"
```

---

### Task 3: `ModuleRepository` implementation + tests

**Files:**
- Create: `src/LogsPlatform.Infrastructure/Repositories/ModuleRepository.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/ModuleRepositoryTests.cs`

**Interfaces:**
- Consumes: `IModuleRepository` (Task 1), `LogsPlatformDbContext` (Task 2).
- Produces: `ModuleRepository` — registered in DI by Task 5, consumed by Task 6's controller.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/ModuleRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ModuleRepositoryTests
{
    private static async Task<int> CreateTestApplicationAsync(LogsPlatformDbContext context, string name)
    {
        var application = new Application { Name = name, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsModule_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ModuleAddTestApp");
        var repository = new ModuleRepository(context);

        var created = await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "Payments" });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Payments", loaded!.Name);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByApplicationIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ModuleFilterTestApp");
        var repository = new ModuleRepository(context);

        var active = await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "Active" });
        var toDeactivate = await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "WillBeInactive" });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByApplicationIdAsync(appId);
        var withInactive = await repository.GetByApplicationIdAsync(appId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesNameAndDescription()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ModuleRenameTestApp");
        var repository = new ModuleRepository(context);
        var created = await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "OldName" });

        var renamed = await repository.RenameAsync(created.Id, "NewName", "new description");

        Assert.Equal("NewName", renamed.Name);
        Assert.Equal("new description", renamed.Description);
        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.Equal("NewName", reloaded!.Name);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ModuleDeactivateTestApp");
        var repository = new ModuleRepository(context);
        var created = await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "ToDeactivate" });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateNameFailure_SubsequentUniqueNameStillSucceeds()
    {
        // Same circuit-scoped-DbContext regression this project already found and fixed once
        // (see the prior plan's final review) -- proactively guarded here from Task 3 onward.
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ModuleCircuitTestApp");
        var repository = new ModuleRepository(context);

        await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "DupModule" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "DupModule" }));

        var created = await repository.AddAsync(new AppModule { ApplicationId = appId, Name = "UniqueModule" });

        Assert.Equal("UniqueModule", created.Name);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ModuleRepositoryTests`
Expected: FAIL — `ModuleRepository` does not exist yet.

- [ ] **Step 3: Implement `ModuleRepository`**

```csharp
// src/LogsPlatform.Infrastructure/Repositories/ModuleRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ModuleRepository : IModuleRepository
{
    private readonly LogsPlatformDbContext _context;

    public ModuleRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<AppModule?> GetByIdAsync(int id) =>
        await _context.Modules.FindAsync(id);

    public async Task<IReadOnlyList<AppModule>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        var query = _context.Modules.AsNoTracking().Where(m => m.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(m => m.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<AppModule> AddAsync(AppModule module)
    {
        _context.Modules.Add(module);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(module).State = EntityState.Detached;
            throw;
        }
        return module;
    }

    public async Task<AppModule> RenameAsync(int id, string name, string? description)
    {
        var module = await _context.Modules.FindAsync(id)
            ?? throw new InvalidOperationException($"Module {id} not found.");
        module.Name = name;
        module.Description = description;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(module).State = EntityState.Detached;
            throw;
        }
        return module;
    }

    public async Task DeactivateAsync(int id)
    {
        var module = await _context.Modules.FindAsync(id)
            ?? throw new InvalidOperationException($"Module {id} not found.");
        module.IsActive = false;
        await _context.SaveChangesAsync();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ModuleRepositoryTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Infrastructure/Repositories/ModuleRepository.cs tests/LogsPlatform.Tests/Infrastructure/ModuleRepositoryTests.cs
git commit -m "Implement ModuleRepository with proactive detach-on-failure handling"
```

---

### Task 4: `ScreenServiceRepository` implementation + tests

**Files:**
- Create: `src/LogsPlatform.Infrastructure/Repositories/ScreenServiceRepository.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/ScreenServiceRepositoryTests.cs`

**Interfaces:**
- Consumes: `IScreenServiceRepository` (Task 1), `LogsPlatformDbContext` (Task 2), `ModuleRepository` pattern (Task 3, for the parallel test-helper shape).
- Produces: `ScreenServiceRepository` — registered in DI by Task 5, consumed by Task 7's controller.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/ScreenServiceRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ScreenServiceRepositoryTests
{
    private static async Task<int> CreateTestModuleAsync(LogsPlatformDbContext context, string appName, string moduleName)
    {
        var application = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        var module = new AppModule { Name = moduleName };
        application.Modules.Add(module);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return module.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsScreenService_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var moduleId = await CreateTestModuleAsync(context, "ScreenServiceAddTestApp", "Payments");
        var repository = new ScreenServiceRepository(context);

        var created = await repository.AddAsync(new ScreenService
        {
            ModuleId = moduleId,
            Name = "PaymentGateway",
            Type = ScreenServiceType.Service
        });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("PaymentGateway", loaded!.Name);
        Assert.Equal(ScreenServiceType.Service, loaded.Type);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByModuleIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var moduleId = await CreateTestModuleAsync(context, "ScreenServiceFilterTestApp", "Payments");
        var repository = new ScreenServiceRepository(context);

        var active = await repository.AddAsync(new ScreenService { ModuleId = moduleId, Name = "Active", Type = ScreenServiceType.Screen });
        var toDeactivate = await repository.AddAsync(new ScreenService { ModuleId = moduleId, Name = "WillBeInactive", Type = ScreenServiceType.Screen });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByModuleIdAsync(moduleId);
        var withInactive = await repository.GetByModuleIdAsync(moduleId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesNameAndDescription()
    {
        using var context = TestDatabase.CreateContext();
        var moduleId = await CreateTestModuleAsync(context, "ScreenServiceRenameTestApp", "Payments");
        var repository = new ScreenServiceRepository(context);
        var created = await repository.AddAsync(new ScreenService { ModuleId = moduleId, Name = "OldName", Type = ScreenServiceType.Screen });

        var renamed = await repository.RenameAsync(created.Id, "NewName", "new description");

        Assert.Equal("NewName", renamed.Name);
        Assert.Equal("new description", renamed.Description);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var moduleId = await CreateTestModuleAsync(context, "ScreenServiceDeactivateTestApp", "Payments");
        var repository = new ScreenServiceRepository(context);
        var created = await repository.AddAsync(new ScreenService { ModuleId = moduleId, Name = "ToDeactivate", Type = ScreenServiceType.Screen });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateNameFailure_SubsequentUniqueNameStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var moduleId = await CreateTestModuleAsync(context, "ScreenServiceCircuitTestApp", "Payments");
        var repository = new ScreenServiceRepository(context);

        await repository.AddAsync(new ScreenService { ModuleId = moduleId, Name = "DupService", Type = ScreenServiceType.Service });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new ScreenService { ModuleId = moduleId, Name = "DupService", Type = ScreenServiceType.Service }));

        var created = await repository.AddAsync(new ScreenService { ModuleId = moduleId, Name = "UniqueService", Type = ScreenServiceType.Service });

        Assert.Equal("UniqueService", created.Name);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ScreenServiceRepositoryTests`
Expected: FAIL — `ScreenServiceRepository` does not exist yet.

- [ ] **Step 3: Implement `ScreenServiceRepository`**

```csharp
// src/LogsPlatform.Infrastructure/Repositories/ScreenServiceRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ScreenServiceRepository : IScreenServiceRepository
{
    private readonly LogsPlatformDbContext _context;

    public ScreenServiceRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<ScreenService?> GetByIdAsync(int id) =>
        await _context.ScreenServices.FindAsync(id);

    public async Task<IReadOnlyList<ScreenService>> GetByModuleIdAsync(int moduleId, bool includeInactive = false)
    {
        var query = _context.ScreenServices.AsNoTracking().Where(s => s.ModuleId == moduleId);
        if (!includeInactive)
        {
            query = query.Where(s => s.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<ScreenService> AddAsync(ScreenService screenService)
    {
        _context.ScreenServices.Add(screenService);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(screenService).State = EntityState.Detached;
            throw;
        }
        return screenService;
    }

    public async Task<ScreenService> RenameAsync(int id, string name, string? description)
    {
        var screenService = await _context.ScreenServices.FindAsync(id)
            ?? throw new InvalidOperationException($"ScreenService {id} not found.");
        screenService.Name = name;
        screenService.Description = description;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(screenService).State = EntityState.Detached;
            throw;
        }
        return screenService;
    }

    public async Task DeactivateAsync(int id)
    {
        var screenService = await _context.ScreenServices.FindAsync(id)
            ?? throw new InvalidOperationException($"ScreenService {id} not found.");
        screenService.IsActive = false;
        await _context.SaveChangesAsync();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ScreenServiceRepositoryTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 21, Skipped: 0, Total: 21` (11 from Tasks 1-2 + 5 from Task 3 + 5 from this task).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Infrastructure/Repositories/ScreenServiceRepository.cs tests/LogsPlatform.Tests/Infrastructure/ScreenServiceRepositoryTests.cs
git commit -m "Implement ScreenServiceRepository with proactive detach-on-failure handling"
```

---

### Task 5: Wire up DI registrations in `Program.cs`

**Files:**
- Modify: `src/LogsPlatform.Web/Program.cs`

**Interfaces:**
- Consumes: `IModuleRepository`/`ModuleRepository`, `IScreenServiceRepository`/`ScreenServiceRepository` (Tasks 1/3/4).
- Produces: DI registrations that make Task 6/7's controllers (and their `WebApplicationFactory` tests) resolvable — same reasoning as the prior plan's Task 5 (DI wiring must precede the controllers that need it).

- [ ] **Step 1: Add the two new DI registrations**

Modify `Program.cs` — add these two lines directly after the existing `AddScoped<IAppEnvironmentRepository, AppEnvironmentRepository>();` line (do not reorder or remove anything else):

```csharp
builder.Services.AddScoped<IModuleRepository, ModuleRepository>();
builder.Services.AddScoped<IScreenServiceRepository, ScreenServiceRepository>();
```

The full `Program.cs` after this change:

```csharp
// src/LogsPlatform.Web/Program.cs — full file content
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Web.Components;
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
builder.Services.AddScoped<IModuleRepository, ModuleRepository>();
builder.Services.AddScoped<IScreenServiceRepository, ScreenServiceRepository>();

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
Expected: `Passed! - Failed: 0, Passed: 21, Skipped: 0, Total: 21` — unchanged (no controller tests exist yet for the new entities — those are Tasks 6-7).

- [ ] **Step 4: Commit**

```bash
git add src/LogsPlatform.Web/Program.cs
git commit -m "Wire up DI for ModuleRepository and ScreenServiceRepository"
```

---

### Task 6: `ModulesController` + tests

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/ModuleContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/ModulesController.cs`
- Create: `tests/LogsPlatform.Tests/Web/ModulesControllerTests.cs`

**Interfaces:**
- Consumes: `IModuleRepository` (Task 1/3), DI wiring (Task 5).
- Produces: `POST/GET/PUT/DELETE /api/v1/admin/applications/{appId}/modules[/{id}]`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/ModulesControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ModulesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ModulesControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<int> CreateApplicationAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications",
            new CreateApplicationRequest(name, null));
        var created = await response.Content.ReadFromJsonAsync<ApplicationResponse>();
        return created!.Id;
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsModule()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "ModuleControllerTestApp1");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/modules",
            new CreateModuleRequest("Payments", "Payment handling"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ModuleResponse>();
        Assert.NotNull(created);
        Assert.Equal("Payments", created!.Name);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/modules/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "ModuleControllerTestApp2");
        var request = new CreateModuleRequest("DuplicateModule", null);

        var first = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/modules", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/modules", request);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_ModuleBelongingToDifferentApplication_Returns404()
    {
        // IDOR guard: a valid module ID under the WRONG appId in the route must 404, not leak data.
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "ModuleIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "ModuleIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/modules",
            new CreateModuleRequest("BelongsToApp1", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var crossAppGet = await client.GetAsync($"/api/v1/admin/applications/{appId2}/modules/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossAppGet.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesNameAndDescription()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "ModuleRenameControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/modules",
            new CreateModuleRequest("OriginalName", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/modules/{created!.Id}",
            new RenameModuleRequest("RenamedModule", "updated"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<ModuleResponse>();
        Assert.Equal("RenamedModule", renamed!.Name);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "ModuleDeactivateControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/modules",
            new CreateModuleRequest("ToDeactivate", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/modules/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<ModuleResponse>>($"/api/v1/admin/applications/{appId}/modules");
        Assert.DoesNotContain(listResponse!, m => m.Id == created.Id);

        var listWithInactive = await client.GetFromJsonAsync<List<ModuleResponse>>(
            $"/api/v1/admin/applications/{appId}/modules?includeInactive=true");
        Assert.Contains(listWithInactive!, m => m.Id == created.Id);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ModulesControllerTests`
Expected: FAIL — `CreateModuleRequest`/`ModuleResponse`/`ModulesController` do not exist yet.

- [ ] **Step 3: Write the request/response contracts**

```csharp
// src/LogsPlatform.Web/Contracts/ModuleContracts.cs
namespace LogsPlatform.Web.Contracts;

public record CreateModuleRequest(string Name, string? Description);

public record ModuleResponse(int Id, int ApplicationId, string Name, string? Description, bool IsActive);

public record RenameModuleRequest(string Name, string? Description);
```

- [ ] **Step 4: Write `ModulesController`**

```csharp
// src/LogsPlatform.Web/Controllers/ModulesController.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/modules")]
public class ModulesController : ControllerBase
{
    private readonly IModuleRepository _modules;

    public ModulesController(IModuleRepository modules)
    {
        _modules = modules;
    }

    [HttpPost]
    public async Task<ActionResult<ModuleResponse>> Create(int appId, CreateModuleRequest request)
    {
        try
        {
            var module = await _modules.AddAsync(new AppModule
            {
                ApplicationId = appId,
                Name = request.Name,
                Description = request.Description
            });

            return CreatedAtAction(nameof(GetById), new { appId, id = module.Id }, ToResponse(module));
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
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
            return ToResponse(module);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
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
        return NoContent();
    }

    private static ModuleResponse ToResponse(AppModule module) =>
        new(module.Id, module.ApplicationId, module.Name, module.Description, module.IsActive);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter ModulesControllerTests`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/ModuleContracts.cs src/LogsPlatform.Web/Controllers/ModulesController.cs tests/LogsPlatform.Tests/Web/ModulesControllerTests.cs
git commit -m "Add Modules admin API controller"
```

---

### Task 7: `ScreenServicesController` + tests

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/ScreenServiceContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/ScreenServicesController.cs`
- Create: `tests/LogsPlatform.Tests/Web/ScreenServicesControllerTests.cs`

**Interfaces:**
- Consumes: `IScreenServiceRepository` (Task 1/4), `IModuleRepository` (Task 1/3, for a helper that creates a test module via the API), DI wiring (Task 5).
- Produces: `POST/GET/PUT/DELETE /api/v1/admin/modules/{moduleId}/screen-services[/{id}]` — the last piece of this plan.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/ScreenServicesControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ScreenServicesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ScreenServicesControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<int> CreateModuleAsync(HttpClient client, string appName, string moduleName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var moduleResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{app!.Id}/modules",
            new CreateModuleRequest(moduleName, null));
        var module = await moduleResponse.Content.ReadFromJsonAsync<ModuleResponse>();
        return module!.Id;
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsScreenService()
    {
        var client = _factory.CreateClient();
        var moduleId = await CreateModuleAsync(client, "ScreenServiceControllerTestApp1", "Payments");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/modules/{moduleId}/screen-services",
            new CreateScreenServiceRequest("PaymentGateway", "Service", "Handles payment calls"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();
        Assert.NotNull(created);
        Assert.Equal("PaymentGateway", created!.Name);
        Assert.Equal("Service", created.Type);

        var getResponse = await client.GetAsync($"/api/v1/admin/modules/{moduleId}/screen-services/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var moduleId = await CreateModuleAsync(client, "ScreenServiceControllerTestApp2", "Payments");
        var request = new CreateScreenServiceRequest("DuplicateService", "Screen", null);

        var first = await client.PostAsJsonAsync($"/api/v1/admin/modules/{moduleId}/screen-services", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/v1/admin/modules/{moduleId}/screen-services", request);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_ScreenServiceBelongingToDifferentModule_Returns404()
    {
        var client = _factory.CreateClient();
        var moduleId1 = await CreateModuleAsync(client, "ScreenServiceIdorTestApp1", "ModuleA");
        var moduleId2 = await CreateModuleAsync(client, "ScreenServiceIdorTestApp2", "ModuleB");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/modules/{moduleId1}/screen-services",
            new CreateScreenServiceRequest("BelongsToModule1", "Screen", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();

        var crossModuleGet = await client.GetAsync($"/api/v1/admin/modules/{moduleId2}/screen-services/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossModuleGet.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesNameAndDescription()
    {
        var client = _factory.CreateClient();
        var moduleId = await CreateModuleAsync(client, "ScreenServiceRenameControllerTestApp", "Payments");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/modules/{moduleId}/screen-services",
            new CreateScreenServiceRequest("OriginalName", "Screen", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/modules/{moduleId}/screen-services/{created!.Id}",
            new RenameScreenServiceRequest("RenamedService", "updated"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();
        Assert.Equal("RenamedService", renamed!.Name);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var moduleId = await CreateModuleAsync(client, "ScreenServiceDeactivateControllerTestApp", "Payments");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/modules/{moduleId}/screen-services",
            new CreateScreenServiceRequest("ToDeactivate", "Screen", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/modules/{moduleId}/screen-services/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<ScreenServiceResponse>>($"/api/v1/admin/modules/{moduleId}/screen-services");
        Assert.DoesNotContain(listResponse!, s => s.Id == created.Id);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ScreenServicesControllerTests`
Expected: FAIL — `CreateScreenServiceRequest`/`ScreenServiceResponse`/`ScreenServicesController` do not exist yet.

- [ ] **Step 3: Write the request/response contracts**

`Type` is exposed as a `string` on the wire (`"Screen"`/`"Service"`), converted to/from the `ScreenServiceType` enum in the controller — keeps the JSON contract readable without requiring API consumers to know the underlying enum's numeric values:

```csharp
// src/LogsPlatform.Web/Contracts/ScreenServiceContracts.cs
namespace LogsPlatform.Web.Contracts;

public record CreateScreenServiceRequest(string Name, string Type, string? Description);

public record ScreenServiceResponse(int Id, int ModuleId, string Name, string Type, string? Description, bool IsActive);

public record RenameScreenServiceRequest(string Name, string? Description);
```

- [ ] **Step 4: Write `ScreenServicesController`**

```csharp
// src/LogsPlatform.Web/Controllers/ScreenServicesController.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/modules/{moduleId:int}/screen-services")]
public class ScreenServicesController : ControllerBase
{
    private readonly IScreenServiceRepository _screenServices;

    public ScreenServicesController(IScreenServiceRepository screenServices)
    {
        _screenServices = screenServices;
    }

    [HttpPost]
    public async Task<ActionResult<ScreenServiceResponse>> Create(int moduleId, CreateScreenServiceRequest request)
    {
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

            return CreatedAtAction(nameof(GetById), new { moduleId, id = screenService.Id }, ToResponse(screenService));
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
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
            return ToResponse(screenService);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
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
        return NoContent();
    }

    private static ScreenServiceResponse ToResponse(ScreenService screenService) =>
        new(screenService.Id, screenService.ModuleId, screenService.Name, screenService.Type.ToString(), screenService.Description, screenService.IsActive);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter ScreenServicesControllerTests`
Expected: PASS (5 tests).

- [ ] **Step 6: Run the full test suite one more time**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 31, Skipped: 0, Total: 31` (21 from Tasks 1-5 + 5 from Task 6 + 5 from this task).

- [ ] **Step 7: Manual end-to-end smoke check**

```bash
dotnet run --project src/LogsPlatform.Web --launch-profile http &
sleep 5
curl -s -i -X POST http://localhost:5201/api/v1/admin/applications \
  -H "Content-Type: application/json" -d '{"name":"SmokeTestApp","description":null}'
```
Note the returned `id`, then:
```bash
curl -s -i -X POST http://localhost:5201/api/v1/admin/applications/<id>/modules \
  -H "Content-Type: application/json" -d '{"name":"SmokeModule","description":null}'
```
Note the returned module `id`, then:
```bash
curl -s -i -X POST http://localhost:5201/api/v1/admin/modules/<moduleId>/screen-services \
  -H "Content-Type: application/json" -d '{"name":"SmokeScreenService","type":"Screen","description":null}'
```
Expected: three `201 Created` responses, each JSON body containing the expected fields. Stop the background process afterward with `taskkill //F //IM dotnet.exe`.

- [ ] **Step 8: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/ScreenServiceContracts.cs src/LogsPlatform.Web/Controllers/ScreenServicesController.cs tests/LogsPlatform.Tests/Web/ScreenServicesControllerTests.cs
git commit -m "Add ScreenServices admin API controller"
```

---

## Self-Review Notes

- **Spec coverage:** Every element of `docs/superpowers/specs/2026-08-18-application-hierarchy-spine-design.md`'s "API Shape", "Deactivate Semantics", "Repository Interfaces", and "Naming Correction" sections is implemented across Tasks 1-7. UI (design doc's "A3") is explicitly out of scope for this plan — it depends on this plan's completed API and is planned separately.
- **Type consistency:** `IModuleRepository`/`IScreenServiceRepository` signatures from Task 1 are used identically by `ModuleRepository`/`ScreenServiceRepository` (Tasks 3-4) and consumed identically by `ModulesController`/`ScreenServicesController` (Tasks 6-7) — verified by re-reading each task above. `ScreenServiceType` enum values (`Screen`, `Service`) are used consistently in tests, the controller's `Enum.TryParse`, and the entity.
- **Global Constraints applied, not just stated:** detach-on-failure appears in every `AddAsync`/`RenameAsync` from Task 3 onward (not retrofitted later); every `GetById`/`Rename`/`Deactivate` controller action checks the parent-id match before acting (IDOR guard), with a dedicated test for it in both controller test files.
- **No placeholders:** every step has complete, runnable code or an exact command with an expected result, including the running test-count expectations at each stage (11 → 21 → 21 → 31), so a deviation is immediately visible.

## After This Plan

Plan A2 (`ProcessNode` + `Operation`, same shape, should move faster with this pattern proven) is next, then Plan A3 (UI drill-down across all four levels). See `docs/superpowers/specs/2026-08-18-application-hierarchy-spine-design.md` for the full three-plan execution split.
