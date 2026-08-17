# Application Model Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the .NET solution skeleton and the first vertical slice of the Application Model — `Application` and `AppEnvironment` entities, persisted in SQL Server via EF Core, exposed through a Web API — proven end-to-end with real tests, establishing the pattern the rest of the hierarchy (Module/ScreenService/ProcessNode/Operation/Customer/AppUser/LogSource/ApiKey/AppVersion/Deployment) will follow in the next plan.

**Architecture:** Modular Monolith per `מסמכי-אפיון/04-ארכיטקטורה.md` — one ASP.NET Core process (`LogsPlatform.Web`) hosting the API, backed by `LogsPlatform.Domain` (entities + repository interfaces, zero external dependencies) and `LogsPlatform.Infrastructure` (EF Core `DbContext` + repository implementations), against one SQL Server database. `LogsPlatform.Analysis` and `LogsPlatform.Client` are not created yet — YAGNI until the plans that need them (M2/M4).

**Tech Stack:** .NET 10 (LTS — see note below), ASP.NET Core Web API (Controllers), EF Core matching the installed SDK + SQL Server provider, SQL Server LocalDB for dev/test, xUnit + `Microsoft.AspNetCore.Mvc.Testing` for tests.

> **Correction (post-Task 1):** this plan originally specified .NET 8. Task 1's implementer reported the dev machine has only the .NET 9 and .NET 10 SDKs installed (no .NET 8 SDK), and `dotnet new` defaulted every project to `net10.0`. Verified via `dotnet --list-sdks`/`--list-runtimes`: SDKs are 9.0.308 and 10.0.101; the .NET 8 *runtime* (8.0.22) is present but its SDK is not. .NET 10 is the current LTS (Microsoft's even-numbered-version-is-LTS cadence), so rather than fight the environment by forcing `net8.0` (untested restore risk with no SDK for it), **the target framework for this entire project is `net10.0`**. Task 1 already installed `Microsoft.EntityFrameworkCore.SqlServer`/`.Design` at `8.0.10` into `LogsPlatform.Infrastructure` and `Microsoft.AspNetCore.Mvc.Testing`/`Microsoft.EntityFrameworkCore.SqlServer` at `8.0.10` into `LogsPlatform.Tests` (reviewed and approved as-is — do not change those). **Every later task that adds an EF Core package must pin the same `8.0.10` version**, so every project in the solution shares one EF Core version — this matters more than matching the package version number to the `net10.0` TargetFramework number, since EF Core 8.x packages are forward-compatible with net10.0 but *mismatched* EF Core versions across projects in one solution is a real source of restore/binding conflicts.

## Global Constraints

