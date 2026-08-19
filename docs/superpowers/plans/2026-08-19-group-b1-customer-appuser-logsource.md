# Group B1: Customer + AppUser + LogSource Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Admin CRUD (Create/List/Rename/Deactivate) for `Customer`, `AppUser`, and `LogSource` — three flat, per-`Application` entities with no parent-child chain among themselves, matching the shape specified in `docs/superpowers/specs/2026-08-19-group-b1-customer-appuser-logsource-design.md`. Backend + UI in one plan (unlike Group A's A1/A2/A3 split — these entities are flat, no drill-down/breadcrumb complexity to justify separating UI out).

**Architecture:** Same Modular Monolith layering as Group A (`LogsPlatform.Domain` entities/interfaces → `LogsPlatform.Infrastructure` EF Core/repositories → `LogsPlatform.Web` controllers), plus three new Blazor child components extending `ApplicationsAdmin.razor`'s existing per-row expansion (the same pattern already used for `AppEnvironment`, not the drill-down-with-breadcrumbs pattern Group A's 4-level hierarchy needed).

**Tech Stack:** Same as the current solution — .NET 10, EF Core 10.0.11, SQL Server, xUnit + `Microsoft.AspNetCore.Mvc.Testing`, Blazor Server. No new packages.

## Global Constraints

