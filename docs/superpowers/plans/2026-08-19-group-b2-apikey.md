# Group B2: ApiKey Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Admin management (Create/List/Revoke) for `ApiKey` — the credential a future ingestion client authenticates with — matching the shape specified in `docs/superpowers/specs/2026-08-19-group-b2-apikey-design.md`. Backend + UI in one plan, same posture as B1.

**Architecture:** Same Modular Monolith layering as Group A/B1 (`LogsPlatform.Domain` entity/interface → `LogsPlatform.Infrastructure` EF Core/repository → `LogsPlatform.Web` controller), plus one new Blazor child component extending `ApplicationsAdmin.razor`'s existing per-row expansion (a 4th subsection, alongside B1's `CustomersSection`/`UsersSection`/`LogSourcesSection`).

**Tech Stack:** Same as the current solution — .NET 10, EF Core 10.0.11, SQL Server, xUnit + `Microsoft.AspNetCore.Mvc.Testing`, Blazor Server. No new packages — key generation and hashing use `System.Security.Cryptography` (`RandomNumberGenerator`, `SHA256`), already part of the BCL.

## Global Constraints

- **No `RenameAsync`/`PUT` endpoint anywhere.** `Label` is set once at `Create` and never edited through this system — unlike every prior entity's uniform 5-endpoint shape, `ApiKey` has exactly 4: `POST`/`GET` (list)/`GET` (single)/`DELETE` (Revoke).
- **No `IsUniqueViolation()`/409 handling anywhere** — repository, controller, or UI. `KeyHash` is derived from a 256-bit random secret the admin never types; a collision is cryptographically unreachable, not just unlikely. This is a deliberate difference from every prior entity in this project, not an omission — do not add a duplicate-conflict test or catch block for it, and do not flag its absence as a gap.
- **Detach-on-failure in `AddAsync` and `RevokeAsync`** — `try`/`catch`, `_context.Entry(entity).State = EntityState.Detached`, re-throw. Present from this plan's first draft (the lesson every one of A1/A2/B1's final reviews had to re-catch because it wasn't carried into the next plan's Global Constraints in time — this plan bakes it in from day one instead). Note: because no duplicate-conflict is reachable (see above), there is no test that actually exercises the `catch` branch in `AddAsync` — that's expected, not a gap; the `RevokeAsync` `Idempotency` test below is the only realistic failure-adjacent scenario, and it doesn't throw either. The detach code is defensive symmetry with the rest of the codebase, not something this plan can prove exercises via a forced failure.
- **`RevokeAsync` is idempotent.** A second `Revoke` call on an already-revoked key must leave the original `RevokedAt` timestamp unchanged (re-revoking must not overwrite the historical fact of when the key actually stopped being valid). Both the repository and the controller's `DELETE` action must return success (not an error) on a second call.
- **IDOR protection**: `GetById`/`Revoke` controller actions verify the loaded entity's `ApplicationId` equals the `appId` in the route, returning `404` if it doesn't. (No `Rename` action exists, so there is nothing to guard there.)
- **Parent-existence guard on `Create`**: verify `appId` exists via `IApplicationRepository.GetByIdAsync` before inserting, returning `404` if it doesn't — same as every prior entity.
- **Raw key generation**: `RandomNumberGenerator.GetBytes(32)` (32 bytes = 256 bits), base64url-encoded (`+`→`-`, `/`→`_`, `=` padding trimmed), prefixed `lgp_`. Generated inside the repository's `AddAsync` — the only place the raw value and its hash need to coexist — and returned to the caller alongside the persisted entity. The raw value is never persisted and never logged.
- **Hashing**: `SHA256.HashData(...)` over the UTF-8 bytes of the raw key, hex-encoded via `Convert.ToHexString(...)` (64 uppercase hex chars), stored in `KeyHash`. Not PBKDF2/BCrypt — those are for low-entropy passwords needing deliberately slow verification; a 256-bit random key has no brute-force surface a fast hash weakens, and this hash sits on a future request hot path (M2 ingestion auth).
- **`KeyHash` column**: `HasMaxLength(64)`, plain (non-unique) index only — present for M2's future ingestion-auth lookup performance, not for any constraint this plan enforces.
- **`Label` has no uniqueness constraint** — two keys on the same `Application` may share a label (e.g. rotating a key: create the replacement with the same label as the one about to be revoked).
- **Response contracts never expose the raw key or the hash except in `Create`'s response.** `ApiKeyResponse` (used by `GetById`/`GetAll`) has no `ApiKey`/`KeyHash` field at all — this is enforced structurally by the record's shape, not by convention. Only `CreateApiKeyResponse` (used solely by `Create`'s `201` response) carries the raw key, under the property name `ApiKey`.
- **`maxlength="200"` on the Label `InputText`** in the UI, matching every prior string field bound to a `HasMaxLength(200)` property.
- **UI**: one new self-contained child component, `src/LogsPlatform.Web/Components/Shared/ApiKeysSection.razor` (`[Parameter] public int ApplicationId`), instantiated once per expanded Application row, added directly after `<LogSourcesSection ApplicationId="application.Id" />` in `ApplicationsAdmin.razor`. No per-application dictionary state needed, same reasoning as B1's three sections. No inline-edit/Rename toggle anywhere in this component — there is nothing to rename. On successful create, the raw key renders once in a `<pre>` block with explicit copy-it-now wording; no clipboard JS interop (keeps this plan free of any new client-side dependency).
- **Manual smoke-check steps use code-inspection, not curl.** `ApiKeysSection` lives inside `ApplicationsAdmin.razor`'s `_expandedAppIds.Contains(...)` conditional, `false` on every cold page load — a `curl` request can never find its content in the response, regardless of whether the component is correctly wired. This plan's UI task copies the already-corrected guidance from B1's fixed plan doc (`docs/superpowers/plans/2026-08-19-group-b1-customer-appuser-logsource.md`, Tasks 10-12) rather than reintroducing the mistake B1 originally made.
- Target framework `net10.0`, EF Core packages pinned at `10.0.11` everywhere (already the case — this plan adds no new package references).

---

### Task 1: Domain entity (`ApiKey`) + repository interface

**Files:**
- Create: `src/LogsPlatform.Domain/Entities/ApiKey.cs`
- Modify: `src/LogsPlatform.Domain/Entities/Application.cs` (add `ApiKeys` navigation collection)
- Create: `src/LogsPlatform.Domain/Repositories/IApiKeyRepository.cs`

**Interfaces:**
- Consumes: `Application` entity (existing).
- Produces: `ApiKey` entity class and `IApiKeyRepository` interface that Task 3 implements against.

- [ ] **Step 1: Write the entity**

```csharp
// src/LogsPlatform.Domain/Entities/ApiKey.cs
namespace LogsPlatform.Domain.Entities;

public class ApiKey
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public string KeyHash { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
```

- [ ] **Step 2: Add the navigation collection to `Application`**

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
}
```

- [ ] **Step 3: Write the repository interface**

```csharp
// src/LogsPlatform.Domain/Repositories/IApiKeyRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IApiKeyRepository
{
    Task<ApiKey?> GetByIdAsync(int id);
    Task<IReadOnlyList<ApiKey>> GetByApplicationIdAsync(int applicationId, bool includeRevoked = false);
    Task<(ApiKey Entity, string RawKey)> AddAsync(int applicationId, string label);
    Task RevokeAsync(int id);
}
```

Note the shape difference from every prior repository: `AddAsync(int applicationId, string label)` takes primitive parameters rather than a constructed entity, and returns a `(ApiKey Entity, string RawKey)` tuple rather than just the entity. The raw key only exists for the instant between generation and hashing — the repository is the only place both the raw value and its persisted hash need to exist together, and the raw value must travel back out to the caller (the controller's `201` response), so it can't be reconstructed from the persisted entity afterward. `RenameAsync` does not exist on this interface at all — see Global Constraints.

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Domain/Entities/ApiKey.cs src/LogsPlatform.Domain/Entities/Application.cs src/LogsPlatform.Domain/Repositories/IApiKeyRepository.cs
git commit -m "Add ApiKey domain entity + repository interface"
```