- Target framework: `net10.0` everywhere (see correction note above), `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>` in every `.csproj`.
- **Naming collision rule** (from `05-מודל-נתונים.md`): entity names that collide with common BCL types get a disambiguating name — `AppEnvironment` not `Environment` (collides with `System.Environment`), `AppVersion` not `Version` (collides with `System.Version`), `ProcessNode` not `Process` (collides with `System.Diagnostics.Process`). Apply this to every future entity too.
- No connection strings or secrets in any file that gets committed. Connection string lives in User Secrets (`dotnet user-secrets`) for `LogsPlatform.Web`, referenced by config key `ConnectionStrings:LogsPlatformDb`.
- Repository interfaces live in `LogsPlatform.Domain`, implementations in `LogsPlatform.Infrastructure` (Dependency Inversion — per `04-ארכיטקטורה.md` section 3).
- Every repository method that touches `Event`/`AppEnvironment`-scoped data in future plans must require the scope explicitly in its signature (no "get all, filter later" — per `10-Security-Design.md` section 4). Not yet exercised by this plan (no cross-application queries yet), but the pattern starts here: `GetByApplicationIdAsync`, not `GetAllAsync()` with no scope, on any entity that belongs to an `Application`.
- Test database: SQL Server LocalDB, database name `LogsPlatformTests`, connection string `Server=(localdb)\mssqllocaldb;Database=LogsPlatformTests;Trusted_Connection=True;`. Assumes LocalDB is installed (ships with Visual Studio; if missing, `winget install Microsoft.SQLServerExpress` or the "SQL Server Express LocalDB" installer covers it — not scripted here since it's a one-time machine setup, not part of the codebase).

---

### Task 1: Solution and project scaffolding

**Files:**
- Create: `LogsPlatform.sln`
- Create: `src/LogsPlatform.Domain/LogsPlatform.Domain.csproj`
- Create: `src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj`
- Create: `src/LogsPlatform.Web/LogsPlatform.Web.csproj`
- Create: `src/LogsPlatform.Web/Program.cs`
- Create: `tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj`

**Interfaces:**
- Produces: a buildable solution with 4 projects wired together (`Domain` ← `Infrastructure` ← `Web`, `Tests` → all three) that later tasks add files into.

- [ ] **Step 1: Create the solution and project files**

Run these from the repository root of the current working tree (the worktree root if you're in one — do not hardcode a path to any other checkout):

```bash
dotnet new sln -n LogsPlatform
dotnet new classlib -n LogsPlatform.Domain -o src/LogsPlatform.Domain
dotnet new classlib -n LogsPlatform.Infrastructure -o src/LogsPlatform.Infrastructure
dotnet new webapi -n LogsPlatform.Web -o src/LogsPlatform.Web --use-controllers
dotnet new xunit -n LogsPlatform.Tests -o tests/LogsPlatform.Tests
dotnet sln add src/LogsPlatform.Domain/LogsPlatform.Domain.csproj
dotnet sln add src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj
dotnet sln add src/LogsPlatform.Web/LogsPlatform.Web.csproj
dotnet sln add tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj
```

- [ ] **Step 2: Wire up project references**

```bash
dotnet add src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj reference src/LogsPlatform.Domain/LogsPlatform.Domain.csproj
dotnet add src/LogsPlatform.Web/LogsPlatform.Web.csproj reference src/LogsPlatform.Domain/LogsPlatform.Domain.csproj
dotnet add src/LogsPlatform.Web/LogsPlatform.Web.csproj reference src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj
dotnet add tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj reference src/LogsPlatform.Domain/LogsPlatform.Domain.csproj
dotnet add tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj reference src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj
dotnet add tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj reference src/LogsPlatform.Web/LogsPlatform.Web.csproj
```

- [ ] **Step 3: Add EF Core packages to Infrastructure**

```bash
dotnet add src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj package Microsoft.EntityFrameworkCore.SqlServer --version 8.0.10
dotnet add src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj package Microsoft.EntityFrameworkCore.Design --version 8.0.10
```

- [ ] **Step 4: Add test packages to Tests project**

```bash
dotnet add tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj package Microsoft.AspNetCore.Mvc.Testing --version 8.0.10
dotnet add tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj package Microsoft.EntityFrameworkCore.SqlServer --version 8.0.10
```

- [ ] **Step 5: Build the solution**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` (the default `webapi` template's sample `WeatherForecast` files are fine to leave for now — Task 6 replaces `Program.cs` content and deletes them).

- [ ] **Step 6: Commit**

```bash
git add LogsPlatform.sln src/ tests/
git commit -m "Scaffold LogsPlatform solution (Domain/Infrastructure/Web/Tests)"
```

---

### Task 2: Domain entities — `Application` and `AppEnvironment`

**Files:**
- Create: `src/LogsPlatform.Domain/Entities/Application.cs`
- Create: `src/LogsPlatform.Domain/Entities/AppEnvironment.cs`
- Create: `src/LogsPlatform.Domain/Repositories/IApplicationRepository.cs`
- Create: `src/LogsPlatform.Domain/Repositories/IAppEnvironmentRepository.cs`

**Interfaces:**
- Consumes: nothing (this is the base of the dependency graph).
- Produces: `Application`, `AppEnvironment` entity classes and `IApplicationRepository`, `IAppEnvironmentRepository` interfaces that Task 3/4 implement against.

- [ ] **Step 1: Write the `Application` entity**

```csharp
// src/LogsPlatform.Domain/Entities/Application.cs
namespace LogsPlatform.Domain.Entities;

public class Application
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<AppEnvironment> Environments { get; set; } = new List<AppEnvironment>();
}
```

- [ ] **Step 2: Write the `AppEnvironment` entity**

```csharp
// src/LogsPlatform.Domain/Entities/AppEnvironment.cs
namespace LogsPlatform.Domain.Entities;

public class AppEnvironment
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public bool IsProduction { get; set; }
}
```

- [ ] **Step 3: Write the repository interfaces**

```csharp
// src/LogsPlatform.Domain/Repositories/IApplicationRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IApplicationRepository
{
    Task<Application?> GetByIdAsync(int id);
    Task<IReadOnlyList<Application>> GetAllAsync();
    Task<Application> AddAsync(Application application);
}
```

```csharp
// src/LogsPlatform.Domain/Repositories/IAppEnvironmentRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IAppEnvironmentRepository
{
    Task<AppEnvironment?> GetByIdAsync(int id);
    Task<IReadOnlyList<AppEnvironment>> GetByApplicationIdAsync(int applicationId);
    Task<AppEnvironment> AddAsync(AppEnvironment environment);
}
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Domain/Entities/ src/LogsPlatform.Domain/Repositories/
git commit -m "Add Application and AppEnvironment domain entities + repository interfaces"
```

---

### Task 3: `LogsPlatformDbContext` + first migration

**Files:**
- Create: `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs`
- Create: `src/LogsPlatform.Infrastructure/LogsPlatformDbContextFactory.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/DatabaseCollection.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs`

**Interfaces:**
- Consumes: `Application`, `AppEnvironment` from Task 2.
- Produces: `LogsPlatformDbContext` with `DbSet<Application> Applications` and `DbSet<AppEnvironment> AppEnvironments`, used by Task 4's repositories.

**Design note:** migration generation needs a `DbContext` the `dotnet ef` CLI can construct *without* the Web project being wired up yet (that only happens in Task 5). `LogsPlatformDbContextFactory` (`IDesignTimeDbContextFactory<T>`) solves this — it's a design-time-only factory that EF's tooling discovers automatically, decoupling migration generation from `Program.cs`. The connection string in it is LocalDB with `Trusted_Connection=True` (Windows Integrated auth, no password/credential material) — safe to commit, same reasoning that makes it fine to hardcode in tests above.

**Test-isolation note:** every test class in this plan shares one `LogsPlatformTests` LocalDB database and calls `EnsureDeleted()`+`Migrate()` at the start of each test. xUnit runs different test *classes* in parallel by default — two classes doing `EnsureDeleted()` against the same database concurrently would be flaky/racy. Fix: a shared `[CollectionDefinition]` forces every test class tagged with it to run sequentially relative to each other (collections run in parallel with *other* collections, but tests inside one collection run in order). Add this once, tag every DB-touching test class with it from here on (Task 4 and Task 6's test classes too).

```csharp
// tests/LogsPlatform.Tests/Infrastructure/DatabaseCollection.cs
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[CollectionDefinition("Database", DisableParallelization = true)]
public class DatabaseCollection
{
}
```

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class LogsPlatformDbContextTests
{
    private const string TestConnectionString =
        "Server=(localdb)\\mssqllocaldb;Database=LogsPlatformTests;Trusted_Connection=True;";

    private static LogsPlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>()
            .UseSqlServer(TestConnectionString)
            .Options;
        var context = new LogsPlatformDbContext(options);
        context.Database.EnsureDeleted();
        context.Database.Migrate();
        return context;
    }

    [Fact]
    public async Task CanInsertAndRetrieveApplicationWithEnvironment()
    {
        using var context = CreateContext();

        var application = new Application
        {
            Name = "RetailPulse",
            Description = "E-commerce simulation app",
            CreatedAt = DateTime.UtcNow
        };
        application.Environments.Add(new AppEnvironment { Name = "Production", IsProduction = true });

        context.Applications.Add(application);
        await context.SaveChangesAsync();

        using var readContext = new LogsPlatformDbContext(
            new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestConnectionString).Options);

        var loaded = await readContext.Applications
            .Include(a => a.Environments)
            .FirstAsync(a => a.Name == "RetailPulse");

        Assert.Single(loaded.Environments);
        Assert.Equal("Production", loaded.Environments.First().Name);
        Assert.True(loaded.Environments.First().IsProduction);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter CanInsertAndRetrieveApplicationWithEnvironment`