- **`ExternalCustomerId`/`ExternalUserId` are immutable after creation** — Create sets them, Rename never touches them (only `Name`/`DisplayName` change). **This means `Customer.RenameAsync`/`AppUser.RenameAsync` cannot violate any unique constraint** — their uniqueness index is on `(ApplicationId, ExternalCustomerId)`/`(ApplicationId, ExternalUserId)`, neither of which Rename modifies. Consequently: the repository's `RenameAsync` still wraps `SaveChangesAsync` in `try`/`catch`/detach (defensive symmetry with the established discipline — protects against any save failure poisoning a circuit-scoped `DbContext`, not just unique violations), but **the `CustomersController`/`AppUsersController` `Rename` actions and the UI's Rename handlers do NOT wrap the call in an `IsUniqueViolation()` catch — there is no reachable 409 case for these two entities' Rename, and this project's convention is not to add error handling for scenarios that can't happen.** `LogSource` is different: its uniqueness is on `(ApplicationId, Name)`, and `Name` **is** renameable, so `LogSourceRepository`/`LogSourcesController`/the UI's LogSource Rename handler all keep the full `IsUniqueViolation()` → `409` pattern, identical to every Group A entity.
- **Detach-on-failure in every `AddAsync` (all three entities) and every `RenameAsync` (all three entities, per the note above)** — `try`/`catch`, `_context.Entry(entity).State = EntityState.Detached`, re-throw. Present from this plan's first draft.
- **IDOR protection**: `GetById`/`Rename`/`Deactivate` controller actions verify the loaded entity's `ApplicationId` equals the `appId` in the route, returning `404` if it doesn't.
- **Parent-existence guard on every `Create`**: verify `appId` exists via `IApplicationRepository.GetByIdAsync` before inserting, returning `404` if it doesn't — the lesson from Group A's A1 final review, present here from the first draft.
- **Soft-delete only, `IsActive` on all three entities** — `05-מודל-נתונים.md`'s schema originally omitted `IsActive` from these three; this was a confirmed spec gap (see the design doc) — all three get it, matching the Admin API design's stated `?includeInactive=true` support. `DeactivateAsync` always sets `IsActive = false`, never hard-deletes (the `Event` table doesn't exist until M2, same reasoning as Group A).
- **Reuse `DbUpdateExceptionExtensions.IsUniqueViolation()`** (`src/LogsPlatform.Web/DbUpdateExceptionExtensions.cs`, already merged) wherever a duplicate-conflict case is actually reachable (see the Rename note above for where it is and isn't).
- **`maxlength="200"` on every string `InputText` bound to a field with `HasMaxLength(200)`** in the UI — baked in from the first draft this time (a Minor finding from Group A's A3 final review, fixed there after the fact; not repeating that gap here).
- **UI: three new child Razor components, not one bigger `ApplicationsAdmin.razor`.** `src/LogsPlatform.Web/Components/Shared/CustomersSection.razor`, `UsersSection.razor`, `LogSourcesSection.razor` — each self-contained (`[Parameter] public int ApplicationId`, its own repository injection, its own local state), instantiated once per expanded Application row. Because each component instance is scoped to exactly one `ApplicationId` already, **no per-application dictionary state is needed** (unlike `ApplicationsAdmin.razor`'s existing `_environmentsByAppId` pattern) — each component just holds its own flat `List<T>`/`_editingId`/`_newModel` fields, matching the simplicity of a page like `ModulesAdmin.razor` rather than the multi-app-row-dictionary pattern. `ApplicationsAdmin.razor` needs `@using LogsPlatform.Web.Components.Shared` added to its own using list so `<CustomersSection>`/`<UsersSection>`/`<LogSourcesSection>` resolve by tag name — Blazor components resolve like any other C# type, via `@using`, not automatically across sibling folders.
- Target framework `net10.0`, EF Core packages pinned at `10.0.11` everywhere (already the case — this plan adds no new package references).

---

### Task 1: Domain entities (`Customer`, `AppUser`, `LogSource`) + repository interfaces

**Files:**
- Create: `src/LogsPlatform.Domain/Entities/Customer.cs`
- Create: `src/LogsPlatform.Domain/Entities/AppUser.cs`
- Create: `src/LogsPlatform.Domain/Entities/LogSource.cs`
- Modify: `src/LogsPlatform.Domain/Entities/Application.cs` (add `Customers`, `Users`, `LogSources` navigation collections)
- Create: `src/LogsPlatform.Domain/Repositories/ICustomerRepository.cs`
- Create: `src/LogsPlatform.Domain/Repositories/IAppUserRepository.cs`
- Create: `src/LogsPlatform.Domain/Repositories/ILogSourceRepository.cs`

**Interfaces:**
- Consumes: `Application` entity (existing).
- Produces: `Customer`, `AppUser`, `LogSource` entity classes and `ICustomerRepository`, `IAppUserRepository`, `ILogSourceRepository` interfaces that Tasks 3-5 implement against.

- [ ] **Step 1: Write the three entities**

```csharp
// src/LogsPlatform.Domain/Entities/Customer.cs
namespace LogsPlatform.Domain.Entities;

public class Customer
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public string ExternalCustomerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
```

```csharp
// src/LogsPlatform.Domain/Entities/AppUser.cs
namespace LogsPlatform.Domain.Entities;

public class AppUser
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public string ExternalUserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
```

```csharp
// src/LogsPlatform.Domain/Entities/LogSource.cs
namespace LogsPlatform.Domain.Entities;

public class LogSource
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}
```

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
}
```

- [ ] **Step 3: Write the repository interfaces**

```csharp
// src/LogsPlatform.Domain/Repositories/ICustomerRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(int id);
    Task<IReadOnlyList<Customer>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<Customer> AddAsync(Customer customer);
    Task<Customer> RenameAsync(int id, string name);
    Task DeactivateAsync(int id);
}
```

```csharp
// src/LogsPlatform.Domain/Repositories/IAppUserRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IAppUserRepository
{
    Task<AppUser?> GetByIdAsync(int id);
    Task<IReadOnlyList<AppUser>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<AppUser> AddAsync(AppUser user);
    Task<AppUser> RenameAsync(int id, string displayName);
    Task DeactivateAsync(int id);
}
```

```csharp
// src/LogsPlatform.Domain/Repositories/ILogSourceRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface ILogSourceRepository
{
    Task<LogSource?> GetByIdAsync(int id);
    Task<IReadOnlyList<LogSource>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<LogSource> AddAsync(LogSource logSource);
    Task<LogSource> RenameAsync(int id, string name, string? description);
    Task DeactivateAsync(int id);
}
```

Note `ICustomerRepository.RenameAsync(int id, string name)` and `IAppUserRepository.RenameAsync(int id, string displayName)` take only the one renameable field — not `(name, description)` like Group A's hierarchy repositories — since `Customer`/`AppUser` have no `Description` field and `ExternalCustomerId`/`ExternalUserId` aren't renameable. `ILogSourceRepository.RenameAsync(int id, string name, string? description)` matches Group A's exact two-parameter shape, since `LogSource` has both fields.

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Domain/Entities/Customer.cs src/LogsPlatform.Domain/Entities/AppUser.cs src/LogsPlatform.Domain/Entities/LogSource.cs src/LogsPlatform.Domain/Entities/Application.cs src/LogsPlatform.Domain/Repositories/ICustomerRepository.cs src/LogsPlatform.Domain/Repositories/IAppUserRepository.cs src/LogsPlatform.Domain/Repositories/ILogSourceRepository.cs
git commit -m "Add Customer, AppUser, LogSource domain entities + repository interfaces"
```

---

### Task 2: `LogsPlatformDbContext` mapping + migration

**Files:**
- Modify: `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs`

**Interfaces:**
- Consumes: `Customer`, `AppUser`, `LogSource` from Task 1.
- Produces: `DbSet<Customer> Customers`, `DbSet<AppUser> Users`, `DbSet<LogSource> LogSources` on `LogsPlatformDbContext`, plus the migration that creates their tables — used by Tasks 3-5's repositories.

- [ ] **Step 1: Write the failing test**

```csharp
// Add to tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs
[Fact]
public async Task CanInsertAndRetrieveCustomerAppUserLogSource()
{
    using var context = TestDatabase.CreateContext();

    var application = new Application { Name = "GroupB1DbContextTestApp", CreatedAt = DateTime.UtcNow };
    application.Customers.Add(new Customer { ExternalCustomerId = "cust-1", Name = "Acme Corp" });
    application.Users.Add(new AppUser { ExternalUserId = "user-1", DisplayName = "Jane Doe" });
    application.LogSources.Add(new LogSource { Name = "PaymentServiceLogs", Description = "Structured logs from the payment microservice" });

    context.Applications.Add(application);
    await context.SaveChangesAsync();

    using var readContext = new LogsPlatformDbContext(
        new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    var loadedApp = await readContext.Applications
        .Include(a => a.Customers)
        .Include(a => a.Users)
        .Include(a => a.LogSources)
        .FirstAsync(a => a.Name == "GroupB1DbContextTestApp");

    Assert.Single(loadedApp.Customers);
    Assert.Equal("cust-1", loadedApp.Customers.First().ExternalCustomerId);
    Assert.True(loadedApp.Customers.First().IsActive);
    Assert.Single(loadedApp.Users);
    Assert.Equal("user-1", loadedApp.Users.First().ExternalUserId);
    Assert.True(loadedApp.Users.First().IsActive);
    Assert.Single(loadedApp.LogSources);
    Assert.Equal("PaymentServiceLogs", loadedApp.LogSources.First().Name);
    Assert.True(loadedApp.LogSources.First().IsActive);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter CanInsertAndRetrieveCustomerAppUserLogSource`
Expected: FAIL — build error, `context.Customers`/`DbSet<Customer>` etc. do not exist yet.

- [ ] **Step 3: Add the `DbSet`s and `OnModelCreating` configuration**

Modify `LogsPlatformDbContext.cs` — add three `DbSet` properties and extend `OnModelCreating` (do not remove any existing configuration block, including `ConfigureConventions`'s `UtcDateTimeConverter` wiring and every existing `Application`/`AppEnvironment`/`AppModule`/`ScreenService`/`ProcessNode`/`Operation` block — only add to them):

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
    }
}
```

- [ ] **Step 4: Generate the migration**

This is an **additive** migration — no existing migration's history may be touched or regenerated:

```bash
dotnet ef migrations add AddCustomerAppUserLogSource \
  --project src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj \
  --startup-project src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj
```

Expected: creates `src/LogsPlatform.Infrastructure/Migrations/<timestamp>_AddCustomerAppUserLogSource.cs` and updates `LogsPlatformDbContextModelSnapshot.cs` — creating three new tables (`Customers`, `Users`, `LogSources`) with the FK/cascade/unique-index shape from Step 3. Verify the generated migration's `Up()` only adds the three new tables and their indexes — it must not contain any `DropTable`/`DropColumn` against any existing table (`Applications`, `AppEnvironments`, `Modules`, `ScreenServices`, `Processes`, `Operations`) or their existing indexes. If it does, something in Step 3 changed the existing model unintentionally — STOP and investigate before proceeding.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter CanInsertAndRetrieveCustomerAppUserLogSource`
Expected: PASS.

- [ ] **Step 6: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 81, Skipped: 0, Total: 81` (the 80 tests that exist on `main` before this task, plus this task's new one).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs src/LogsPlatform.Infrastructure/Migrations/ tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs
git commit -m "Add Customer, AppUser, LogSource EF Core mapping + migration"
```

---

### Task 3: `CustomerRepository` implementation + tests

**Files:**
- Create: `src/LogsPlatform.Infrastructure/Repositories/CustomerRepository.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/CustomerRepositoryTests.cs`

**Interfaces:**
- Consumes: `ICustomerRepository` (Task 1), `LogsPlatformDbContext` (Task 2).
- Produces: `CustomerRepository` — registered in DI by Task 6, consumed by Task 7's controller and Task 10's UI component.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/CustomerRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class CustomerRepositoryTests
{
    private static async Task<int> CreateTestApplicationAsync(LogsPlatformDbContext context, string name)
    {
        var application = new Application { Name = name, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsCustomer_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "CustomerAddTestApp");
        var repository = new CustomerRepository(context);

        var created = await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-1", Name = "Acme Corp" });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("cust-1", loaded!.ExternalCustomerId);
        Assert.Equal("Acme Corp", loaded.Name);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByApplicationIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "CustomerFilterTestApp");
        var repository = new CustomerRepository(context);

        var active = await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-active", Name = "Active" });
        var toDeactivate = await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-inactive", Name = "WillBeInactive" });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByApplicationIdAsync(appId);
        var withInactive = await repository.GetByApplicationIdAsync(appId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesName_LeavesExternalCustomerIdUnchanged()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "CustomerRenameTestApp");
        var repository = new CustomerRepository(context);
        var created = await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-1", Name = "OldName" });

        var renamed = await repository.RenameAsync(created.Id, "NewName");

        Assert.Equal("NewName", renamed.Name);
        Assert.Equal("cust-1", renamed.ExternalCustomerId);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "CustomerDeactivateTestApp");
        var repository = new CustomerRepository(context);
        var created = await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-1", Name = "ToDeactivate" });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateExternalIdFailure_SubsequentUniqueExternalIdStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "CustomerCircuitTestApp");
        var repository = new CustomerRepository(context);

        await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-dup", Name = "First" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-dup", Name = "Second" }));

        var created = await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-unique", Name = "Third" });

        Assert.Equal("cust-unique", created.ExternalCustomerId);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter CustomerRepositoryTests`
Expected: FAIL — `CustomerRepository` does not exist yet.

- [ ] **Step 3: Implement `CustomerRepository`**

```csharp
// src/LogsPlatform.Infrastructure/Repositories/CustomerRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class CustomerRepository : ICustomerRepository
{
    private readonly LogsPlatformDbContext _context;

    public CustomerRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<Customer?> GetByIdAsync(int id) =>
        await _context.Customers.FindAsync(id);

    public async Task<IReadOnlyList<Customer>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        var query = _context.Customers.AsNoTracking().Where(c => c.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<Customer> AddAsync(Customer customer)
    {
        _context.Customers.Add(customer);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(customer).State = EntityState.Detached;
            throw;
        }
        return customer;
    }

    public async Task<Customer> RenameAsync(int id, string name)
    {
        var customer = await _context.Customers.FindAsync(id)
            ?? throw new InvalidOperationException($"Customer {id} not found.");
        customer.Name = name;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(customer).State = EntityState.Detached;
            throw;
        }
        return customer;
    }

    public async Task DeactivateAsync(int id)
    {
        var customer = await _context.Customers.FindAsync(id)
            ?? throw new InvalidOperationException($"Customer {id} not found.");
        customer.IsActive = false;
        await _context.SaveChangesAsync();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter CustomerRepositoryTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Infrastructure/Repositories/CustomerRepository.cs tests/LogsPlatform.Tests/Infrastructure/CustomerRepositoryTests.cs
git commit -m "Implement CustomerRepository with detach-on-failure handling"
```

---

### Task 4: `AppUserRepository` implementation + tests

**Files:**
- Create: `src/LogsPlatform.Infrastructure/Repositories/AppUserRepository.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/AppUserRepositoryTests.cs`

**Interfaces:**
- Consumes: `IAppUserRepository` (Task 1), `LogsPlatformDbContext` (Task 2), `CustomerRepository` pattern (Task 3, for the parallel shape).
- Produces: `AppUserRepository` — registered in DI by Task 6, consumed by Task 8's controller and Task 11's UI component.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/AppUserRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class AppUserRepositoryTests
{
    private static async Task<int> CreateTestApplicationAsync(LogsPlatformDbContext context, string name)
    {
        var application = new Application { Name = name, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsAppUser_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppUserAddTestApp");
        var repository = new AppUserRepository(context);

        var created = await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-1", DisplayName = "Jane Doe" });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("user-1", loaded!.ExternalUserId);
        Assert.Equal("Jane Doe", loaded.DisplayName);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByApplicationIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppUserFilterTestApp");
        var repository = new AppUserRepository(context);

        var active = await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-active", DisplayName = "Active" });
        var toDeactivate = await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-inactive", DisplayName = "WillBeInactive" });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByApplicationIdAsync(appId);
        var withInactive = await repository.GetByApplicationIdAsync(appId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesDisplayName_LeavesExternalUserIdUnchanged()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppUserRenameTestApp");
        var repository = new AppUserRepository(context);
        var created = await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-1", DisplayName = "OldName" });

        var renamed = await repository.RenameAsync(created.Id, "NewName");

        Assert.Equal("NewName", renamed.DisplayName);
        Assert.Equal("user-1", renamed.ExternalUserId);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppUserDeactivateTestApp");
        var repository = new AppUserRepository(context);
        var created = await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-1", DisplayName = "ToDeactivate" });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateExternalIdFailure_SubsequentUniqueExternalIdStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "AppUserCircuitTestApp");
        var repository = new AppUserRepository(context);

        await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-dup", DisplayName = "First" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-dup", DisplayName = "Second" }));

        var created = await repository.AddAsync(new AppUser { ApplicationId = appId, ExternalUserId = "user-unique", DisplayName = "Third" });

        Assert.Equal("user-unique", created.ExternalUserId);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter AppUserRepositoryTests`
Expected: FAIL — `AppUserRepository` does not exist yet.

- [ ] **Step 3: Implement `AppUserRepository`**

```csharp
// src/LogsPlatform.Infrastructure/Repositories/AppUserRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class AppUserRepository : IAppUserRepository
{
    private readonly LogsPlatformDbContext _context;

    public AppUserRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<AppUser?> GetByIdAsync(int id) =>
        await _context.Users.FindAsync(id);

    public async Task<IReadOnlyList<AppUser>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        var query = _context.Users.AsNoTracking().Where(u => u.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(u => u.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<AppUser> AddAsync(AppUser user)
    {
        _context.Users.Add(user);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(user).State = EntityState.Detached;
            throw;
        }
        return user;
    }

    public async Task<AppUser> RenameAsync(int id, string displayName)
    {
        var user = await _context.Users.FindAsync(id)
            ?? throw new InvalidOperationException($"AppUser {id} not found.");
        user.DisplayName = displayName;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(user).State = EntityState.Detached;
            throw;
        }
        return user;
    }

    public async Task DeactivateAsync(int id)
    {
        var user = await _context.Users.FindAsync(id)
            ?? throw new InvalidOperationException($"AppUser {id} not found.");
        user.IsActive = false;
        await _context.SaveChangesAsync();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter AppUserRepositoryTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Infrastructure/Repositories/AppUserRepository.cs tests/LogsPlatform.Tests/Infrastructure/AppUserRepositoryTests.cs
git commit -m "Implement AppUserRepository with detach-on-failure handling"
```

---

### Task 5: `LogSourceRepository` implementation + tests

**Files:**
- Create: `src/LogsPlatform.Infrastructure/Repositories/LogSourceRepository.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/LogSourceRepositoryTests.cs`

**Interfaces:**
- Consumes: `ILogSourceRepository` (Task 1), `LogsPlatformDbContext` (Task 2).
- Produces: `LogSourceRepository` — registered in DI by Task 6, consumed by Task 9's controller and Task 12's UI component.

**Note:** unlike `CustomerRepository`/`AppUserRepository`, `LogSource.Name` **is** part of its unique index (`(ApplicationId, Name)`) and **is** renameable — so `LogSourceRepository` needs the rename-into-duplicate-name regression test too, matching Group A's exact pattern (this is the one entity in this plan where that scenario is actually reachable).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/LogSourceRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class LogSourceRepositoryTests
{
    private static async Task<int> CreateTestApplicationAsync(LogsPlatformDbContext context, string name)
    {
        var application = new Application { Name = name, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsLogSource_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "LogSourceAddTestApp");
        var repository = new LogSourceRepository(context);

        var created = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "PaymentServiceLogs", Description = "Structured logs" });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("PaymentServiceLogs", loaded!.Name);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByApplicationIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "LogSourceFilterTestApp");
        var repository = new LogSourceRepository(context);

        var active = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "Active" });
        var toDeactivate = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "WillBeInactive" });
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
        var appId = await CreateTestApplicationAsync(context, "LogSourceRenameTestApp");
        var repository = new LogSourceRepository(context);
        var created = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "OldName" });

        var renamed = await repository.RenameAsync(created.Id, "NewName", "new description");

        Assert.Equal("NewName", renamed.Name);
        Assert.Equal("new description", renamed.Description);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "LogSourceDeactivateTestApp");
        var repository = new LogSourceRepository(context);
        var created = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "ToDeactivate" });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateNameFailure_SubsequentUniqueNameStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "LogSourceCircuitTestApp");
        var repository = new LogSourceRepository(context);

        await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "DupSource" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "DupSource" }));

        var created = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "UniqueSource" });

        Assert.Equal("UniqueSource", created.Name);
    }

    [Fact]
    public async Task RenameAsync_ToExistingSiblingName_ThrowsAndSubsequentWriteStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "LogSourceRenameConflictTestApp");
        var repository = new LogSourceRepository(context);
        await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "Taken" });
        var toRename = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "ToRename" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.RenameAsync(toRename.Id, "Taken", null));

        var created = await repository.AddAsync(new LogSource { ApplicationId = appId, Name = "StillWorks" });
        Assert.Equal("StillWorks", created.Name);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter LogSourceRepositoryTests`
Expected: FAIL — `LogSourceRepository` does not exist yet.

- [ ] **Step 3: Implement `LogSourceRepository`**

```csharp
// src/LogsPlatform.Infrastructure/Repositories/LogSourceRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class LogSourceRepository : ILogSourceRepository
{
    private readonly LogsPlatformDbContext _context;

    public LogSourceRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<LogSource?> GetByIdAsync(int id) =>
        await _context.LogSources.FindAsync(id);

    public async Task<IReadOnlyList<LogSource>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        var query = _context.LogSources.AsNoTracking().Where(l => l.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(l => l.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<LogSource> AddAsync(LogSource logSource)
    {
        _context.LogSources.Add(logSource);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(logSource).State = EntityState.Detached;
            throw;
        }
        return logSource;
    }

    public async Task<LogSource> RenameAsync(int id, string name, string? description)
    {
        var logSource = await _context.LogSources.FindAsync(id)
            ?? throw new InvalidOperationException($"LogSource {id} not found.");
        logSource.Name = name;
        logSource.Description = description;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(logSource).State = EntityState.Detached;
            throw;
        }
        return logSource;
    }

    public async Task DeactivateAsync(int id)
    {
        var logSource = await _context.LogSources.FindAsync(id)
            ?? throw new InvalidOperationException($"LogSource {id} not found.");
        logSource.IsActive = false;
        await _context.SaveChangesAsync();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter LogSourceRepositoryTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 97, Skipped: 0, Total: 97` (81 from Tasks 1-2 + 5 from Task 3 + 5 from Task 4 + 6 from this task).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Infrastructure/Repositories/LogSourceRepository.cs tests/LogsPlatform.Tests/Infrastructure/LogSourceRepositoryTests.cs
git commit -m "Implement LogSourceRepository with detach-on-failure handling"
```

---

### Task 6: Wire up DI registrations in `Program.cs`

**Files:**
- Modify: `src/LogsPlatform.Web/Program.cs`

**Interfaces:**
- Consumes: `ICustomerRepository`/`CustomerRepository`, `IAppUserRepository`/`AppUserRepository`, `ILogSourceRepository`/`LogSourceRepository` (Tasks 1/3/4/5).
- Produces: DI registrations that make Tasks 7-9's controllers and Tasks 10-12's UI components resolvable.

- [ ] **Step 1: Add the three new DI registrations**

Modify `Program.cs` — add these three lines directly after the existing `AddScoped<BreadcrumbBuilder>();` line:

```csharp
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<IAppUserRepository, AppUserRepository>();
builder.Services.AddScoped<ILogSourceRepository, LogSourceRepository>();
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
Expected: `Passed! - Failed: 0, Passed: 97, Skipped: 0, Total: 97` — unchanged.

- [ ] **Step 4: Commit**

```bash
git add src/LogsPlatform.Web/Program.cs
git commit -m "Wire up DI for CustomerRepository, AppUserRepository, LogSourceRepository"
```

---

### Task 7: `CustomersController` + tests

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/CustomerContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/CustomersController.cs`
- Create: `tests/LogsPlatform.Tests/Web/CustomersControllerTests.cs`

**Interfaces:**
- Consumes: `ICustomerRepository` (Task 1/3), `IApplicationRepository` (existing, for the parent-existence guard), DI wiring (Task 6).
- Produces: `POST/GET/PUT/DELETE /api/v1/admin/applications/{appId}/customers[/{id}]`.

**Note:** `Rename` does **not** wrap `RenameAsync` in a try/catch — `Name` isn't part of the `(ApplicationId, ExternalCustomerId)` unique index, so no `409` case is reachable here (see this plan's Global Constraints).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/CustomersControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class CustomersControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public CustomersControllerTests(TestWebApplicationFactory factory)
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
    public async Task PostThenGet_CreatesAndReturnsCustomer()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "CustomerControllerTestApp1");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/customers",
            new CreateCustomerRequest("cust-1", "Acme Corp"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.NotNull(created);
        Assert.Equal("cust-1", created!.ExternalCustomerId);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/customers/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateExternalCustomerId_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "CustomerControllerTestApp2");
        var request = new CreateCustomerRequest("cust-dup", "First");

        var first = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/customers", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/customers",
            new CreateCustomerRequest("cust-dup", "Second"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_CustomerBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "CustomerIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "CustomerIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/customers",
            new CreateCustomerRequest("cust-1", "BelongsToApp1"));
        var created = await createResponse.Content.ReadFromJsonAsync<CustomerResponse>();

        var crossAppGet = await client.GetAsync($"/api/v1/admin/applications/{appId2}/customers/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossAppGet.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesName_LeavesExternalCustomerIdUnchanged()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "CustomerRenameControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/customers",
            new CreateCustomerRequest("cust-1", "OriginalName"));
        var created = await createResponse.Content.ReadFromJsonAsync<CustomerResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/customers/{created!.Id}",
            new RenameCustomerRequest("RenamedCustomer"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.Equal("RenamedCustomer", renamed!.Name);
        Assert.Equal("cust-1", renamed.ExternalCustomerId);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/customers/{created.Id}");
        var reloaded = await getResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.Equal("RenamedCustomer", reloaded!.Name);
    }

    [Fact]
    public async Task Create_UnknownApplicationId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications/999999/customers",
            new CreateCustomerRequest("cust-1", "Acme Corp"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "CustomerDeactivateControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/customers",
            new CreateCustomerRequest("cust-1", "ToDeactivate"));
        var created = await createResponse.Content.ReadFromJsonAsync<CustomerResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/customers/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<CustomerResponse>>($"/api/v1/admin/applications/{appId}/customers");
        Assert.DoesNotContain(listResponse!, c => c.Id == created.Id);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter CustomersControllerTests`
Expected: FAIL — `CreateCustomerRequest`/`CustomerResponse`/`CustomersController` do not exist yet.

- [ ] **Step 3: Write the request/response contracts**

```csharp
// src/LogsPlatform.Web/Contracts/CustomerContracts.cs
namespace LogsPlatform.Web.Contracts;

