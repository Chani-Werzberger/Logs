# Hierarchy Spine A2: ProcessNode + Operation Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Full backend (entities, EF Core mapping, repositories, Admin API) for the last two levels of the Application hierarchy spine — `ProcessNode` (child of `ScreenService`) and `Operation` (child of `ProcessNode`) — with Create/List/GetById/Rename/Deactivate, matching the shape specified in `docs/superpowers/specs/2026-08-18-application-hierarchy-spine-design.md`. This completes the full 4-level hierarchy chain (`Application → AppModule → ScreenService → ProcessNode → Operation`) at the backend level; UI drill-down (A3) is separate, later work.

**Architecture:** Identical Modular Monolith layering to A1 (`LogsPlatform.Domain` entities/interfaces → `LogsPlatform.Infrastructure` EF Core/repositories → `LogsPlatform.Web` controllers). This plan replicates A1's now-proven pattern for the remaining two entities. Per `05-מודל-נתונים.md` §2, both `ProcessNode` and `Operation` have the exact same shape as `AppModule` (no extra discriminator field like `ScreenService.Type`).

**Tech Stack:** Same as the current solution — .NET 10, EF Core 10.0.11, SQL Server, xUnit + `Microsoft.AspNetCore.Mvc.Testing`. No new packages.

## Global Constraints