Expected: FAIL — build error, `LogsPlatformDbContext` does not exist yet.

- [ ] **Step 3: Write the `DbContext`**

```csharp
// src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs
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
    }
}
```

- [ ] **Step 4: Write the design-time factory**

```csharp
// src/LogsPlatform.Infrastructure/LogsPlatformDbContextFactory.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LogsPlatform.Infrastructure;

// Used only by the `dotnet ef` CLI to construct the DbContext for migrations,
// independent of LogsPlatform.Web's runtime DI setup.
public class LogsPlatformDbContextFactory : IDesignTimeDbContextFactory<LogsPlatformDbContext>
{
    public LogsPlatformDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LogsPlatformDbContext>();
        optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=LogsPlatformDev;Trusted_Connection=True;");
        return new LogsPlatformDbContext(optionsBuilder.Options);
    }
}
```

- [ ] **Step 5: Install the EF Core CLI tool (once per machine) and generate the migration**

```bash
dotnet tool install --global dotnet-ef --version 8.* 2>/dev/null || dotnet tool update --global dotnet-ef --version 8.*
dotnet ef migrations add InitialCreate \
  --project src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj \
  --startup-project src/LogsPlatform.Infrastructure/LogsPlatform.Infrastructure.csproj
```

Expected: creates `src/LogsPlatform.Infrastructure/Migrations/<timestamp>_InitialCreate.cs` and `LogsPlatformDbContextModelSnapshot.cs`. This works standalone — it does not need `LogsPlatform.Web` at all, thanks to the factory in Step 4.

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test --filter CanInsertAndRetrieveApplicationWithEnvironment`
Expected: PASS (creates and migrates a real `LogsPlatformTests` database on LocalDB).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs src/LogsPlatform.Infrastructure/LogsPlatformDbContextFactory.cs src/LogsPlatform.Infrastructure/Migrations/ tests/LogsPlatform.Tests/Infrastructure/
git commit -m "Add LogsPlatformDbContext with Application/AppEnvironment mapping + InitialCreate migration"
```