---

### Task 2: `LogsPlatformDbContext` mapping + migration

**Files:**
- Modify: `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs`
- Modify: `tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs`

**Interfaces:**
- Consumes: `ApiKey` from Task 1.
- Produces: `DbSet<ApiKey> ApiKeys` on `LogsPlatformDbContext`, plus the migration that creates its table — used by Task 3's repository.

- [ ] **Step 1: Write the failing test**

```csharp
// Add to tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs
[Fact]
public async Task CanInsertAndRetrieveApiKey()
{
    using var context = TestDatabase.CreateContext();

    var application = new Application { Name = "ApiKeyDbContextTestApp", CreatedAt = DateTime.UtcNow };
    application.ApiKeys.Add(new ApiKey { KeyHash = new string('a', 64), Label = "CI pipeline key", CreatedAt = DateTime.UtcNow });

    context.Applications.Add(application);
    await context.SaveChangesAsync();

    using var readContext = new LogsPlatformDbContext(
        new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    var loadedApp = await readContext.Applications
        .Include(a => a.ApiKeys)
        .FirstAsync(a => a.Name == "ApiKeyDbContextTestApp");

    Assert.Single(loadedApp.ApiKeys);
    Assert.Equal("CI pipeline key", loadedApp.ApiKeys.First().Label);
    Assert.Null(loadedApp.ApiKeys.First().RevokedAt);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter CanInsertAndRetrieveApiKey`