- **Naming — FK/nav property prefix follows the prosaic form, entity type follows the BCL-safe form.** This is the established, verified convention from the already-merged code: `ScreenService.ModuleId` (FK) / `ScreenService.Module` (nav, typed `AppModule`) — the property is named after the prosaic "Module", the declared type is the BCL-safe `AppModule`. `05-מודל-נתונים.md` §2 explicitly specifies `Operation`'s FK column as `ProcessId` (not `ProcessNodeId`), confirming the same rule applies here: `Operation.ProcessId` (FK) / `Operation.Process` (nav, typed `ProcessNode`). Collection navigation properties follow the same rule: `ScreenService.Processes` (not `ProcessNodes`), mirroring the already-merged `Application.Modules` (not `AppModules`). `ProcessNode.Operations` matches the entity name directly since `Operation` has no prosaic-vs-type-name split, mirroring `AppModule.ScreenServices`.
- **Repository interface/class names mirror the entity name exactly:** `IProcessNodeRepository`/`ProcessNodeRepository`, `IOperationRepository`/`OperationRepository` — this is the corrected convention from A1 (after fixing `IModuleRepository` → `IAppModuleRepository`). Do not abbreviate.
- **Routes use the prosaic short form, scoped by immediate parent only:** `ProcessNode` under `/api/v1/admin/screen-services/{screenServiceId}/processes`, `Operation` under `/api/v1/admin/processes/{processId}/operations` — per the design doc's explicit route table. Controller class names follow the route segment (`ProcessesController`, `OperationsController`), matching the established `ModulesController`/`ScreenServicesController`/`EnvironmentsController` precedent (controller name = route noun, not necessarily the full entity type name).
- **Detach-on-failure in every `AddAsync`/`RenameAsync`** — `try`/`catch`, `_context.Entry(entity).State = EntityState.Detached`, re-throw. Non-negotiable, present from this plan's first draft.
- **IDOR protection on every child-by-id endpoint:** `GetById`/`Rename`/`Deactivate` must verify the loaded entity's parent-FK equals the parent id in the route, returning `404` if it doesn't.
- **Parent-existence guard on every `Create` — learned the hard way in A1's final review, must be present from this plan's first draft, not discovered later.** A1 shipped without this and its `Create` actions 500'd (unhandled FK-violation `SqlException` 547) instead of 404ing when given a nonexistent parent id — found only in the final whole-branch review and fixed afterward. This plan bakes the fix in from Task 6/7's first draft: `ProcessesController.Create` must check `screenServiceId` exists via `IScreenServiceRepository.GetByIdAsync` before inserting; `OperationsController.Create` must check `processId` exists via `IProcessNodeRepository.GetByIdAsync` before inserting. Both return `404 NotFound` with a message if the parent doesn't exist — exact pattern already shipped in `src/LogsPlatform.Web/Controllers/ModulesController.cs:22-28` and `ScreenServicesController.cs:22-28`.
- **Reuse the existing `DbUpdateExceptionExtensions.IsUniqueViolation()` helper (`src/LogsPlatform.Web/DbUpdateExceptionExtensions.cs`) — do not reintroduce an inline `ex.InnerException is SqlException { Number: 2601 or 2627 }` check.** This helper was extracted in A1's final-review fix specifically so a third and fourth copy wouldn't appear in A2. Use `catch (DbUpdateException ex) when (ex.IsUniqueViolation())`.
- **Controller-level Rename tests must prove real persistence via a genuine follow-up GET (a separate HTTP request, hence a fresh request-scoped `DbContext`) — not just assert the PUT's own response body.** A1's original Rename tests only checked the mutated in-memory response and would have passed even if `SaveChangesAsync` were silently deleted from `RenameAsync` — found in A1's final review. This plan's Task 6/7 tests must include the follow-up-GET pattern from the first draft (see `tests/LogsPlatform.Tests/Web/ModulesControllerTests.cs:78-98`'s `Rename_UpdatesNameAndDescription` for the exact shape to replicate).
- **Rename-into-a-duplicate-name test coverage is required from the first draft, at both repository and controller level** — proving detach-on-failure actually applies to `RenameAsync`, not just `AddAsync`. A1 shipped without this and it was found untested in the final review. See `AppModuleRepositoryTests.cs:104-117` (`RenameAsync_ToExistingSiblingName_ThrowsAndSubsequentWriteStillSucceeds`) and `ModulesControllerTests.cs:101-114` (`Rename_DuplicateName_Returns409Conflict`) for the exact shape to replicate.
- **Soft-delete only — no hard-delete anywhere in this plan.** `DeactivateAsync` always sets `IsActive = false`.
- **Name uniqueness is scoped to the immediate parent:** `ProcessNode.Name` unique within `ScreenServiceId`; `Operation.Name` unique within `ProcessId`. Both enforced via a composite unique index.
- **`.superpowers/` is gitignored project-wide as of A1's merge — do not `git add` anything under it in this plan's commits.** The progress ledger and task briefs/reports are local scratch only; this was fixed in A1 specifically so it wouldn't need re-fixing here.
- Target framework `net10.0`, EF Core packages pinned at `10.0.11` everywhere (already the case — this plan adds no new package references).

---

### Task 1: Domain entities (`ProcessNode`, `Operation`) + repository interfaces

**Files:**
- Create: `src/LogsPlatform.Domain/Entities/ProcessNode.cs`
- Create: `src/LogsPlatform.Domain/Entities/Operation.cs`
- Modify: `src/LogsPlatform.Domain/Entities/ScreenService.cs` (add `Processes` navigation collection)
- Create: `src/LogsPlatform.Domain/Repositories/IProcessNodeRepository.cs`
- Create: `src/LogsPlatform.Domain/Repositories/IOperationRepository.cs`

**Interfaces:**
- Consumes: `ScreenService` entity (existing, from A1).
- Produces: `ProcessNode`, `Operation` entity classes and `IProcessNodeRepository`, `IOperationRepository` interfaces that Task 3/4 implement against.

- [ ] **Step 1: Write the `ProcessNode` entity**

```csharp
// src/LogsPlatform.Domain/Entities/ProcessNode.cs
namespace LogsPlatform.Domain.Entities;

public class ProcessNode
{
    public int Id { get; set; }
    public int ScreenServiceId { get; set; }
    public ScreenService ScreenService { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<Operation> Operations { get; set; } = new List<Operation>();
}
```

- [ ] **Step 2: Write the `Operation` entity**

```csharp
// src/LogsPlatform.Domain/Entities/Operation.cs
namespace LogsPlatform.Domain.Entities;

public class Operation
{
    public int Id { get; set; }
    public int ProcessId { get; set; }
    public ProcessNode Process { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}
```

- [ ] **Step 3: Add the `Processes` navigation collection to `ScreenService`**

```csharp
// src/LogsPlatform.Domain/Entities/ScreenService.cs — full file content
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

    public ICollection<ProcessNode> Processes { get; set; } = new List<ProcessNode>();
}
```

- [ ] **Step 4: Write the repository interfaces**

```csharp
// src/LogsPlatform.Domain/Repositories/IProcessNodeRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IProcessNodeRepository
{
    Task<ProcessNode?> GetByIdAsync(int id);
    Task<IReadOnlyList<ProcessNode>> GetByScreenServiceIdAsync(int screenServiceId, bool includeInactive = false);
    Task<ProcessNode> AddAsync(ProcessNode process);
    Task<ProcessNode> RenameAsync(int id, string name, string? description);
    Task DeactivateAsync(int id);
}
```

```csharp
// src/LogsPlatform.Domain/Repositories/IOperationRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IOperationRepository
{
    Task<Operation?> GetByIdAsync(int id);
    Task<IReadOnlyList<Operation>> GetByProcessIdAsync(int processId, bool includeInactive = false);
    Task<Operation> AddAsync(Operation operation);
    Task<Operation> RenameAsync(int id, string name, string? description);
    Task DeactivateAsync(int id);
}
```

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Domain/Entities/ProcessNode.cs src/LogsPlatform.Domain/Entities/Operation.cs src/LogsPlatform.Domain/Entities/ScreenService.cs src/LogsPlatform.Domain/Repositories/IProcessNodeRepository.cs src/LogsPlatform.Domain/Repositories/IOperationRepository.cs
git commit -m "Add ProcessNode and Operation domain entities + repository interfaces"
```

---

### Task 2: `LogsPlatformDbContext` mapping + migration

**Files:**
- Modify: `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs`

**Interfaces:**
- Consumes: `ProcessNode`, `Operation` from Task 1.
- Produces: `DbSet<ProcessNode> Processes`, `DbSet<Operation> Operations` on `LogsPlatformDbContext`, plus the migration that creates their tables — used by Task 3/4's repositories.

- [ ] **Step 1: Write the failing test**

```csharp
// Add to tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs
[Fact]
public async Task CanInsertAndRetrieveProcessNodeWithOperation()
{
    using var context = TestDatabase.CreateContext();

    var application = new Application { Name = "HierarchyDbContextTestApp2", CreatedAt = DateTime.UtcNow };
    var module = new AppModule { Name = "Payments" };
    var screenService = new ScreenService { Name = "PaymentGateway", Type = ScreenServiceType.Service };
    var process = new ProcessNode { Name = "ChargeCard" };
    process.Operations.Add(new Operation { Name = "AuthorizePayment" });
    screenService.Processes.Add(process);
    module.ScreenServices.Add(screenService);
    application.Modules.Add(module);

    context.Applications.Add(application);
    await context.SaveChangesAsync();

    using var readContext = new LogsPlatformDbContext(
        new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    var loaded = await readContext.Processes
        .Include(p => p.Operations)
        .FirstAsync(p => p.Name == "ChargeCard");

    Assert.True(loaded.IsActive);
    Assert.Single(loaded.Operations);
    Assert.Equal("AuthorizePayment", loaded.Operations.First().Name);
    Assert.True(loaded.Operations.First().IsActive);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter CanInsertAndRetrieveProcessNodeWithOperation`
Expected: FAIL — build error, `context.Processes`/`DbSet<ProcessNode>` does not exist yet.

- [ ] **Step 3: Add the `DbSet`s and `OnModelCreating` configuration**

Modify `LogsPlatformDbContext.cs` — add two `DbSet` properties and extend `OnModelCreating` (do not remove any existing configuration block, including `ConfigureConventions`'s `UtcDateTimeConverter` wiring and the `Application`/`AppEnvironment`/`AppModule`/`ScreenService` blocks — only add to them):

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
    }
}
```

- [ ] **Step 4: Generate the migration**

This is an **additive** migration — no existing migration's history may be touched or regenerated:

```bash
dotnet ef migrations add AddProcessNodeAndOperation \
  --project src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj \
  --startup-project src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj
```

Expected: creates `src/LogsPlatform.Infrastructure/Migrations/<timestamp>_AddProcessNodeAndOperation.cs` and updates `LogsPlatformDbContextModelSnapshot.cs` — creating two new tables (`Processes`, `Operations`) with the FK/cascade/unique-index shape from Step 3. Verify the generated migration's `Up()` only adds the two new tables and their indexes — it must not contain any `DropTable`/`DropColumn` against any existing table (`Applications`, `AppEnvironments`, `Modules`, `ScreenServices`) or their existing indexes. If it does, something in Step 3 changed the existing model unintentionally — STOP and investigate before proceeding.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter CanInsertAndRetrieveProcessNodeWithOperation`
Expected: PASS.

- [ ] **Step 6: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 38, Skipped: 0, Total: 38` (the 37 tests that exist on `main` before this task, plus this task's new one).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs src/LogsPlatform.Infrastructure/Migrations/ tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs
git commit -m "Add ProcessNode and Operation EF Core mapping + migration"
```

---

### Task 3: `ProcessNodeRepository` implementation + tests

**Files:**
- Create: `src/LogsPlatform.Infrastructure/Repositories/ProcessNodeRepository.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/ProcessNodeRepositoryTests.cs`

**Interfaces:**
- Consumes: `IProcessNodeRepository` (Task 1), `LogsPlatformDbContext` (Task 2).
- Produces: `ProcessNodeRepository` — registered in DI by Task 5, consumed by Task 6's controller.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/ProcessNodeRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ProcessNodeRepositoryTests
{
    private static async Task<int> CreateTestScreenServiceAsync(LogsPlatformDbContext context, string appName, string moduleName, string screenServiceName)
    {
        var application = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        var module = new AppModule { Name = moduleName };
        var screenService = new ScreenService { Name = screenServiceName, Type = ScreenServiceType.Service };
        module.ScreenServices.Add(screenService);
        application.Modules.Add(module);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return screenService.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsProcessNode_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var screenServiceId = await CreateTestScreenServiceAsync(context, "ProcessAddTestApp", "Payments", "PaymentGateway");
        var repository = new ProcessNodeRepository(context);

        var created = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "ChargeCard" });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("ChargeCard", loaded!.Name);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByScreenServiceIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var screenServiceId = await CreateTestScreenServiceAsync(context, "ProcessFilterTestApp", "Payments", "PaymentGateway");
        var repository = new ProcessNodeRepository(context);

        var active = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "Active" });
        var toDeactivate = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "WillBeInactive" });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByScreenServiceIdAsync(screenServiceId);
        var withInactive = await repository.GetByScreenServiceIdAsync(screenServiceId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesNameAndDescription()
    {
        using var context = TestDatabase.CreateContext();
        var screenServiceId = await CreateTestScreenServiceAsync(context, "ProcessRenameTestApp", "Payments", "PaymentGateway");
        var repository = new ProcessNodeRepository(context);
        var created = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "OldName" });

        var renamed = await repository.RenameAsync(created.Id, "NewName", "new description");

        Assert.Equal("NewName", renamed.Name);
        Assert.Equal("new description", renamed.Description);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var screenServiceId = await CreateTestScreenServiceAsync(context, "ProcessDeactivateTestApp", "Payments", "PaymentGateway");
        var repository = new ProcessNodeRepository(context);
        var created = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "ToDeactivate" });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateNameFailure_SubsequentUniqueNameStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var screenServiceId = await CreateTestScreenServiceAsync(context, "ProcessCircuitTestApp", "Payments", "PaymentGateway");
        var repository = new ProcessNodeRepository(context);

        await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "DupProcess" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "DupProcess" }));

        var created = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "UniqueProcess" });

        Assert.Equal("UniqueProcess", created.Name);
    }

    [Fact]
    public async Task RenameAsync_ToExistingSiblingName_ThrowsAndSubsequentWriteStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var screenServiceId = await CreateTestScreenServiceAsync(context, "ProcessRenameConflictTestApp", "Payments", "PaymentGateway");
        var repository = new ProcessNodeRepository(context);
        await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "Taken" });
        var toRename = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "ToRename" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.RenameAsync(toRename.Id, "Taken", null));

        var created = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "StillWorks" });
        Assert.Equal("StillWorks", created.Name);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ProcessNodeRepositoryTests`
Expected: FAIL — `ProcessNodeRepository` does not exist yet.

- [ ] **Step 3: Implement `ProcessNodeRepository`**

```csharp
// src/LogsPlatform.Infrastructure/Repositories/ProcessNodeRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ProcessNodeRepository : IProcessNodeRepository
{
    private readonly LogsPlatformDbContext _context;

    public ProcessNodeRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<ProcessNode?> GetByIdAsync(int id) =>
        await _context.Processes.FindAsync(id);

    public async Task<IReadOnlyList<ProcessNode>> GetByScreenServiceIdAsync(int screenServiceId, bool includeInactive = false)
    {
        var query = _context.Processes.AsNoTracking().Where(p => p.ScreenServiceId == screenServiceId);
        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<ProcessNode> AddAsync(ProcessNode process)
    {
        _context.Processes.Add(process);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(process).State = EntityState.Detached;
            throw;
        }
        return process;
    }

    public async Task<ProcessNode> RenameAsync(int id, string name, string? description)
    {
        var process = await _context.Processes.FindAsync(id)
            ?? throw new InvalidOperationException($"ProcessNode {id} not found.");
        process.Name = name;
        process.Description = description;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(process).State = EntityState.Detached;
            throw;
        }
        return process;
    }

    public async Task DeactivateAsync(int id)
    {
        var process = await _context.Processes.FindAsync(id)
            ?? throw new InvalidOperationException($"ProcessNode {id} not found.");
        process.IsActive = false;
        await _context.SaveChangesAsync();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ProcessNodeRepositoryTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Infrastructure/Repositories/ProcessNodeRepository.cs tests/LogsPlatform.Tests/Infrastructure/ProcessNodeRepositoryTests.cs
git commit -m "Implement ProcessNodeRepository with detach-on-failure handling"
```

---

### Task 4: `OperationRepository` implementation + tests

**Files:**
- Create: `src/LogsPlatform.Infrastructure/Repositories/OperationRepository.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/OperationRepositoryTests.cs`

**Interfaces:**
- Consumes: `IOperationRepository` (Task 1), `LogsPlatformDbContext` (Task 2), `ProcessNodeRepository` pattern (Task 3, for the parallel test-helper shape).
- Produces: `OperationRepository` — registered in DI by Task 5, consumed by Task 7's controller.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/OperationRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class OperationRepositoryTests
{
    private static async Task<int> CreateTestProcessNodeAsync(LogsPlatformDbContext context, string appName, string moduleName, string screenServiceName, string processName)
    {
        var application = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        var module = new AppModule { Name = moduleName };
        var screenService = new ScreenService { Name = screenServiceName, Type = ScreenServiceType.Service };
        var process = new ProcessNode { Name = processName };
        screenService.Processes.Add(process);
        module.ScreenServices.Add(screenService);
        application.Modules.Add(module);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return process.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsOperation_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var processId = await CreateTestProcessNodeAsync(context, "OperationAddTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var repository = new OperationRepository(context);

        var created = await repository.AddAsync(new Operation { ProcessId = processId, Name = "AuthorizePayment" });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("AuthorizePayment", loaded!.Name);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByProcessIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var processId = await CreateTestProcessNodeAsync(context, "OperationFilterTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var repository = new OperationRepository(context);

        var active = await repository.AddAsync(new Operation { ProcessId = processId, Name = "Active" });
        var toDeactivate = await repository.AddAsync(new Operation { ProcessId = processId, Name = "WillBeInactive" });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByProcessIdAsync(processId);
        var withInactive = await repository.GetByProcessIdAsync(processId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesNameAndDescription()
    {
        using var context = TestDatabase.CreateContext();
        var processId = await CreateTestProcessNodeAsync(context, "OperationRenameTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var repository = new OperationRepository(context);
        var created = await repository.AddAsync(new Operation { ProcessId = processId, Name = "OldName" });

        var renamed = await repository.RenameAsync(created.Id, "NewName", "new description");

        Assert.Equal("NewName", renamed.Name);
        Assert.Equal("new description", renamed.Description);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var processId = await CreateTestProcessNodeAsync(context, "OperationDeactivateTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var repository = new OperationRepository(context);
        var created = await repository.AddAsync(new Operation { ProcessId = processId, Name = "ToDeactivate" });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateNameFailure_SubsequentUniqueNameStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var processId = await CreateTestProcessNodeAsync(context, "OperationCircuitTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var repository = new OperationRepository(context);

        await repository.AddAsync(new Operation { ProcessId = processId, Name = "DupOperation" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new Operation { ProcessId = processId, Name = "DupOperation" }));

        var created = await repository.AddAsync(new Operation { ProcessId = processId, Name = "UniqueOperation" });

        Assert.Equal("UniqueOperation", created.Name);
    }

    [Fact]
    public async Task RenameAsync_ToExistingSiblingName_ThrowsAndSubsequentWriteStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var processId = await CreateTestProcessNodeAsync(context, "OperationRenameConflictTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var repository = new OperationRepository(context);
        await repository.AddAsync(new Operation { ProcessId = processId, Name = "Taken" });
        var toRename = await repository.AddAsync(new Operation { ProcessId = processId, Name = "ToRename" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.RenameAsync(toRename.Id, "Taken", null));

        var created = await repository.AddAsync(new Operation { ProcessId = processId, Name = "StillWorks" });
        Assert.Equal("StillWorks", created.Name);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter OperationRepositoryTests`
Expected: FAIL — `OperationRepository` does not exist yet.

- [ ] **Step 3: Implement `OperationRepository`**

```csharp
// src/LogsPlatform.Infrastructure/Repositories/OperationRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class OperationRepository : IOperationRepository
{
    private readonly LogsPlatformDbContext _context;

    public OperationRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<Operation?> GetByIdAsync(int id) =>
        await _context.Operations.FindAsync(id);

    public async Task<IReadOnlyList<Operation>> GetByProcessIdAsync(int processId, bool includeInactive = false)
    {
        var query = _context.Operations.AsNoTracking().Where(o => o.ProcessId == processId);
        if (!includeInactive)
        {
            query = query.Where(o => o.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<Operation> AddAsync(Operation operation)
    {
        _context.Operations.Add(operation);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(operation).State = EntityState.Detached;
            throw;
        }
        return operation;
    }

    public async Task<Operation> RenameAsync(int id, string name, string? description)
    {
        var operation = await _context.Operations.FindAsync(id)
            ?? throw new InvalidOperationException($"Operation {id} not found.");
        operation.Name = name;
        operation.Description = description;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(operation).State = EntityState.Detached;
            throw;
        }
        return operation;
    }

    public async Task DeactivateAsync(int id)
    {
        var operation = await _context.Operations.FindAsync(id)
            ?? throw new InvalidOperationException($"Operation {id} not found.");
        operation.IsActive = false;
        await _context.SaveChangesAsync();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter OperationRepositoryTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 50, Skipped: 0, Total: 50` (38 from Tasks 1-2 + 6 from Task 3 + 6 from this task).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Infrastructure/Repositories/OperationRepository.cs tests/LogsPlatform.Tests/Infrastructure/OperationRepositoryTests.cs
git commit -m "Implement OperationRepository with detach-on-failure handling"
```

---

### Task 5: Wire up DI registrations in `Program.cs`

**Files:**
- Modify: `src/LogsPlatform.Web/Program.cs`

**Interfaces:**
- Consumes: `IProcessNodeRepository`/`ProcessNodeRepository`, `IOperationRepository`/`OperationRepository` (Tasks 1/3/4).
- Produces: DI registrations that make Task 6/7's controllers (and their `WebApplicationFactory` tests) resolvable.

- [ ] **Step 1: Add the two new DI registrations**

Modify `Program.cs` — add these two lines directly after the existing `AddScoped<IScreenServiceRepository, ScreenServiceRepository>();` line (do not reorder or remove anything else):

```csharp
builder.Services.AddScoped<IProcessNodeRepository, ProcessNodeRepository>();
builder.Services.AddScoped<IOperationRepository, OperationRepository>();
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
builder.Services.AddScoped<IAppModuleRepository, AppModuleRepository>();
builder.Services.AddScoped<IScreenServiceRepository, ScreenServiceRepository>();
builder.Services.AddScoped<IProcessNodeRepository, ProcessNodeRepository>();
builder.Services.AddScoped<IOperationRepository, OperationRepository>();

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
Expected: `Passed! - Failed: 0, Passed: 50, Skipped: 0, Total: 50` — unchanged (no controller tests exist yet for the new entities — those are Tasks 6-7).

- [ ] **Step 4: Commit**

```bash
git add src/LogsPlatform.Web/Program.cs
git commit -m "Wire up DI for ProcessNodeRepository and OperationRepository"
```

---

### Task 6: `ProcessesController` + tests

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/ProcessContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/ProcessesController.cs`
- Create: `tests/LogsPlatform.Tests/Web/ProcessesControllerTests.cs`

**Interfaces:**
- Consumes: `IProcessNodeRepository` (Task 1/3), `IScreenServiceRepository` (Task 1/4 of A1, existing, for the parent-existence guard), DI wiring (Task 5).
- Produces: `POST/GET/PUT/DELETE /api/v1/admin/screen-services/{screenServiceId}/processes[/{id}]`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/ProcessesControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ProcessesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ProcessesControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<int> CreateScreenServiceAsync(HttpClient client, string appName, string moduleName, string screenServiceName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var moduleResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{app!.Id}/modules",
            new CreateModuleRequest(moduleName, null));
        var module = await moduleResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var screenServiceResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/modules/{module!.Id}/screen-services",
            new CreateScreenServiceRequest(screenServiceName, "Service", null));
        var screenService = await screenServiceResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();
        return screenService!.Id;
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsProcess()
    {
        var client = _factory.CreateClient();
        var screenServiceId = await CreateScreenServiceAsync(client, "ProcessControllerTestApp1", "Payments", "PaymentGateway");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId}/processes",
            new CreateProcessRequest("ChargeCard", "Charges a customer's card"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ProcessResponse>();
        Assert.NotNull(created);
        Assert.Equal("ChargeCard", created!.Name);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var screenServiceId = await CreateScreenServiceAsync(client, "ProcessControllerTestApp2", "Payments", "PaymentGateway");
        var request = new CreateProcessRequest("DuplicateProcess", null);

        var first = await client.PostAsJsonAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes", request);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_ProcessBelongingToDifferentScreenService_Returns404()
    {
        var client = _factory.CreateClient();
        var screenServiceId1 = await CreateScreenServiceAsync(client, "ProcessIdorTestApp1", "ModuleA", "ScreenServiceA");
        var screenServiceId2 = await CreateScreenServiceAsync(client, "ProcessIdorTestApp2", "ModuleB", "ScreenServiceB");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId1}/processes",
            new CreateProcessRequest("BelongsToScreenService1", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ProcessResponse>();

        var crossParentGet = await client.GetAsync($"/api/v1/admin/screen-services/{screenServiceId2}/processes/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossParentGet.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesNameAndDescription()
    {
        var client = _factory.CreateClient();
        var screenServiceId = await CreateScreenServiceAsync(client, "ProcessRenameControllerTestApp", "Payments", "PaymentGateway");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId}/processes",
            new CreateProcessRequest("OriginalName", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ProcessResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId}/processes/{created!.Id}",
            new RenameProcessRequest("RenamedProcess", "updated"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<ProcessResponse>();
        Assert.Equal("RenamedProcess", renamed!.Name);

        var getResponse = await client.GetAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes/{created.Id}");
        var reloaded = await getResponse.Content.ReadFromJsonAsync<ProcessResponse>();
        Assert.Equal("RenamedProcess", reloaded!.Name);
    }

    [Fact]
    public async Task Rename_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var screenServiceId = await CreateScreenServiceAsync(client, "ProcessRenameConflictControllerTestApp", "Payments", "PaymentGateway");
        await client.PostAsJsonAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes", new CreateProcessRequest("Taken", null));
        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes", new CreateProcessRequest("ToRename", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ProcessResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId}/processes/{created!.Id}",
            new RenameProcessRequest("Taken", null));

        Assert.Equal(HttpStatusCode.Conflict, renameResponse.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownScreenServiceId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/screen-services/999999/processes",
            new CreateProcessRequest("ChargeCard", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var screenServiceId = await CreateScreenServiceAsync(client, "ProcessDeactivateControllerTestApp", "Payments", "PaymentGateway");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenServiceId}/processes",
            new CreateProcessRequest("ToDeactivate", null));
        var created = await createResponse.Content.ReadFromJsonAsync<ProcessResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<ProcessResponse>>($"/api/v1/admin/screen-services/{screenServiceId}/processes");
        Assert.DoesNotContain(listResponse!, p => p.Id == created.Id);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ProcessesControllerTests`
Expected: FAIL — `CreateProcessRequest`/`ProcessResponse`/`ProcessesController` do not exist yet.

- [ ] **Step 3: Write the request/response contracts**

```csharp
// src/LogsPlatform.Web/Contracts/ProcessContracts.cs
namespace LogsPlatform.Web.Contracts;

public record CreateProcessRequest(string Name, string? Description);

public record ProcessResponse(int Id, int ScreenServiceId, string Name, string? Description, bool IsActive);

public record RenameProcessRequest(string Name, string? Description);
```

- [ ] **Step 4: Write `ProcessesController`**

```csharp
// src/LogsPlatform.Web/Controllers/ProcessesController.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/screen-services/{screenServiceId:int}/processes")]
public class ProcessesController : ControllerBase
{
    private readonly IScreenServiceRepository _screenServices;
    private readonly IProcessNodeRepository _processes;

    public ProcessesController(IScreenServiceRepository screenServices, IProcessNodeRepository processes)
    {
        _screenServices = screenServices;
        _processes = processes;
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
        return NoContent();
    }

    private static ProcessResponse ToResponse(ProcessNode process) =>
        new(process.Id, process.ScreenServiceId, process.Name, process.Description, process.IsActive);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter ProcessesControllerTests`
Expected: PASS (7 tests).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/ProcessContracts.cs src/LogsPlatform.Web/Controllers/ProcessesController.cs tests/LogsPlatform.Tests/Web/ProcessesControllerTests.cs
git commit -m "Add Processes admin API controller"
```

---

### Task 7: `OperationsController` + tests

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/OperationContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/OperationsController.cs`
- Create: `tests/LogsPlatform.Tests/Web/OperationsControllerTests.cs`

**Interfaces:**
- Consumes: `IOperationRepository` (Task 1/4), `IProcessNodeRepository` (Task 1/3, for the parent-existence guard), DI wiring (Task 5).
- Produces: `POST/GET/PUT/DELETE /api/v1/admin/processes/{processId}/operations[/{id}]` — the last piece of this plan; completes the full 4-level hierarchy backend.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/OperationsControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class OperationsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public OperationsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<int> CreateProcessAsync(HttpClient client, string appName, string moduleName, string screenServiceName, string processName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var moduleResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{app!.Id}/modules",
            new CreateModuleRequest(moduleName, null));
        var module = await moduleResponse.Content.ReadFromJsonAsync<ModuleResponse>();

        var screenServiceResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/modules/{module!.Id}/screen-services",
            new CreateScreenServiceRequest(screenServiceName, "Service", null));
        var screenService = await screenServiceResponse.Content.ReadFromJsonAsync<ScreenServiceResponse>();

        var processResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/screen-services/{screenService!.Id}/processes",
            new CreateProcessRequest(processName, null));
        var process = await processResponse.Content.ReadFromJsonAsync<ProcessResponse>();
        return process!.Id;
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsOperation()
    {
        var client = _factory.CreateClient();
        var processId = await CreateProcessAsync(client, "OperationControllerTestApp1", "Payments", "PaymentGateway", "ChargeCard");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/processes/{processId}/operations",
            new CreateOperationRequest("AuthorizePayment", "Authorizes the payment with the card network"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<OperationResponse>();
        Assert.NotNull(created);
        Assert.Equal("AuthorizePayment", created!.Name);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/processes/{processId}/operations/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var processId = await CreateProcessAsync(client, "OperationControllerTestApp2", "Payments", "PaymentGateway", "ChargeCard");
        var request = new CreateOperationRequest("DuplicateOperation", null);

        var first = await client.PostAsJsonAsync($"/api/v1/admin/processes/{processId}/operations", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/v1/admin/processes/{processId}/operations", request);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_OperationBelongingToDifferentProcess_Returns404()
    {
        var client = _factory.CreateClient();
        var processId1 = await CreateProcessAsync(client, "OperationIdorTestApp1", "ModuleA", "ScreenServiceA", "ProcessA");
        var processId2 = await CreateProcessAsync(client, "OperationIdorTestApp2", "ModuleB", "ScreenServiceB", "ProcessB");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/processes/{processId1}/operations",
            new CreateOperationRequest("BelongsToProcess1", null));
        var created = await createResponse.Content.ReadFromJsonAsync<OperationResponse>();

        var crossParentGet = await client.GetAsync($"/api/v1/admin/processes/{processId2}/operations/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossParentGet.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesNameAndDescription()
    {
        var client = _factory.CreateClient();
        var processId = await CreateProcessAsync(client, "OperationRenameControllerTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/processes/{processId}/operations",
            new CreateOperationRequest("OriginalName", null));
        var created = await createResponse.Content.ReadFromJsonAsync<OperationResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/processes/{processId}/operations/{created!.Id}",
            new RenameOperationRequest("RenamedOperation", "updated"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<OperationResponse>();
        Assert.Equal("RenamedOperation", renamed!.Name);

        var getResponse = await client.GetAsync($"/api/v1/admin/processes/{processId}/operations/{created.Id}");
        var reloaded = await getResponse.Content.ReadFromJsonAsync<OperationResponse>();
        Assert.Equal("RenamedOperation", reloaded!.Name);
    }

    [Fact]
    public async Task Rename_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var processId = await CreateProcessAsync(client, "OperationRenameConflictControllerTestApp", "Payments", "PaymentGateway", "ChargeCard");
        await client.PostAsJsonAsync($"/api/v1/admin/processes/{processId}/operations", new CreateOperationRequest("Taken", null));
        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/processes/{processId}/operations", new CreateOperationRequest("ToRename", null));
        var created = await createResponse.Content.ReadFromJsonAsync<OperationResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/processes/{processId}/operations/{created!.Id}",
            new RenameOperationRequest("Taken", null));

        Assert.Equal(HttpStatusCode.Conflict, renameResponse.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownProcessId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/processes/999999/operations",
            new CreateOperationRequest("AuthorizePayment", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var processId = await CreateProcessAsync(client, "OperationDeactivateControllerTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/processes/{processId}/operations",
            new CreateOperationRequest("ToDeactivate", null));
        var created = await createResponse.Content.ReadFromJsonAsync<OperationResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/processes/{processId}/operations/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<OperationResponse>>($"/api/v1/admin/processes/{processId}/operations");
        Assert.DoesNotContain(listResponse!, o => o.Id == created.Id);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter OperationsControllerTests`
Expected: FAIL — `CreateOperationRequest`/`OperationResponse`/`OperationsController` do not exist yet.

- [ ] **Step 3: Write the request/response contracts**

```csharp
// src/LogsPlatform.Web/Contracts/OperationContracts.cs
namespace LogsPlatform.Web.Contracts;

public record CreateOperationRequest(string Name, string? Description);

public record OperationResponse(int Id, int ProcessId, string Name, string? Description, bool IsActive);

public record RenameOperationRequest(string Name, string? Description);
```

- [ ] **Step 4: Write `OperationsController`**

```csharp
// src/LogsPlatform.Web/Controllers/OperationsController.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/processes/{processId:int}/operations")]
public class OperationsController : ControllerBase
{
    private readonly IProcessNodeRepository _processes;
    private readonly IOperationRepository _operations;

    public OperationsController(IProcessNodeRepository processes, IOperationRepository operations)
    {
        _processes = processes;
        _operations = operations;
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
        return NoContent();
    }

    private static OperationResponse ToResponse(Operation operation) =>
        new(operation.Id, operation.ProcessId, operation.Name, operation.Description, operation.IsActive);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter OperationsControllerTests`
Expected: PASS (7 tests).

- [ ] **Step 6: Run the full test suite one more time**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 64, Skipped: 0, Total: 64` (50 from Tasks 1-5 + 7 from Task 6 + 7 from this task).

- [ ] **Step 7: Manual end-to-end smoke check**

The dev database must already have this plan's migration applied — run `dotnet ef database update --project src/LogsPlatform.Infrastructure --connection "<your dev connection string>"` first if you haven't (see `docs/database-setup.md`).

```bash
dotnet run --project src/LogsPlatform.Web --launch-profile http &
sleep 5
curl -s -i -X POST http://localhost:5201/api/v1/admin/applications \
  -H "Content-Type: application/json" -d '{"name":"SmokeTestAppA2","description":null}'
```
Note the returned `id`, then create a module, screen-service, process, and operation in sequence:
```bash
curl -s -i -X POST http://localhost:5201/api/v1/admin/applications/<id>/modules \
  -H "Content-Type: application/json" -d '{"name":"SmokeModule","description":null}'
curl -s -i -X POST http://localhost:5201/api/v1/admin/modules/<moduleId>/screen-services \
  -H "Content-Type: application/json" -d '{"name":"SmokeScreenService","type":"Screen","description":null}'
curl -s -i -X POST http://localhost:5201/api/v1/admin/screen-services/<screenServiceId>/processes \
  -H "Content-Type: application/json" -d '{"name":"SmokeProcess","description":null}'
curl -s -i -X POST http://localhost:5201/api/v1/admin/processes/<processId>/operations \
  -H "Content-Type: application/json" -d '{"name":"SmokeOperation","description":null}'
```
Expected: four `201 Created` responses in sequence, each JSON body containing the expected fields — proving the full 4-level chain can be built end-to-end through the API. Stop the background process afterward with `taskkill //F //IM dotnet.exe`.

- [ ] **Step 8: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/OperationContracts.cs src/LogsPlatform.Web/Controllers/OperationsController.cs tests/LogsPlatform.Tests/Web/OperationsControllerTests.cs
git commit -m "Add Operations admin API controller"
```

---

## Self-Review Notes

- **Spec coverage:** Every element of `docs/superpowers/specs/2026-08-18-application-hierarchy-spine-design.md`'s "API Shape", "Deactivate Semantics", and "Repository Interfaces" sections is implemented for `ProcessNode`/`Operation` across Tasks 1-7, matching the pattern already proven for `AppModule`/`ScreenService` in A1. UI (design doc's "A3") remains explicitly out of scope — it depends on this plan's completed API and is planned separately, and only makes sense once the full 4-level chain exists (this plan completes it).
- **Type consistency:** `IProcessNodeRepository`/`IOperationRepository` signatures from Task 1 are used identically by `ProcessNodeRepository`/`OperationRepository` (Tasks 3-4) and consumed identically by `ProcessesController`/`OperationsController` (Tasks 6-7) — verified by re-reading each task above against the actual current A1 code on `main` (`ModulesController.cs`, `ScreenServicesController.cs`, `AppModuleRepository.cs`, `ScreenServiceRepository.cs`, their test files, and `DbUpdateExceptionExtensions.cs` were all read directly from the repository before writing this plan, not from memory).
- **A1's final-review lessons are applied from this plan's first draft, not discovered again:** parent-existence guard on every `Create` (Task 6/7), reuse of `IsUniqueViolation()` instead of a fresh inline exception filter, genuine follow-up-GET persistence proof in controller-level Rename tests, and Rename-into-duplicate-name test coverage at both repository and controller level (all listed explicitly in Global Constraints above, with exact line references to the A1 code they replicate).
- **No placeholders:** every step has complete, runnable code or an exact command with an expected result, including the running test-count expectations at each stage (37 → 38 → 50 → 50 → 57 → 64), so a deviation is immediately visible.

## After This Plan

The full 4-level hierarchy backend (`Application → AppModule → ScreenService → ProcessNode → Operation`) is complete. Plan A3 (UI drill-down with breadcrumbs, covering all four levels at once) is next — see `docs/superpowers/specs/2026-08-18-application-hierarchy-spine-design.md`'s "Execution Split" section. After A3, M1 (Application Model) still needs "Group B" (`Customer`/`AppUser`/`LogSource`/`ApiKey`/`AppVersion`/`Deployment`) before M1's acceptance criterion ("can fully define RetailPulse+FieldOps via the API/UI") is met — see `12-תוכנית-עבודה-ואבני-דרך.md`.