public record CreateCustomerRequest(string ExternalCustomerId, string Name);

public record CustomerResponse(int Id, int ApplicationId, string ExternalCustomerId, string Name, bool IsActive);

public record RenameCustomerRequest(string Name);
```

- [ ] **Step 4: Write `CustomersController`**

```csharp
// src/LogsPlatform.Web/Controllers/CustomersController.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/customers")]
public class CustomersController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly ICustomerRepository _customers;

    public CustomersController(IApplicationRepository applications, ICustomerRepository customers)
    {
        _applications = applications;
        _customers = customers;
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
        return ToResponse(customer);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _customers.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _customers.DeactivateAsync(id);
        return NoContent();
    }

    private static CustomerResponse ToResponse(Customer customer) =>
        new(customer.Id, customer.ApplicationId, customer.ExternalCustomerId, customer.Name, customer.IsActive);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter CustomersControllerTests`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/CustomerContracts.cs src/LogsPlatform.Web/Controllers/CustomersController.cs tests/LogsPlatform.Tests/Web/CustomersControllerTests.cs
git commit -m "Add Customers admin API controller"
```

---

### Task 8: `AppUsersController` + tests

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/AppUserContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/AppUsersController.cs`
- Create: `tests/LogsPlatform.Tests/Web/AppUsersControllerTests.cs`

**Interfaces:**
- Consumes: `IAppUserRepository` (Task 1/4), `IApplicationRepository` (existing), DI wiring (Task 6).
- Produces: `POST/GET/PUT/DELETE /api/v1/admin/applications/{appId}/users[/{id}]`.

**Note:** same as `CustomersController` — `Rename` does not wrap `RenameAsync` in a try/catch (`DisplayName` isn't part of the unique index).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/AppUsersControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AppUsersControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AppUsersControllerTests(TestWebApplicationFactory factory)
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
    public async Task PostThenGet_CreatesAndReturnsAppUser()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "AppUserControllerTestApp1");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/users",
            new CreateAppUserRequest("user-1", "Jane Doe"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<AppUserResponse>();
        Assert.NotNull(created);
        Assert.Equal("user-1", created!.ExternalUserId);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/users/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateExternalUserId_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "AppUserControllerTestApp2");
        var first = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/users",
            new CreateAppUserRequest("user-dup", "First"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/users",
            new CreateAppUserRequest("user-dup", "Second"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_AppUserBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "AppUserIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "AppUserIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/users",
            new CreateAppUserRequest("user-1", "BelongsToApp1"));
        var created = await createResponse.Content.ReadFromJsonAsync<AppUserResponse>();

        var crossAppGet = await client.GetAsync($"/api/v1/admin/applications/{appId2}/users/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossAppGet.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesDisplayName_LeavesExternalUserIdUnchanged()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "AppUserRenameControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/users",
            new CreateAppUserRequest("user-1", "OriginalName"));
        var created = await createResponse.Content.ReadFromJsonAsync<AppUserResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/users/{created!.Id}",
            new RenameAppUserRequest("RenamedUser"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<AppUserResponse>();
        Assert.Equal("RenamedUser", renamed!.DisplayName);
        Assert.Equal("user-1", renamed.ExternalUserId);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/users/{created.Id}");
        var reloaded = await getResponse.Content.ReadFromJsonAsync<AppUserResponse>();
        Assert.Equal("RenamedUser", reloaded!.DisplayName);
    }

    [Fact]
    public async Task Create_UnknownApplicationId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications/999999/users",
            new CreateAppUserRequest("user-1", "Jane Doe"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "AppUserDeactivateControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/users",
            new CreateAppUserRequest("user-1", "ToDeactivate"));
        var created = await createResponse.Content.ReadFromJsonAsync<AppUserResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/users/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<AppUserResponse>>($"/api/v1/admin/applications/{appId}/users");
        Assert.DoesNotContain(listResponse!, u => u.Id == created.Id);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter AppUsersControllerTests`