Expected: FAIL — build error, `context.ApiKeys`/`DbSet<ApiKey>` etc. do not exist yet.

- [ ] **Step 3: Add the `DbSet` and `OnModelCreating` configuration**

Modify `LogsPlatformDbContext.cs` — add the `DbSet<ApiKey>` property and extend `OnModelCreating` (do not remove any existing configuration block — only add to them):

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
    }
}
```

Note `entity.HasIndex(k => k.KeyHash)` has **no** `.IsUnique()` call — see Global Constraints for why.

- [ ] **Step 4: Generate the migration**

This is an **additive** migration — no existing migration's history may be touched or regenerated:

```bash
dotnet ef migrations add AddApiKey \
  --project src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj \
  --startup-project src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj
```

Expected: creates `src/LogsPlatform.Infrastructure/Migrations/<timestamp>_AddApiKey.cs` and updates `LogsPlatformDbContextModelSnapshot.cs` — creating one new table (`ApiKeys`) with the FK/cascade/non-unique-index shape from Step 3. Verify the generated migration's `Up()` only adds the new table and its index — it must not contain any `DropTable`/`DropColumn` against any existing table or their existing indexes. If it does, something in Step 3 changed the existing model unintentionally — STOP and investigate before proceeding.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter CanInsertAndRetrieveApiKey`
Expected: PASS.

- [ ] **Step 6: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 123, Skipped: 0, Total: 123` (the 122 tests that exist on `main` before this task, plus this task's new one).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs src/LogsPlatform.Infrastructure/Migrations/ tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs
git commit -m "Add ApiKey EF Core mapping + migration"
```

---

### Task 3: `ApiKeyRepository` implementation + tests

**Files:**
- Create: `src/LogsPlatform.Infrastructure/Repositories/ApiKeyRepository.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/ApiKeyRepositoryTests.cs`