---

### Task 4: Repository implementations

**Files:**
- Create: `src/LogsPlatform.Infrastructure/Repositories/ApplicationRepository.cs`
- Create: `src/LogsPlatform.Infrastructure/Repositories/AppEnvironmentRepository.cs`
- Create: `tests/LogsPlatform.Tests/Infrastructure/ApplicationRepositoryTests.cs`

**Interfaces:**
- Consumes: `IApplicationRepository`, `IAppEnvironmentRepository` (Task 2), `LogsPlatformDbContext` (Task 3).
- Produces: `ApplicationRepository`, `AppEnvironmentRepository` — registered in DI by Task 5, consumed by Task 6's controllers.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/ApplicationRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ApplicationRepositoryTests
{
    private const string TestConnectionString =
        "Server=(localdb)\\mssqllocaldb;Database=LogsPlatformTests;Trusted_Connection=True;";

    private static LogsPlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>()
            .UseSqlServer(TestConnectionString)
            .Options;
        var context = new LogsPlatformDbContext(options);
        context.Database.EnsureDeleted();
        context.Database.Migrate();
        return context;
    }

    [Fact]
    public async Task AddAsync_PersistsApplication_RetrievableByGetByIdAsync()
    {
        using var context = CreateContext();
        var repository = new ApplicationRepository(context);

        var created = await repository.AddAsync(new Application
        {
            Name = "FieldOps",
            Description = "Field-service scheduling simulation app",
            CreatedAt = DateTime.UtcNow
        });

        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("FieldOps", loaded!.Name);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllPersistedApplications()
    {
        using var context = CreateContext();
        var repository = new ApplicationRepository(context);

        await repository.AddAsync(new Application { Name = "RetailPulse", CreatedAt = DateTime.UtcNow });
        await repository.AddAsync(new Application { Name = "FieldOps", CreatedAt = DateTime.UtcNow });

        var all = await repository.GetAllAsync();

        Assert.Equal(2, all.Count);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ApplicationRepositoryTests`
Expected: FAIL — `ApplicationRepository` does not exist yet.

- [ ] **Step 3: Implement the repositories**

```csharp
// src/LogsPlatform.Infrastructure/Repositories/ApplicationRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ApplicationRepository : IApplicationRepository
{
    private readonly LogsPlatformDbContext _context;

    public ApplicationRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<Application?> GetByIdAsync(int id) =>
        await _context.Applications.FindAsync(id);

    public async Task<IReadOnlyList<Application>> GetAllAsync() =>
        await _context.Applications.AsNoTracking().ToListAsync();

    public async Task<Application> AddAsync(Application application)
    {
        _context.Applications.Add(application);
        await _context.SaveChangesAsync();
        return application;
    }
}
```

```csharp
// src/LogsPlatform.Infrastructure/Repositories/AppEnvironmentRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class AppEnvironmentRepository : IAppEnvironmentRepository
{
    private readonly LogsPlatformDbContext _context;

    public AppEnvironmentRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<AppEnvironment?> GetByIdAsync(int id) =>
        await _context.AppEnvironments.FindAsync(id);

    public async Task<IReadOnlyList<AppEnvironment>> GetByApplicationIdAsync(int applicationId) =>
        await _context.AppEnvironments
            .AsNoTracking()
            .Where(e => e.ApplicationId == applicationId)
            .ToListAsync();

    public async Task<AppEnvironment> AddAsync(AppEnvironment environment)
    {
        _context.AppEnvironments.Add(environment);
        await _context.SaveChangesAsync();
        return environment;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ApplicationRepositoryTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Infrastructure/Repositories/ tests/LogsPlatform.Tests/Infrastructure/ApplicationRepositoryTests.cs
git commit -m "Implement ApplicationRepository and AppEnvironmentRepository"
```

---

### Task 5: Wire up `Program.cs`, DI, and User Secrets

**Files:**
- Modify: `src/LogsPlatform.Web/Program.cs`
- Modify: `src/LogsPlatform.Web/LogsPlatform.Web.csproj` (add `Microsoft.EntityFrameworkCore.SqlServer` package + `UserSecretsId`)

**Interfaces:**
- Consumes: `LogsPlatformDbContext` (Task 3), `IApplicationRepository`/`ApplicationRepository`, `IAppEnvironmentRepository`/`AppEnvironmentRepository` (Task 2/4).
- Produces: a running `LogsPlatform.Web` process with DI fully wired for `LogsPlatformDbContext`, `IApplicationRepository`, and `IAppEnvironmentRepository` — this is what makes Task 6's controllers (and their `WebApplicationFactory` tests) resolvable without any "come back later" step.

**Why this comes before the controllers:** `WebApplicationFactory<Program>`-based tests boot the *whole* `Program.cs` DI container. If the controllers in Task 6 existed before DI is wired, their tests would fail on every run with a DI resolution error, not just conditionally — wiring DI first means Task 6's tests pass cleanly the first time.

- [ ] **Step 1: Add the SQL Server package and enable User Secrets**

```bash
dotnet add src/LogsPlatform.Web/LogsPlatform.Web.csproj package Microsoft.EntityFrameworkCore.SqlServer --version 8.0.10
cd src/LogsPlatform.Web && dotnet user-secrets init && cd ../..
```

- [ ] **Step 2: Set the local dev connection string in User Secrets (not committed)**

```bash
cd src/LogsPlatform.Web
dotnet user-secrets set "ConnectionStrings:LogsPlatformDb" "Server=(localdb)\mssqllocaldb;Database=LogsPlatformDev;Trusted_Connection=True;"
cd ../..
```

- [ ] **Step 3: Rewrite `Program.cs` to register the `DbContext` and repositories**

```csharp
// src/LogsPlatform.Web/Program.cs
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<LogsPlatformDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("LogsPlatformDb")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:LogsPlatformDb configuration.")));

builder.Services.AddScoped<IApplicationRepository, ApplicationRepository>();
builder.Services.AddScoped<IAppEnvironmentRepository, AppEnvironmentRepository>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapControllers();

app.Run();

public partial class Program
{
} // exposes Program for WebApplicationFactory<Program> in tests
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Re-run the full test suite**

Run: `dotnet test`
Expected: PASS — the 3 tests from Tasks 3–4 (`CanInsertAndRetrieveApplicationWithEnvironment`, `AddAsync_PersistsApplication_RetrievableByGetByIdAsync`, `GetAllAsync_ReturnsAllPersistedApplications`). No controller tests exist yet — those are Task 6.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Program.cs src/LogsPlatform.Web/LogsPlatform.Web.csproj
git commit -m "Wire up DI, DbContext, and User Secrets for LogsPlatform.Web"
```

---

### Task 6: Admin API — Applications and Environments controllers

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/ApplicationContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/ApplicationsController.cs`
- Create: `src/LogsPlatform.Web/Controllers/EnvironmentsController.cs`
- Delete: `src/LogsPlatform.Web/Controllers/WeatherForecastController.cs` (template sample)
- Delete: `src/LogsPlatform.Web/WeatherForecast.cs` (template sample)
- Create: `tests/LogsPlatform.Tests/Web/TestWebApplicationFactory.cs`
- Create: `tests/LogsPlatform.Tests/Web/ApplicationsControllerTests.cs`

**Interfaces:**
- Consumes: `IApplicationRepository`, `IAppEnvironmentRepository` (Task 2/4), DI wiring (Task 5).
- Produces: `POST/GET /api/v1/admin/applications`, `POST/GET /api/v1/admin/applications/{appId}/environments` per `07-Ingestion-ו-API.md` section 5.

**Test-isolation note:** the default `WebApplicationFactory<Program>` boots the app with whatever `Program.cs` configures — after Task 5, that's the persistent dev database via User Secrets. Hitting that directly would mean re-running this test twice creates two `Application` rows named "RetailPulse", violating the unique index from Task 3 and failing the *second* run. `TestWebApplicationFactory` below overrides the `DbContext` registration to point at the same `LogsPlatformTests` database the other tests use, and resets it — keeping this test repeatable. It shares the `Database` collection so it never races the Task 3/4 tests over that database.

- [ ] **Step 1: Remove the template sample files**

```bash
rm src/LogsPlatform.Web/Controllers/WeatherForecastController.cs
rm src/LogsPlatform.Web/WeatherForecast.cs
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/LogsPlatform.Tests/Web/TestWebApplicationFactory.cs
using LogsPlatform.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LogsPlatform.Tests.Web;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string TestConnectionString =
        "Server=(localdb)\\mssqllocaldb;Database=LogsPlatformTests;Trusted_Connection=True;";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<LogsPlatformDbContext>>();
            services.AddDbContext<LogsPlatformDbContext>(options => options.UseSqlServer(TestConnectionString));

            using var scope = services.BuildServiceProvider().CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
            context.Database.EnsureDeleted();
            context.Database.Migrate();
        });
    }
}
```

```csharp
// tests/LogsPlatform.Tests/Web/ApplicationsControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class ApplicationsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ApplicationsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostThenGet_CreatesAndReturnsApplication()
    {
        var client = _factory.CreateClient();

        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/applications",
            new CreateApplicationRequest("RetailPulse", "E-commerce simulation app"));

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.NotNull(created);
        Assert.Equal("RetailPulse", created!.Name);

        var getResponse = await client.GetAsync($"/api/v1/admin/applications/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.Equal(created.Id, fetched!.Id);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test --filter ApplicationsControllerTests`
Expected: FAIL — `CreateApplicationRequest`/`ApplicationResponse`/controllers do not exist yet.

- [ ] **Step 4: Write the request/response contracts**

```csharp
// src/LogsPlatform.Web/Contracts/ApplicationContracts.cs
namespace LogsPlatform.Web.Contracts;

public record CreateApplicationRequest(string Name, string? Description);

public record ApplicationResponse(int Id, string Name, string? Description, DateTime CreatedAt);

public record CreateEnvironmentRequest(string Name, bool IsProduction);

public record EnvironmentResponse(int Id, int ApplicationId, string Name, bool IsProduction);
```

- [ ] **Step 5: Write the `ApplicationsController`**

```csharp
// src/LogsPlatform.Web/Controllers/ApplicationsController.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications")]
public class ApplicationsController : ControllerBase
{
    private readonly IApplicationRepository _applications;

    public ApplicationsController(IApplicationRepository applications)
    {
        _applications = applications;
    }

    [HttpPost]
    public async Task<ActionResult<ApplicationResponse>> Create(CreateApplicationRequest request)
    {
        var application = await _applications.AddAsync(new Application
        {
            Name = request.Name,
            Description = request.Description,
            CreatedAt = DateTime.UtcNow
        });

        var response = new ApplicationResponse(application.Id, application.Name, application.Description, application.CreatedAt);
        return CreatedAtAction(nameof(GetById), new { id = application.Id }, response);
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

- [ ] **Step 6: Write the `EnvironmentsController`**

```csharp
// src/LogsPlatform.Web/Controllers/EnvironmentsController.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/environments")]
public class EnvironmentsController : ControllerBase
{
    private readonly IAppEnvironmentRepository _environments;

    public EnvironmentsController(IAppEnvironmentRepository environments)
    {
        _environments = environments;
    }

    [HttpPost]
    public async Task<ActionResult<EnvironmentResponse>> Create(int appId, CreateEnvironmentRequest request)
    {
        var environment = await _environments.AddAsync(new AppEnvironment
        {
            ApplicationId = appId,
            Name = request.Name,
            IsProduction = request.IsProduction
        });

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

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test --filter ApplicationsControllerTests`
Expected: PASS — DI was already wired in Task 5, so this passes cleanly on the first try.

- [ ] **Step 8: Manual end-to-end smoke check**

```bash
dotnet run --project src/LogsPlatform.Web
```

In another terminal:
```bash
curl -X POST https://localhost:<port>/api/v1/admin/applications \
  -H "Content-Type: application/json" \
  -d '{"name":"RetailPulse","description":"E-commerce simulation app"}' -k
```
Expected: `201 Created` with the created `Application` JSON. Stop the running app (Ctrl+C) after confirming.

- [ ] **Step 9: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/ src/LogsPlatform.Web/Controllers/ApplicationsController.cs src/LogsPlatform.Web/Controllers/EnvironmentsController.cs tests/LogsPlatform.Tests/Web/
git rm src/LogsPlatform.Web/Controllers/WeatherForecastController.cs src/LogsPlatform.Web/WeatherForecast.cs
git commit -m "Add Applications and Environments admin API controllers"
```

---

## Self-Review Notes

- **Spec coverage:** This plan covers the `Application`/`AppEnvironment` slice of `05-מודל-נתונים.md` section 2 and the corresponding two endpoints of `07-Ingestion-ו-API.md` section 5. It does **not** yet cover Module/ScreenService/ProcessNode/Operation/Customer/AppUser/LogSource/ApiKey/AppVersion/Deployment, `IsActive` soft-delete semantics (not applicable to `Application`/`AppEnvironment` per the data model), or any Blazor UI — those are the next plan(s) in the M1 milestone, following the exact same pattern established here (entity → repository interface → `DbContext` mapping/migration → repository implementation → DI wiring → controller, each test-first).
- **Type consistency:** `IApplicationRepository`/`IAppEnvironmentRepository` signatures introduced in Task 2 are used identically (same method names/return types) by `ApplicationRepository`/`AppEnvironmentRepository` in Task 4 and consumed identically by the controllers in Task 6 — verified by re-reading each task above.
- **Task-order correctness (caught on self-review):** the original draft had DI wiring (formerly Task 6) *after* the controllers (formerly Task 5), which meant the controller test's "run test to verify it passes" step would fail deterministically, and Task 3's migration-generation step needed the Web project wired before it existed. Fixed by adding a design-time `DbContext` factory (Task 3) so migrations don't depend on `Program.cs` at all, and reordering so DI wiring (Task 5) precedes the controllers that need it (Task 6). Also fixed: the controller test originally hit the persistent dev database with no reset, which would fail on a second test run due to the unique index on `Application.Name` — fixed with `TestWebApplicationFactory` pointing at the isolated, reset-per-run `LogsPlatformTests` database. Also added a shared xUnit `[CollectionDefinition("Database")]` so the three DB-touching test classes (which all reset the same LocalDB database) don't race each other under xUnit's default cross-class parallelism.
- **No placeholders:** every step has complete, runnable code or an exact command with an expected result.

## Next Plans (not in this one — YAGNI)

1. Replicate this exact pattern for `Module → ScreenService → ProcessNode → Operation` (with `IsActive` soft-delete per `06-מודל-אפליקציה.md` section 3) and for `Customer`/`AppUser`/`LogSource`/`ApiKey`/`AppVersion`/`Deployment`.
2. Minimal Blazor Server admin UI over these APIs (M1 tail end, per `12-תוכנית-עבודה-ואבני-דרך.md`).
3. `LogsPlatform.Client` + Ingestion API (M2).