Expected: FAIL — `CreateAppUserRequest`/`AppUserResponse`/`AppUsersController` do not exist yet.

- [ ] **Step 3: Write the request/response contracts**

```csharp
// src/LogsPlatform.Web/Contracts/AppUserContracts.cs
namespace LogsPlatform.Web.Contracts;

public record CreateAppUserRequest(string ExternalUserId, string DisplayName);

public record AppUserResponse(int Id, int ApplicationId, string ExternalUserId, string DisplayName, bool IsActive);

public record RenameAppUserRequest(string DisplayName);
```

- [ ] **Step 4: Write `AppUsersController`**

```csharp
// src/LogsPlatform.Web/Controllers/AppUsersController.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/users")]
public class AppUsersController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppUserRepository _users;

    public AppUsersController(IApplicationRepository applications, IAppUserRepository users)
    {
        _applications = applications;
        _users = users;
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
        return ToResponse(user);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _users.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _users.DeactivateAsync(id);
        return NoContent();
    }

    private static AppUserResponse ToResponse(AppUser user) =>
        new(user.Id, user.ApplicationId, user.ExternalUserId, user.DisplayName, user.IsActive);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter AppUsersControllerTests`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/AppUserContracts.cs src/LogsPlatform.Web/Controllers/AppUsersController.cs tests/LogsPlatform.Tests/Web/AppUsersControllerTests.cs