**Interfaces:**
- Consumes: `IApiKeyRepository` (Task 1), `LogsPlatformDbContext` (Task 2).
- Produces: `ApiKeyRepository` — registered in DI by Task 4, consumed by Task 5's controller and Task 6's UI component.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/ApiKeyRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ApiKeyRepositoryTests
{
    private static async Task<int> CreateTestApplicationAsync(LogsPlatformDbContext context, string name)
    {
        var application = new Application { Name = name, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsApiKey_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ApiKeyAddTestApp");
        var repository = new ApiKeyRepository(context);

        var (created, rawKey) = await repository.AddAsync(appId, "CI pipeline key");
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("CI pipeline key", loaded!.Label);
        Assert.Null(loaded.RevokedAt);
        Assert.NotEmpty(rawKey);
    }

    [Fact]
    public async Task AddAsync_RawKeyHasExpectedPrefix_AndIsNotStoredInKeyHash()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ApiKeyPrefixTestApp");
        var repository = new ApiKeyRepository(context);

        var (created, rawKey) = await repository.AddAsync(appId, "Prefix test key");

        Assert.StartsWith("lgp_", rawKey);
        Assert.NotEqual(rawKey, created.KeyHash);
        Assert.NotEmpty(created.KeyHash);
    }

    [Fact]
    public async Task AddAsync_TwoCalls_ProduceDifferentRawKeysAndHashes()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ApiKeyUniquenessTestApp");
        var repository = new ApiKeyRepository(context);

        var (first, firstRawKey) = await repository.AddAsync(appId, "Key A");
        var (second, secondRawKey) = await repository.AddAsync(appId, "Key B");

        Assert.NotEqual(firstRawKey, secondRawKey);
        Assert.NotEqual(first.KeyHash, second.KeyHash);
    }

    [Fact]
    public async Task GetByApplicationIdAsync_ExcludesRevokedByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ApiKeyFilterTestApp");
        var repository = new ApiKeyRepository(context);

        var (active, _) = await repository.AddAsync(appId, "Active");
        var (toRevoke, _) = await repository.AddAsync(appId, "WillBeRevoked");
        await repository.RevokeAsync(toRevoke.Id);

        var defaultResult = await repository.GetByApplicationIdAsync(appId);
        var withRevoked = await repository.GetByApplicationIdAsync(appId, includeRevoked: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withRevoked.Count);
    }

    [Fact]
    public async Task RevokeAsync_SetsRevokedAt()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ApiKeyRevokeTestApp");
        var repository = new ApiKeyRepository(context);
        var (created, _) = await repository.AddAsync(appId, "ToRevoke");

        await repository.RevokeAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.NotNull(reloaded!.RevokedAt);
    }

    [Fact]
    public async Task RevokeAsync_CalledTwice_LeavesOriginalRevokedAtUnchanged()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ApiKeyDoubleRevokeTestApp");
        var repository = new ApiKeyRepository(context);
        var (created, _) = await repository.AddAsync(appId, "DoubleRevoke");

        await repository.RevokeAsync(created.Id);
        var firstRevokedAt = (await repository.GetByIdAsync(created.Id))!.RevokedAt;

        await Task.Delay(50);
        await repository.RevokeAsync(created.Id);
        var secondRevokedAt = (await repository.GetByIdAsync(created.Id))!.RevokedAt;

        Assert.Equal(firstRevokedAt, secondRevokedAt);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ApiKeyRepositoryTests`
Expected: FAIL — `ApiKeyRepository` does not exist yet.

- [ ] **Step 3: Implement `ApiKeyRepository`**

```csharp
// src/LogsPlatform.Infrastructure/Repositories/ApiKeyRepository.cs
using System.Security.Cryptography;
using System.Text;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ApiKeyRepository : IApiKeyRepository
{
    private const string KeyPrefix = "lgp_";

    private readonly LogsPlatformDbContext _context;

    public ApiKeyRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<ApiKey?> GetByIdAsync(int id) =>
        await _context.ApiKeys.FindAsync(id);

    public async Task<IReadOnlyList<ApiKey>> GetByApplicationIdAsync(int applicationId, bool includeRevoked = false)
    {
        var query = _context.ApiKeys.AsNoTracking().Where(k => k.ApplicationId == applicationId);
        if (!includeRevoked)
        {
            query = query.Where(k => k.RevokedAt == null);
        }
        return await query.ToListAsync();
    }

    public async Task<(ApiKey Entity, string RawKey)> AddAsync(int applicationId, string label)
    {
        var rawKey = GenerateRawKey();
        var apiKey = new ApiKey
        {
            ApplicationId = applicationId,
            Label = label,
            KeyHash = Hash(rawKey),
            CreatedAt = DateTime.UtcNow
        };

        _context.ApiKeys.Add(apiKey);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(apiKey).State = EntityState.Detached;
            throw;
        }
        return (apiKey, rawKey);
    }

    public async Task RevokeAsync(int id)
    {
        var apiKey = await _context.ApiKeys.FindAsync(id)
            ?? throw new InvalidOperationException($"ApiKey {id} not found.");

        if (apiKey.RevokedAt is not null)
        {
            return;
        }

        apiKey.RevokedAt = DateTime.UtcNow;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(apiKey).State = EntityState.Detached;
            throw;
        }
    }

    private static string GenerateRawKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var base64Url = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return KeyPrefix + base64Url;
    }

    private static string Hash(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ApiKeyRepositoryTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 129, Skipped: 0, Total: 129` (123 from Tasks 1-2 + 6 from this task).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Infrastructure/Repositories/ApiKeyRepository.cs tests/LogsPlatform.Tests/Infrastructure/ApiKeyRepositoryTests.cs
git commit -m "Implement ApiKeyRepository with detach-on-failure handling and idempotent revoke"
```

---

### Task 4: Wire up DI registration in `Program.cs`

**Files:**
- Modify: `src/LogsPlatform.Web/Program.cs`

**Interfaces:**
- Consumes: `IApiKeyRepository`/`ApiKeyRepository` (Tasks 1/3).
- Produces: DI registration that makes Task 5's controller and Task 6's UI component resolvable.

- [ ] **Step 1: Add the DI registration**

Modify `Program.cs` — add this line directly after the existing `AddScoped<ILogSourceRepository, LogSourceRepository>();` line:

```csharp
builder.Services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
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
Expected: `Passed! - Failed: 0, Passed: 129, Skipped: 0, Total: 129` — unchanged.

- [ ] **Step 4: Commit**

```bash
git add src/LogsPlatform.Web/Program.cs
git commit -m "Wire up DI for ApiKeyRepository"
```

---

### Task 5: `ApiKeysController` + tests

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/ApiKeyContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/ApiKeysController.cs`
- Create: `tests/LogsPlatform.Tests/Web/ApiKeysControllerTests.cs`

**Interfaces:**
- Consumes: `IApiKeyRepository` (Task 1/3), `IApplicationRepository` (existing, for the parent-existence guard), DI wiring (Task 4).
- Produces: `POST/GET/DELETE /api/v1/admin/applications/{appId}/api-keys[/{id}]`.

**Note:** no `PUT` action exists — see Global Constraints. `Create` never wraps `AddAsync` in a try/catch for `IsUniqueViolation()` — no `409` case is reachable here (see Global Constraints).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/ApiKeysControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ApiKeysControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ApiKeysControllerTests(TestWebApplicationFactory factory)
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
    public async Task PostThenGet_CreatesAndReturnsApiKey()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "ApiKeyControllerTestApp1");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/api-keys",
            new CreateApiKeyRequest("CI pipeline key"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();
        Assert.NotNull(created);
        Assert.Equal("CI pipeline key", created!.Label);
        Assert.StartsWith("lgp_", created.ApiKey);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/api-keys/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<ApiKeyResponse>();
        Assert.Equal("CI pipeline key", fetched!.Label);
        Assert.Null(fetched.RevokedAt);
    }

    [Fact]
    public async Task GetById_ResponseNeverContainsRawKeyOrHashField()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "ApiKeyNoLeakTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/api-keys",
            new CreateApiKeyRequest("LeakCheckKey"));
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{appId}/api-keys/{created!.Id}");
        var body = await getResponse.Content.ReadAsStringAsync();

        Assert.DoesNotContain(created.ApiKey, body);
        Assert.DoesNotContain("keyHash", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetById_ApiKeyBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "ApiKeyIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "ApiKeyIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/api-keys",
            new CreateApiKeyRequest("BelongsToApp1"));
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var crossAppGet = await client.GetAsync($"/api/v1/admin/applications/{appId2}/api-keys/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossAppGet.StatusCode);
    }

    [Fact]
    public async Task Revoke_ApiKeyBelongingToDifferentApplication_Returns404()
    {
        var client = _factory.CreateClient();
        var appId1 = await CreateApplicationAsync(client, "ApiKeyRevokeIdorTestApp1");
        var appId2 = await CreateApplicationAsync(client, "ApiKeyRevokeIdorTestApp2");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId1}/api-keys",
            new CreateApiKeyRequest("BelongsToApp1"));
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var crossAppRevoke = await client.DeleteAsync($"/api/v1/admin/applications/{appId2}/api-keys/{created!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, crossAppRevoke.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownApplicationId_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/applications/999999/api-keys",
            new CreateApiKeyRequest("Orphan"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_SetsRevokedAt_ExcludedFromDefaultList()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "ApiKeyRevokeControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/api-keys",
            new CreateApiKeyRequest("ToRevoke"));
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var revokeResponse = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/api-keys/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var listResponse = await client.GetFromJsonAsync<List<ApiKeyResponse>>($"/api/v1/admin/applications/{appId}/api-keys");
        Assert.DoesNotContain(listResponse!, k => k.Id == created.Id);
    }

    [Fact]
    public async Task Revoke_CalledTwice_ReturnsNoContentBothTimes()
    {
        var client = _factory.CreateClient();
        var appId = await CreateApplicationAsync(client, "ApiKeyDoubleRevokeControllerTestApp");
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/api-keys",
            new CreateApiKeyRequest("DoubleRevoke"));
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var firstRevoke = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/api-keys/{created!.Id}");
        var secondRevoke = await client.DeleteAsync($"/api/v1/admin/applications/{appId}/api-keys/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, firstRevoke.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondRevoke.StatusCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ApiKeysControllerTests`
Expected: FAIL — `CreateApiKeyRequest`/`ApiKeyResponse`/`CreateApiKeyResponse`/`ApiKeysController` do not exist yet.

- [ ] **Step 3: Write the request/response contracts**

```csharp
// src/LogsPlatform.Web/Contracts/ApiKeyContracts.cs
namespace LogsPlatform.Web.Contracts;

public record CreateApiKeyRequest(string Label);

public record ApiKeyResponse(int Id, int ApplicationId, string Label, DateTime CreatedAt, DateTime? RevokedAt);

public record CreateApiKeyResponse(int Id, int ApplicationId, string Label, DateTime CreatedAt, string ApiKey);
```

Note `ApiKeyResponse` and `CreateApiKeyResponse` are two distinct records — `ApiKeyResponse` (used by `GetById`/`GetAll`) has no field carrying the raw key or the hash; `CreateApiKeyResponse` (used only by `Create`'s `201` response) is the only one with an `ApiKey` property. This is the structural enforcement described in Global Constraints — do not merge these into one record with a nullable `ApiKey` field, since that would make it possible for a future change to `GetById`/`GetAll` to populate it by accident.

- [ ] **Step 4: Implement `ApiKeysController`**

```csharp
// src/LogsPlatform.Web/Controllers/ApiKeysController.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/api-keys")]
public class ApiKeysController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IApiKeyRepository _apiKeys;

    public ApiKeysController(IApplicationRepository applications, IApiKeyRepository apiKeys)
    {
        _applications = applications;
        _apiKeys = apiKeys;
    }

    [HttpPost]
    public async Task<ActionResult<CreateApiKeyResponse>> Create(int appId, CreateApiKeyRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var (apiKey, rawKey) = await _apiKeys.AddAsync(appId, request.Label);

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
        return NoContent();
    }

    private static ApiKeyResponse ToResponse(ApiKey apiKey) =>
        new(apiKey.Id, apiKey.ApplicationId, apiKey.Label, apiKey.CreatedAt, apiKey.RevokedAt);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter ApiKeysControllerTests`
Expected: PASS (7 tests).

- [ ] **Step 6: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 136, Skipped: 0, Total: 136` (129 from Tasks 1-4 + 7 from this task).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/ApiKeyContracts.cs src/LogsPlatform.Web/Controllers/ApiKeysController.cs tests/LogsPlatform.Tests/Web/ApiKeysControllerTests.cs
git commit -m "Add ApiKeysController with create/list/get/revoke"
```

---

### Task 6: `ApiKeysSection.razor` UI component — completes Group B2

**Files:**
- Create: `src/LogsPlatform.Web/Components/Shared/ApiKeysSection.razor`
- Modify: `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor`

**Interfaces:**
- Consumes: `IApiKeyRepository` (Task 1/3), DI wiring (Task 4).
- Produces: the "API Keys" subsection on `ApplicationsAdmin.razor`'s per-row expansion.

- [ ] **Step 1: Create `ApiKeysSection.razor`**

```razor
@* src/LogsPlatform.Web/Components/Shared/ApiKeysSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@inject IApiKeyRepository ApiKeyRepository

<h4>API Keys</h4>
@if (_newRawKey is not null)
{
    <p style="color:darkred">This is the only time you will see this key — copy it now.</p>
    <pre>@_newRawKey</pre>
}
<table>
    <thead>
        <tr>
            <th>Label</th>
            <th>Created At</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var apiKey in _apiKeys)
        {
            <tr>
                <td>@apiKey.Label</td>
                <td>@apiKey.CreatedAt</td>
                <td>
                    <button @onclick="() => RevokeAsync(apiKey.Id)">Revoke</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newApiKey" OnValidSubmit="CreateApiKeyAsync">
    <label>
        Label:
        <InputText @bind-Value="_newApiKey.Label" required maxlength="200" />
    </label>
    <button type="submit">Add API Key</button>
</EditForm>

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<ApiKey> _apiKeys = new();
    private readonly NewApiKeyModel _newApiKey = new();
    private string? _newRawKey;

    protected override async Task OnInitializedAsync()
    {
        _apiKeys = (await ApiKeyRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateApiKeyAsync()
    {
        var (_, rawKey) = await ApiKeyRepository.AddAsync(ApplicationId, _newApiKey.Label);

        _newRawKey = rawKey;
        _newApiKey.Label = string.Empty;
        _apiKeys = (await ApiKeyRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task RevokeAsync(int apiKeyId)
    {
        await ApiKeyRepository.RevokeAsync(apiKeyId);
        _apiKeys = (await ApiKeyRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private class NewApiKeyModel
    {
        public string Label { get; set; } = string.Empty;
    }
}
```

No `IsUniqueViolation()` catch anywhere in this component — see Global Constraints. No inline-edit/Rename toggle — there is nothing to rename.

- [ ] **Step 2: Wire `ApiKeysSection` into `ApplicationsAdmin.razor`**

Modify `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor` — add one line directly after the existing `<LogSourcesSection ApplicationId="application.Id" />` line:

```razor
<ApiKeysSection ApplicationId="application.Id" />
```

The `@using LogsPlatform.Web.Components.Shared` directive is already present from B1 — no other change to this file. Do not modify anything else in `ApplicationsAdmin.razor`.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 136, Skipped: 0, Total: 136` — unchanged (this task adds no automated tests, matching the established UI-task testing posture).

- [ ] **Step 5: Verify by code inspection (curl cannot reach this content)**

`ApiKeysSection` is nested inside `ApplicationsAdmin.razor`'s `_expandedAppIds.Contains(...)` conditional, which is `false` for every row on a cold page load. A `curl` request only ever sees the server's initial static render, so it can **never** find `API Keys`/`<h4>API Keys</h4>` in the response — not even when the component is correctly wired. `curl`-based smoke checks are unusable for any content behind this expand toggle; do not attempt one here or treat a failed grep as a defect. Instead confirm the component is correctly wired by inspection: `<ApiKeysSection ApplicationId="application.Id" />` is present inside `ApplicationsAdmin.razor`'s expanded-row block, directly after `<LogSourcesSection>`, and the build in Step 3 succeeded (a missing/misspelled component reference is a compile error, not a silent no-op). Full interactive confirmation — actually creating a key and confirming the raw-key banner renders — happens once during the manual walkthrough after this plan merges.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/ApiKeysSection.razor src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor
git commit -m "Add ApiKeysSection UI component — completes Group B2"
```

---

## Closing Verification (after all 6 tasks merge)

Run a full manual walkthrough in a real browser against the dev database:
1. Start the app (`dotnet run --project src/LogsPlatform.Web --launch-profile http`), navigate to `/admin/applications`.
2. Expand an application row. Confirm "API Keys" appears as a 4th subsection, after Customers/Users/Log Sources.
3. Create an API key with a label. Confirm the raw key renders once, prefixed `lgp_`, in a visibly-flagged block.
4. Collapse and re-expand the row (or reload the page). Confirm the raw key is **not** shown again, only the label and created-at date.
5. Revoke the key. Confirm it disappears from the list.
6. Confirm no console/server errors during any of the above.