git commit -m "Add AppUsers admin API controller"
```

---

### Task 9: `LogSourcesController` + tests

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/LogSourceContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/LogSourcesController.cs`
- Create: `tests/LogsPlatform.Tests/Web/LogSourcesControllerTests.cs`

**Interfaces:**
- Consumes: `ILogSourceRepository` (Task 1/5), `IApplicationRepository` (existing), DI wiring (Task 6).
- Produces: `POST/GET/PUT/DELETE /api/v1/admin/applications/{appId}/log-sources[/{id}]`.

**Note:** unlike Tasks 7-8, `LogSource.Name` **is** part of its unique index and **is** renameable, so `Rename` here **does** wrap `RenameAsync` in the full `IsUniqueViolation()` → `409` pattern, matching Group A's controllers exactly (including the rename-into-duplicate-name test).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/LogSourcesControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class LogSourcesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public LogSourcesControllerTests(TestWebApplicationFactory factory)
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
    public async Task PostThenGet_CreatesAndReturnsLogSource()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "LogSourceControllerTestApp1");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/log-sources",
            new CreateLogSourceRequest("PaymentServiceLogs", "Structured logs"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<LogSourceResponse>();
        Assert.NotNull(created);
        Assert.Equal("PaymentServiceLogs", created!.Name);
        Assert.True(created.IsActive);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/log-sources/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "LogSourceControllerTestApp2");
        var request = new CreateLogSourceRequest("DuplicateSource", null);

        var first = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/log-sources", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/log-sources", request);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task GetById_LogSourceBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "LogSourceIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "LogSourceIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/log-sources",
            new CreateLogSourceRequest("BelongsToApp1", null));
        var created = await createResponse.Content.ReadFromJsonAsync<LogSourceResponse>();

        var crossAppGet = await client.GetAsync($"/api/v1/admin/applications/{appId2}/log-sources/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossAppGet.StatusCode);
    }

    [Fact]
    public async Task Rename_UpdatesNameAndDescription()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "LogSourceRenameControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/log-sources",
            new CreateLogSourceRequest("OriginalName", null));
        var created = await createResponse.Content.ReadFromJsonAsync<LogSourceResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/log-sources/{created!.Id}",
            new RenameLogSourceRequest("RenamedSource", "updated"));

        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var renamed = await renameResponse.Content.ReadFromJsonAsync<LogSourceResponse>();
        Assert.Equal("RenamedSource", renamed!.Name);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/log-sources/{created.Id}");
        var reloaded = await getResponse.Content.ReadFromJsonAsync<LogSourceResponse>();
        Assert.Equal("RenamedSource", reloaded!.Name);
    }

    [Fact]
    public async Task Rename_DuplicateName_Returns409Conflict()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "LogSourceRenameConflictControllerTestApp");
        await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/log-sources", new CreateLogSourceRequest("Taken", null));
        var createResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/log-sources", new CreateLogSourceRequest("ToRename", null));
        var created = await createResponse.Content.ReadFromJsonAsync<LogSourceResponse>();

        var renameResponse = await client.PutAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/log-sources/{created!.Id}",
            new RenameLogSourceRequest("Taken", null));

        Assert.Equal(HttpStatusCode.Conflict, renameResponse.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownApplicationId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications/999999/log-sources",
            new CreateLogSourceRequest("PaymentServiceLogs", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsInactive_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "LogSourceDeactivateControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/log-sources",
            new CreateLogSourceRequest("ToDeactivate", null));
        var created = await createResponse.Content.ReadFromJsonAsync<LogSourceResponse>();

        var deactivateResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/log-sources/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<LogSourceResponse>>($"/api/v1/admin/applications/{appId}/log-sources");
        Assert.DoesNotContain(listResponse!, l => l.Id == created.Id);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter LogSourcesControllerTests`
Expected: FAIL — `CreateLogSourceRequest`/`LogSourceResponse`/`LogSourcesController` do not exist yet.

- [ ] **Step 3: Write the request/response contracts**

```csharp
// src/LogsPlatform.Web/Contracts/LogSourceContracts.cs
namespace LogsPlatform.Web.Contracts;

public record CreateLogSourceRequest(string Name, string? Description);

public record LogSourceResponse(int Id, int ApplicationId, string Name, string? Description, bool IsActive);

public record RenameLogSourceRequest(string Name, string? Description);
```

- [ ] **Step 4: Write `LogSourcesController`**

```csharp
// src/LogsPlatform.Web/Controllers/LogSourcesController.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/log-sources")]
public class LogSourcesController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly ILogSourceRepository _logSources;

    public LogSourcesController(IApplicationRepository applications, ILogSourceRepository logSources)
    {
        _applications = applications;
        _logSources = logSources;
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
        return NoContent();
    }

    private static LogSourceResponse ToResponse(LogSource logSource) =>
        new(logSource.Id, logSource.ApplicationId, logSource.Name, logSource.Description, logSource.IsActive);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter LogSourcesControllerTests`
Expected: PASS (7 tests).

- [ ] **Step 6: Run the full test suite one more time**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 116, Skipped: 0, Total: 116` (97 from Tasks 1-6 + 6 from Task 7 + 6 from Task 8 + 7 from this task).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/LogSourceContracts.cs src/LogsPlatform.Web/Controllers/LogSourcesController.cs tests/LogsPlatform.Tests/Web/LogSourcesControllerTests.cs
git commit -m "Add LogSources admin API controller"
```

---

### Task 10: `CustomersSection.razor` UI component

**Files:**
- Create: `src/LogsPlatform.Web/Components/Shared/CustomersSection.razor`
- Modify: `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor` (add `<CustomersSection>` to the expanded row + `@using` for the new namespace)

**Interfaces:**
- Consumes: `ICustomerRepository` (Task 1/3/6).
- Produces: a self-contained Blazor component, one instance per expanded Application row.

- [ ] **Step 1: Write `CustomersSection.razor`**

```razor
@* src/LogsPlatform.Web/Components/Shared/CustomersSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using Microsoft.EntityFrameworkCore
@inject ICustomerRepository CustomerRepository

<h4>Customers</h4>
<table>
    <thead>
        <tr>
            <th>External Customer Id</th>
            <th>Name</th>
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
                            <InputText @bind-Value="_editModel!.Name" required maxlength="200" />
                            <button type="submit">Save</button>
                            <button type="button" @onclick="CancelEdit">Cancel</button>
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
                        <button @onclick="() => StartEdit(customer)">Edit</button>
                    }
                    <button @onclick="() => DeactivateAsync(customer.Id)">Deactivate</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newCustomer" OnValidSubmit="CreateCustomerAsync">
    <label>
        External Customer Id:
        <InputText @bind-Value="_newCustomer.ExternalCustomerId" required maxlength="200" />
    </label>
    <label>
        Name:
        <InputText @bind-Value="_newCustomer.Name" required maxlength="200" />
    </label>
    <button type="submit">Add Customer</button>
</EditForm>
@if (_createError is not null)
{
    <p style="color:red">@_createError</p>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<Customer> _customers = new();
    private readonly NewCustomerModel _newCustomer = new();
    private string? _createError;

    private int? _editingId;
    private EditCustomerModel? _editModel;

    protected override async Task OnInitializedAsync()
    {
        _customers = (await CustomerRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateCustomerAsync()
    {
        _createError = null;
        try
        {
            await CustomerRepository.AddAsync(new Customer
            {
                ApplicationId = ApplicationId,
                ExternalCustomerId = _newCustomer.ExternalCustomerId,
                Name = _newCustomer.Name
            });

            _newCustomer.ExternalCustomerId = string.Empty;
            _newCustomer.Name = string.Empty;
            _customers = (await CustomerRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"A customer with external id '{_newCustomer.ExternalCustomerId}' already exists.";
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
        await CustomerRepository.RenameAsync(customerId, _editModel!.Name);
        _editingId = null;
        _editModel = null;
        _customers = (await CustomerRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task DeactivateAsync(int customerId)
    {
        await CustomerRepository.DeactivateAsync(customerId);
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

Note `SaveRenameAsync` has no try/catch (matches this plan's Global Constraint — `Name` isn't part of `Customer`'s unique index, so there's no reachable conflict here; contrast with Task 12's `LogSourcesSection.razor`, which does need one).

- [ ] **Step 2: Wire `CustomersSection` into `ApplicationsAdmin.razor`**

Add `@using LogsPlatform.Web.Components.Shared` to the top of `ApplicationsAdmin.razor` (alongside the existing `@using` lines), and add `<CustomersSection ApplicationId="application.Id" />` inside the expanded row's `<td colspan="5">`, after the existing Environments `<h4>`/table/form block and before the closing `</td>`:

```razor
@* src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor — top of file *@
@page "/admin/applications"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web.Components.Shared
@using Microsoft.EntityFrameworkCore
@using Microsoft.Data.SqlClient
@inject IApplicationRepository ApplicationRepository
@inject IAppEnvironmentRepository EnvironmentRepository
@rendermode InteractiveServer
```

And within the expanded row (do not change anything else in the file — the Create-Application form, the Environments section, and the `@code` block are all unmodified by this task):

```razor
                        @if (_newEnvironmentModels.TryGetValue(application.Id, out var newEnvironment))
                        {
                            <EditForm Model="newEnvironment" OnValidSubmit="() => CreateEnvironmentAsync(application.Id)">
                                <label>
                                    Name:
                                    <InputText @bind-Value="newEnvironment.Name" required />
                                </label>
                                <label>
                                    Is Production:
                                    <InputCheckbox @bind-Value="newEnvironment.IsProduction" />
                                </label>
                                <button type="submit">Add Environment</button>
                            </EditForm>
                        }

                        <CustomersSection ApplicationId="application.Id" />
                    </td>
                </tr>
            }
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 116, Skipped: 0, Total: 116` — unchanged (this task adds no automated tests, matching Group A's UI-task testing posture).

- [ ] **Step 5: Verify by code inspection (curl cannot reach this content)**

`CustomersSection` is nested inside `ApplicationsAdmin.razor`'s `_expandedAppIds.Contains(...)` conditional, which is `false` for every row on a cold page load. A `curl` request only ever sees the server's initial static render, so it can **never** find `Customers`/`<h4>Customers</h4>` in the response — not even when the component is correctly wired. `curl`-based smoke checks are unusable for any content behind this expand toggle; do not attempt one here or treat a failed grep as a defect. Instead confirm the component is correctly wired by inspection: `<CustomersSection ApplicationId="application.Id" />` is present inside `ApplicationsAdmin.razor`'s expanded-row block, `@using LogsPlatform.Web.Components.Shared` resolves the tag, and the build in Step 3 succeeded (a missing/misspelled component reference is a compile error, not a silent no-op). Full interactive confirmation — actually clicking a row open in a browser — happens once during the required manual walkthrough after all of Group B1 merges (see the plan's closing verification section), not per-task.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/CustomersSection.razor src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor
git commit -m "Add CustomersSection UI component"
```

---

### Task 11: `UsersSection.razor` UI component

**Files:**
- Create: `src/LogsPlatform.Web/Components/Shared/UsersSection.razor`
- Modify: `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor` (add `<UsersSection>` to the expanded row)

**Interfaces:**
- Consumes: `IAppUserRepository` (Task 1/4/6).
- Produces: a self-contained Blazor component, one instance per expanded Application row — same shape as Task 10's `CustomersSection`, different fields.

- [ ] **Step 1: Write `UsersSection.razor`**

```razor
@* src/LogsPlatform.Web/Components/Shared/UsersSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using Microsoft.EntityFrameworkCore
@inject IAppUserRepository AppUserRepository

<h4>Users</h4>
<table>
    <thead>
        <tr>
            <th>External User Id</th>
            <th>Display Name</th>
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
                            <InputText @bind-Value="_editModel!.DisplayName" required maxlength="200" />
                            <button type="submit">Save</button>
                            <button type="button" @onclick="CancelEdit">Cancel</button>
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
                        <button @onclick="() => StartEdit(user)">Edit</button>
                    }
                    <button @onclick="() => DeactivateAsync(user.Id)">Deactivate</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newUser" OnValidSubmit="CreateUserAsync">
    <label>
        External User Id:
        <InputText @bind-Value="_newUser.ExternalUserId" required maxlength="200" />
    </label>
    <label>
        Display Name:
        <InputText @bind-Value="_newUser.DisplayName" required maxlength="200" />
    </label>
    <button type="submit">Add User</button>
</EditForm>
@if (_createError is not null)
{
    <p style="color:red">@_createError</p>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<AppUser> _users = new();
    private readonly NewUserModel _newUser = new();
    private string? _createError;

    private int? _editingId;
    private EditUserModel? _editModel;

    protected override async Task OnInitializedAsync()
    {
        _users = (await AppUserRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateUserAsync()
    {
        _createError = null;
        try
        {
            await AppUserRepository.AddAsync(new AppUser
            {
                ApplicationId = ApplicationId,
                ExternalUserId = _newUser.ExternalUserId,
                DisplayName = _newUser.DisplayName
            });

            _newUser.ExternalUserId = string.Empty;
            _newUser.DisplayName = string.Empty;
            _users = (await AppUserRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"A user with external id '{_newUser.ExternalUserId}' already exists.";
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
        await AppUserRepository.RenameAsync(userId, _editModel!.DisplayName);
        _editingId = null;
        _editModel = null;
        _users = (await AppUserRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task DeactivateAsync(int userId)
    {
        await AppUserRepository.DeactivateAsync(userId);
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

- [ ] **Step 2: Wire `UsersSection` into `ApplicationsAdmin.razor`**

Add `<UsersSection ApplicationId="application.Id" />` directly after the `<CustomersSection ApplicationId="application.Id" />` line added in Task 10 (no other change needed — `@using LogsPlatform.Web.Components.Shared` was already added in Task 10 and covers this component too):

```razor
                        <CustomersSection ApplicationId="application.Id" />
                        <UsersSection ApplicationId="application.Id" />
                    </td>
                </tr>
            }
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 116, Skipped: 0, Total: 116` — unchanged.

- [ ] **Step 5: Verify by code inspection (curl cannot reach this content)**

`UsersSection` is nested inside `ApplicationsAdmin.razor`'s `_expandedAppIds.Contains(...)` conditional, which is `false` for every row on a cold page load. A `curl` request only ever sees the server's initial static render, so it can **never** find `Users`/`<h4>Users</h4>` in the response — not even when the component is correctly wired. `curl`-based smoke checks are unusable for any content behind this expand toggle; do not attempt one here or treat a failed grep as a defect. Instead confirm the component is correctly wired by inspection: `<UsersSection ApplicationId="application.Id" />` is present inside `ApplicationsAdmin.razor`'s expanded-row block, directly after `<CustomersSection>`, and the build in Step 3 succeeded (a missing/misspelled component reference is a compile error, not a silent no-op). Full interactive confirmation — actually clicking a row open in a browser — happens once during the required manual walkthrough after all of Group B1 merges (see the plan's closing verification section), not per-task.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/UsersSection.razor src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor
git commit -m "Add UsersSection UI component"
```

---

### Task 12: `LogSourcesSection.razor` UI component

**Files:**
- Create: `src/LogsPlatform.Web/Components/Shared/LogSourcesSection.razor`
- Modify: `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor` (add `<LogSourcesSection>` to the expanded row — the last piece of this plan)

**Interfaces:**
- Consumes: `ILogSourceRepository` (Task 1/5/6).
- Produces: a self-contained Blazor component, one instance per expanded Application row.

**Note:** unlike Tasks 10-11, `LogSource.Name` is renameable **and** part of the unique index — so `SaveRenameAsync` here **does** need the `IsUniqueViolation()` catch, matching Group A's Rename pattern exactly (this is the one UI section in this plan where a rename-conflict inline error is reachable).

- [ ] **Step 1: Write `LogSourcesSection.razor`**

```razor
@* src/LogsPlatform.Web/Components/Shared/LogSourcesSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using Microsoft.EntityFrameworkCore
@inject ILogSourceRepository LogSourceRepository

<h4>Log Sources</h4>
<table>
    <thead>
        <tr>
            <th>Name</th>
            <th>Description</th>
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
                            <InputText @bind-Value="_editModel!.Name" required maxlength="200" />
                            <InputText @bind-Value="_editModel!.Description" />
                            <button type="submit">Save</button>
                            <button type="button" @onclick="CancelEdit">Cancel</button>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <p style="color:red">@_editError</p>
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
                        <button @onclick="() => StartEdit(logSource)">Edit</button>
                    }
                    <button @onclick="() => DeactivateAsync(logSource.Id)">Deactivate</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newLogSource" OnValidSubmit="CreateLogSourceAsync">
    <label>
        Name:
        <InputText @bind-Value="_newLogSource.Name" required maxlength="200" />
    </label>
    <label>
        Description:
        <InputText @bind-Value="_newLogSource.Description" />
    </label>
    <button type="submit">Add Log Source</button>
</EditForm>
@if (_createError is not null)
{
    <p style="color:red">@_createError</p>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<LogSource> _logSources = new();
    private readonly NewLogSourceModel _newLogSource = new();
    private string? _createError;

    private int? _editingId;
    private EditLogSourceModel? _editModel;
    private string? _editError;

    protected override async Task OnInitializedAsync()
    {
        _logSources = (await LogSourceRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateLogSourceAsync()
    {
        _createError = null;
        try
        {
            await LogSourceRepository.AddAsync(new LogSource
            {
                ApplicationId = ApplicationId,
                Name = _newLogSource.Name,
                Description = _newLogSource.Description
            });

            _newLogSource.Name = string.Empty;
            _newLogSource.Description = null;
            _logSources = (await LogSourceRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"A log source named '{_newLogSource.Name}' already exists.";
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
        try
        {
            await LogSourceRepository.RenameAsync(logSourceId, _editModel!.Name, _editModel!.Description);
            _editingId = null;
            _editModel = null;
            _logSources = (await LogSourceRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _editError = $"A log source named '{_editModel!.Name}' already exists.";
        }
    }

    private async Task DeactivateAsync(int logSourceId)
    {
        await LogSourceRepository.DeactivateAsync(logSourceId);
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

- [ ] **Step 2: Wire `LogSourcesSection` into `ApplicationsAdmin.razor`**

Add `<LogSourcesSection ApplicationId="application.Id" />` directly after the `<UsersSection ApplicationId="application.Id" />` line added in Task 11:

```razor
                        <CustomersSection ApplicationId="application.Id" />
                        <UsersSection ApplicationId="application.Id" />
                        <LogSourcesSection ApplicationId="application.Id" />
                    </td>
                </tr>
            }
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 116, Skipped: 0, Total: 116` — unchanged.

- [ ] **Step 5: Verify by code inspection (curl cannot reach this content)**

`LogSourcesSection` is nested inside `ApplicationsAdmin.razor`'s `_expandedAppIds.Contains(...)` conditional, which is `false` for every row on a cold page load. A `curl` request only ever sees the server's initial static render, so it can **never** find `Log Sources`/`<h4>Log Sources</h4>` in the response — not even when the component is correctly wired. `curl`-based smoke checks are unusable for any content behind this expand toggle; do not attempt one here or treat a failed grep as a defect. Instead confirm the component is correctly wired by inspection: `<LogSourcesSection ApplicationId="application.Id" />` is present inside `ApplicationsAdmin.razor`'s expanded-row block, directly after `<UsersSection>`, and the build in Step 3 succeeded (a missing/misspelled component reference is a compile error, not a silent no-op). Full interactive confirmation — actually clicking a row open in a browser — happens once during the required manual walkthrough after all of Group B1 merges (see the plan's closing verification section), not per-task.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/LogSourcesSection.razor src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor
git commit -m "Add LogSourcesSection UI component — completes Group B1"
```

---

## Self-Review Notes

- **Spec coverage:** every element of `docs/superpowers/specs/2026-08-19-group-b1-customer-appuser-logsource-design.md` is implemented — the three entities with `IsActive` (closing the confirmed spec gap), the immutable-external-id/renameable-display-field distinction for `Customer`/`AppUser`, `LogSource`'s full Group-A-shaped Rename (the one entity where a rename-conflict is reachable), the three parallel Admin API resource shapes, and the three-child-component UI extension of `ApplicationsAdmin.razor`'s existing per-row expansion (not a new page/breadcrumb).
- **Type consistency:** every repository interface's method signatures (Task 1) are used identically by their implementations (Tasks 3-5), consumed identically by their controllers (Tasks 7-9) and UI components (Tasks 10-12) — verified by re-reading each task above against the actual current `ApplicationsAdmin.razor`/`Application.cs`/`LogsPlatformDbContext.cs`/`Program.cs` on `main` before writing this plan, not from memory.
- **The Customer/AppUser-vs-LogSource Rename asymmetry is deliberate and documented in three places** (this plan's Global Constraints, Task 7/8's notes, Task 10/11 vs. Task 12's code) so no implementer mistakes it for an inconsistency to "fix" by adding a matching catch block to Customer/AppUser or removing it from LogSource.
- **No placeholders:** every step has complete, runnable code or an exact command with an expected result, including the running test-count expectations at each stage (80 → 81 → 97 → 97 → 116 → 116 → 116 → 116), so a deviation is immediately visible.

## After This Plan

Group B1 (`Customer`/`AppUser`/`LogSource`) is complete. Per the design doc, **B2** (`ApiKey` — a genuinely different lifecycle: raw-key-shown-once, hash storage, revoke instead of rename/deactivate) and **B3** (`AppVersion` + `Deployment` — a linked release-record pair) are separate, later plans. After Group B is fully done, M1's own acceptance criterion ("fully define RetailPulse+FieldOps via the API/UI") is met, and M2 (Ingestion) is next per `12-תוכנית-עבודה-ואבני-דרך.md`.
