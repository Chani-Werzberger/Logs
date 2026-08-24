# M6: Security Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add real username/password login (cookie auth), an admin/non-admin authorization split covering every Admin/Query/Findings endpoint and every Blazor page, an authenticated audit trail for Promote-to-Conclusion, a redaction hook in the Client library, and a pre-commit secrets scan — closing M6's scope per `docs/superpowers/specs/2026-08-23-m6-security-hardening-design.md`.

**Architecture:** A new top-level `PlatformUser` entity (distinct from the existing per-Application `AppUser`) backed by custom PBKDF2 hashing; ASP.NET Core cookie authentication as the default scheme (the existing `X-Api-Key` scheme on `IngestionController` stays untouched, already scheme-qualified); a global fallback authorization policy gates every controller automatically, a `RequireAdmin` policy gates Admin controllers explicitly; Blazor Server pages are gated individually via `@attribute [Authorize]` on each page plus `AuthorizeRouteView` in `Routes.razor`, since Blazor's router does not honor MVC's fallback-policy mechanism. Every existing `WebApplicationFactory`-based test that hits a now-gated endpoint is migrated to a shared `AuthenticatedTestClientHelper`.

**Tech Stack:** .NET 10, EF Core 10, SQL Server (real DB in tests, no mocking), ASP.NET Core Cookie Authentication, `Rfc2898DeriveBytes.Pbkdf2` (no new NuGet package), Blazor Server (Interactive Server render mode), xUnit.

## Global Constraints

- Real SQL Server only in tests — no InMemory provider, no mocking of `LogsPlatformDbContext`. Use `[Collection("Database")]` and `TestDatabase.CreateContext()` / `TestDatabase.ConnectionString` per the established convention.
- **Verification `DbContext` pitfall:** any test reading back data written through a separately-created `DbContext`/hosted server MUST build the verification context via `new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options` directly — never call `TestDatabase.CreateContext()` a second time (it wipes the DB via `EnsureDeleted`+`Migrate`).
- Password hashing: PBKDF2 via `Rfc2898DeriveBytes.Pbkdf2`, SHA-256, 100,000 iterations, 16-byte salt, 32-byte output — no new NuGet dependency.
- `PlatformUser` is top-level (no `ApplicationId`), unrelated to `AppUser` (a per-Application end-user).
- `IngestionController`'s existing `[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]` (verified present at `src/LogsPlatform.Web/Controllers/IngestionController.cs:15`) is not touched by this plan.
- Uniqueness violations on `DbUpdateException` are detected via the existing `ex.IsUniqueViolation()` extension (`src/LogsPlatform.Web/DbUpdateExceptionExtensions.cs`) — the established convention across every admin controller/section in this codebase. Do not reintroduce the older `SqlException { Number: 2601 or 2627 }` pattern.
- Hebrew UI throughout (all new UI text), except the "LogsPlatform" brand name and real domain data — matching every prior milestone.
- No mixed positional/named C# arguments.
- `DomainFixture` (`tests/SyntheticDataGenerator/DomainFixture.cs`) is **not modified** — its methods already take `HttpClient` as an external parameter. The decision locked in for this plan: **callers authenticate the `HttpClient` before passing it to `DomainFixture`**, never the other way around. This keeps `DomainFixture` a pure "build domain objects via this client" utility with no auth awareness.
- Commit after each task, following this project's established frequent-commit convention.

---

## Task 1: `PlatformUser` entity, `PasswordHasher`, repository, DbContext wiring

**Files:**
- Create: `src/LogsPlatform.Domain/Entities/PlatformUser.cs`
- Create: `src/LogsPlatform.Domain/Repositories/IPlatformUserRepository.cs`
- Create: `src/LogsPlatform.Infrastructure/Repositories/PlatformUserRepository.cs`
- Create: `src/LogsPlatform.Infrastructure/PasswordHasher.cs`
- Modify: `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs`
- Modify: `src/LogsPlatform.Web/Program.cs`
- Test: `tests/LogsPlatform.Tests/Infrastructure/PasswordHasherTests.cs`
- Test: `tests/LogsPlatform.Tests/Infrastructure/PlatformUserRepositoryTests.cs`

**Interfaces:**
- Produces: `PlatformUser { int Id; string Username; string PasswordHash; bool IsAdmin; bool IsActive; DateTime CreatedAt; }`
- Produces: `PasswordHasher.Hash(string password) -> string`, `PasswordHasher.Verify(string password, string encodedHash) -> bool`
- Produces: `IPlatformUserRepository.GetByUsernameAsync(string username) -> Task<PlatformUser?>`, `GetAllAsync() -> Task<IReadOnlyList<PlatformUser>>`, `AddAsync(PlatformUser user) -> Task<PlatformUser>`, `DeactivateAsync(int id) -> Task`, `AnyAsync() -> Task<bool>` (used by startup seeding to check whether any row exists at all, including inactive ones).

- [ ] **Step 1: Write the failing test for `PasswordHasher`**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/PasswordHasherTests.cs
using LogsPlatform.Infrastructure;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_SamePasswordTwice_ProducesDifferentHashes()
    {
        var first = PasswordHasher.Hash("correct horse battery staple");
        var second = PasswordHasher.Hash("correct horse battery staple");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Verify_CorrectPassword_ReturnsTrue()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        Assert.False(PasswordHasher.Verify("wrong password", hash));
    }

    [Fact]
    public void Hash_NeverEqualsThePlaintextPassword()
    {
        var password = "correct horse battery staple";
        var hash = PasswordHasher.Hash(password);

        Assert.NotEqual(password, hash);
        Assert.DoesNotContain(password, hash);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~PasswordHasherTests"`
Expected: FAIL (build error — `PasswordHasher` does not exist)

- [ ] **Step 3: Write `PasswordHasher`**

```csharp
// src/LogsPlatform.Infrastructure/PasswordHasher.cs
using System.Security.Cryptography;

namespace LogsPlatform.Infrastructure;

public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encodedHash)
    {
        var parts = encodedHash.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expectedHash = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~PasswordHasherTests"`
Expected: PASS (4/4)

- [ ] **Step 5: Create the `PlatformUser` entity**

```csharp
// src/LogsPlatform.Domain/Entities/PlatformUser.cs
namespace LogsPlatform.Domain.Entities;

public class PlatformUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
```

- [ ] **Step 6: Create `IPlatformUserRepository`**

```csharp
// src/LogsPlatform.Domain/Repositories/IPlatformUserRepository.cs
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IPlatformUserRepository
{
    Task<PlatformUser?> GetByUsernameAsync(string username);
    Task<IReadOnlyList<PlatformUser>> GetAllAsync();
    Task<PlatformUser> AddAsync(PlatformUser user);
    Task DeactivateAsync(int id);
    Task<bool> AnyAsync();
}
```

- [ ] **Step 7: Write the failing repository test**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/PlatformUserRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class PlatformUserRepositoryTests
{
    private static LogsPlatformDbContext CreateUntrackedContext()
    {
        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString)
            .Options;
        return new LogsPlatformDbContext(options);
    }

    [Fact]
    public async Task AddAsync_PersistsUser_PasswordHashIsNeverThePlaintextPassword()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new PlatformUserRepository(context);
        const string plaintextPassword = "correct horse battery staple";

        var created = await repository.AddAsync(new PlatformUser
        {
            Username = "PlatformUserAddTest",
            PasswordHash = PasswordHasher.Hash(plaintextPassword),
            IsAdmin = true,
            CreatedAt = DateTime.UtcNow
        });

        await using var verify = CreateUntrackedContext();
        var loaded = await verify.Set<PlatformUser>().SingleAsync(u => u.Id == created.Id);

        Assert.NotEqual(plaintextPassword, loaded.PasswordHash);
        Assert.DoesNotContain(plaintextPassword, loaded.PasswordHash);
        Assert.True(PasswordHasher.Verify(plaintextPassword, loaded.PasswordHash));
    }

    [Fact]
    public async Task GetByUsernameAsync_ExistingUser_ReturnsIt()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new PlatformUserRepository(context);
        await repository.AddAsync(new PlatformUser
        {
            Username = "PlatformUserLookupTest",
            PasswordHash = PasswordHasher.Hash("irrelevant"),
            CreatedAt = DateTime.UtcNow
        });

        var found = await repository.GetByUsernameAsync("PlatformUserLookupTest");

        Assert.NotNull(found);
        Assert.Equal("PlatformUserLookupTest", found!.Username);
    }

    [Fact]
    public async Task GetByUsernameAsync_UnknownUsername_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new PlatformUserRepository(context);

        var found = await repository.GetByUsernameAsync("no-such-user");

        Assert.Null(found);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new PlatformUserRepository(context);
        var created = await repository.AddAsync(new PlatformUser
        {
            Username = "PlatformUserDeactivateTest",
            PasswordHash = PasswordHasher.Hash("irrelevant"),
            CreatedAt = DateTime.UtcNow
        });

        await repository.DeactivateAsync(created.Id);

        await using var verify = CreateUntrackedContext();
        var loaded = await verify.Set<PlatformUser>().SingleAsync(u => u.Id == created.Id);
        Assert.False(loaded.IsActive);
    }

    [Fact]
    public async Task AnyAsync_NoUsers_ReturnsFalse()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new PlatformUserRepository(context);

        Assert.False(await repository.AnyAsync());
    }

    [Fact]
    public async Task AnyAsync_OneUserExists_ReturnsTrue()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new PlatformUserRepository(context);
        await repository.AddAsync(new PlatformUser
        {
            Username = "PlatformUserAnyTest",
            PasswordHash = PasswordHasher.Hash("irrelevant"),
            CreatedAt = DateTime.UtcNow
        });

        Assert.True(await repository.AnyAsync());
    }
}
```

- [ ] **Step 8: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~PlatformUserRepositoryTests"`
Expected: FAIL (build error — `PlatformUserRepository` does not exist, `PlatformUsers` DbSet does not exist)

- [ ] **Step 9: Implement `PlatformUserRepository`**

```csharp
// src/LogsPlatform.Infrastructure/Repositories/PlatformUserRepository.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class PlatformUserRepository : IPlatformUserRepository
{
    private readonly LogsPlatformDbContext _context;

    public PlatformUserRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<PlatformUser?> GetByUsernameAsync(string username) =>
        await _context.PlatformUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username);

    public async Task<IReadOnlyList<PlatformUser>> GetAllAsync() =>
        await _context.PlatformUsers.AsNoTracking().OrderBy(u => u.Username).ToListAsync();

    public async Task<PlatformUser> AddAsync(PlatformUser user)
    {
        _context.PlatformUsers.Add(user);
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
        var user = await _context.PlatformUsers.FindAsync(id)
            ?? throw new InvalidOperationException($"PlatformUser {id} not found.");

        await _context.Entry(user).ReloadAsync();

        if (!user.IsActive)
        {
            return;
        }

        user.IsActive = false;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(user).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<bool> AnyAsync() =>
        await _context.PlatformUsers.AnyAsync();
}
```

- [ ] **Step 10: Add `PlatformUser` to `LogsPlatformDbContext`**

In `src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs`, add a `DbSet` after the `Evidence` line (line 29):

```csharp
    public DbSet<Evidence> Evidence => Set<Evidence>();
    public DbSet<PlatformUser> PlatformUsers => Set<PlatformUser>();
```

And add a new `modelBuilder.Entity<PlatformUser>` block at the end of `OnModelCreating`, after the `Evidence` block (after line 233, before the closing brace of the method):

```csharp
        modelBuilder.Entity<PlatformUser>(entity =>
        {
            entity.Property(u => u.Username).HasMaxLength(200).IsRequired();
            entity.Property(u => u.PasswordHash).HasMaxLength(200).IsRequired();
            entity.HasIndex(u => u.Username).IsUnique();
        });
```

- [ ] **Step 11: Create and apply the EF Core migration**

Run: `dotnet ef migrations add AddPlatformUser --project src/LogsPlatform.Infrastructure --startup-project src/LogsPlatform.Web`
Expected: a new migration file under `src/LogsPlatform.Infrastructure/Migrations/` adding a `PlatformUsers` table with a unique index on `Username`.

Run: `dotnet ef database update --project src/LogsPlatform.Infrastructure --startup-project src/LogsPlatform.Web`
Expected: succeeds against the local dev database. (Tests use `EnsureDeleted()`+`Migrate()` per-factory, so this step is for local manual verification only — the migration file itself is what test databases pick up.)

- [ ] **Step 12: Register `IPlatformUserRepository` in `Program.cs`**

In `src/LogsPlatform.Web/Program.cs`, add after line 21 (`builder.Services.AddScoped<IApplicationRepository, ApplicationRepository>();`):

```csharp
builder.Services.AddScoped<IPlatformUserRepository, PlatformUserRepository>();
```

- [ ] **Step 13: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~PasswordHasherTests|FullyQualifiedName~PlatformUserRepositoryTests"`
Expected: PASS (10/10 — 4 PasswordHasher + 6 PlatformUserRepository)

- [ ] **Step 14: Commit**

```bash
git add src/LogsPlatform.Domain/Entities/PlatformUser.cs src/LogsPlatform.Domain/Repositories/IPlatformUserRepository.cs src/LogsPlatform.Infrastructure/Repositories/PlatformUserRepository.cs src/LogsPlatform.Infrastructure/PasswordHasher.cs src/LogsPlatform.Infrastructure/LogsPlatformDbContext.cs src/LogsPlatform.Infrastructure/Migrations/ src/LogsPlatform.Web/Program.cs tests/LogsPlatform.Tests/Infrastructure/PasswordHasherTests.cs tests/LogsPlatform.Tests/Infrastructure/PlatformUserRepositoryTests.cs
git commit -m "feat(m6): add PlatformUser entity, PasswordHasher, and repository"
```

---

## Task 2: `AuthController` (login/logout)

**Files:**
- Create: `src/LogsPlatform.Web/Contracts/AuthContracts.cs`
- Create: `src/LogsPlatform.Web/Controllers/AuthController.cs`
- Test: `tests/LogsPlatform.Tests/Web/AuthControllerTests.cs`

**Interfaces:**
- Consumes: `IPlatformUserRepository` (Task 1), `PasswordHasher.Verify` (Task 1)
- Produces: `LoginRequest(string Username, string Password)`; `POST /api/v1/auth/login` → `204` + `Set-Cookie` on success, `401` on bad credentials or inactive user; `POST /api/v1/auth/logout` → `204`, clears the cookie. Depends on cookie authentication being registered (Task 3) to actually issue/clear a working cookie — until Task 3 registers `AddCookie`, `HttpContext.SignInAsync`/`SignOutAsync` in this task will throw at runtime since no cookie handler is configured yet. This task's own test therefore has one test (`Login_InactiveUser_Returns401`) that doesn't depend on sign-in succeeding, and the success-path tests are deferred to Task 3's test file where cookie auth is live. Do not skip re-running this file's tests after Task 3.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LogsPlatform.Tests/Web/AuthControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class AuthControllerTests
{
    private static async Task SeedUserAsync(TestWebApplicationFactory factory, string username, string password, bool isAdmin, bool isActive = true)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        context.PlatformUsers.Add(new PlatformUser
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            IsAdmin = isAdmin,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Login_UnknownUsername_Returns401()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("no-such-user", "irrelevant"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "AuthWrongPasswordTest", "correct-password", isAdmin: false);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("AuthWrongPasswordTest", "wrong-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_InactiveUser_Returns401()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "AuthInactiveUserTest", "correct-password", isAdmin: false, isActive: false);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("AuthInactiveUserTest", "correct-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~AuthControllerTests"`
Expected: FAIL (build error — `LoginRequest` / `AuthController` don't exist; route 404s)

- [ ] **Step 3: Create the contract**

```csharp
// src/LogsPlatform.Web/Contracts/AuthContracts.cs
namespace LogsPlatform.Web.Contracts;

public record LoginRequest(string Username, string Password);
```

- [ ] **Step 4: Implement `AuthController`**

```csharp
// src/LogsPlatform.Web/Controllers/AuthController.cs
using System.Security.Claims;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly IPlatformUserRepository _users;

    public AuthController(IPlatformUserRepository users)
    {
        _users = users;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _users.GetByUsernameAsync(request.Username);
        if (user is null || !user.IsActive || !PasswordHasher.Verify(request.Password, user.PasswordHash))
        {
            return Unauthorized();
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("IsAdmin", user.IsAdmin ? "true" : "false")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return NoContent();
    }

    [HttpPost("logout")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }
}
```

`[IgnoreAntiforgeryToken]` on `Logout`: the site-wide `app.UseAntiforgery()` middleware (already present in `Program.cs`) validates antiforgery tokens on unsafe HTTP methods for endpoints that opt into it; the NavMenu logout form (Task 10) is a plain HTML form with no Blazor antiforgery wiring, and signing out carries no risk of mutating sensitive state beyond ending the current session, so this is a deliberate, narrow exception — not a template for other endpoints.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~AuthControllerTests"`
Expected: PASS (3/3)

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Contracts/AuthContracts.cs src/LogsPlatform.Web/Controllers/AuthController.cs tests/LogsPlatform.Tests/Web/AuthControllerTests.cs
git commit -m "feat(m6): add AuthController with login/logout"
```

---

## Task 3: Cookie authentication + authorization policies

**Files:**
- Modify: `src/LogsPlatform.Web/Program.cs`
- Modify: 12 Admin controllers (listed below) — add `[Authorize(Policy = "RequireAdmin")]`
- Modify: `tests/LogsPlatform.Tests/Web/AuthControllerTests.cs` — add the success-path tests deferred from Task 2
- Test: `tests/LogsPlatform.Tests/Web/RequireAdminPolicyTests.cs`

**Interfaces:**
- Consumes: `IPlatformUserRepository`, `PasswordHasher` (Task 1), `AuthController` (Task 2)
- Produces: cookie auth as the default scheme; a `RequireAdmin` authorization policy (claim `IsAdmin == "true"`); a fallback policy requiring any authenticated user for every endpoint without its own `[Authorize]`. `IngestionController` is unaffected — it already carries its own scheme-qualified `[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]`, which takes precedence over the fallback.

**The 12 Admin controllers to modify** (all under `/api/v1/admin/...`, all currently `[ApiController]` with no `[Authorize]` attribute):
`ApplicationsController.cs`, `EnvironmentsController.cs`, `ModulesController.cs`, `ScreenServicesController.cs`, `OperationsController.cs`, `ProcessesController.cs`, `AppUsersController.cs`, `CustomersController.cs`, `LogSourcesController.cs`, `ApiKeysController.cs`, `DeploymentsController.cs`, `VersionsController.cs` — all in `src/LogsPlatform.Web/Controllers/`.

`EventsController.cs`, `ExceptionGroupsController.cs`, `TimelineController.cs`, `FindingsController.cs` are **not** modified in this task — they get "any authenticated user" for free from the fallback policy, matching the design ("no per-Application permissions in V1 — every authenticated user sees every Application").

- [ ] **Step 1: Write the failing test for the RequireAdmin policy**

```csharp
// tests/LogsPlatform.Tests/Web/RequireAdminPolicyTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class RequireAdminPolicyTests
{
    private static async Task SeedUserAsync(TestWebApplicationFactory factory, string username, string password, bool isAdmin)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        context.PlatformUsers.Add(new PlatformUser
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            IsAdmin = isAdmin,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task AdminEndpoint_NoCookie_Returns401()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/applications/1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoint_NonAdminCookie_Returns403()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "RequireAdminNonAdminTest", "password123", isAdmin: false);
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("RequireAdminNonAdminTest", "password123"));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        var response = await client.GetAsync("/api/v1/admin/applications/1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoint_AdminCookie_ReachesTheEndpoint()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "RequireAdminAdminTest", "password123", isAdmin: true);
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("RequireAdminAdminTest", "password123"));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        var response = await client.GetAsync("/api/v1/admin/applications/1");

        // 404 (no Application with id 1), not 401/403 — proves the request reached the controller.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task QueryEndpoint_AnyAuthenticatedUser_ReachesTheEndpoint()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "RequireAdminQueryEndpointTest", "password123", isAdmin: false);
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("RequireAdminQueryEndpointTest", "password123"));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        var response = await client.GetAsync("/api/v1/findings?applicationId=1&environmentId=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task IngestionEndpoint_ApiKeyOnly_StillWorksWithoutACookie()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();

        // No login at all — proves the ApiKey scheme on IngestionController is unaffected by
        // cookie auth becoming the default scheme.
        var response = await client.PostAsJsonAsync("/api/v1/ingest/events", new List<object>());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // missing X-Api-Key, not a cookie redirect
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RequireAdminPolicyTests"`
Expected: FAIL — every Admin endpoint currently returns 404/200 regardless of auth (no gating exists yet); `AdminEndpoint_NoCookie_Returns401` fails because it currently returns 404.

- [ ] **Step 3: Register cookie authentication and authorization policies**

In `src/LogsPlatform.Web/Program.cs`, replace lines 16–18:

```csharp
builder.Services.AddAuthentication(ApiKeyAuthenticationOptions.SchemeName)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.SchemeName, options => { });
builder.Services.AddAuthorization();
```

with:

```csharp
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.SchemeName, options => { });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build())
    .AddPolicy("RequireAdmin", policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim("IsAdmin", "true"));
```

Add the required `using` at the top of `Program.cs`:

```csharp
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
```

- [ ] **Step 4: Add `[Authorize(Policy = "RequireAdmin")]` to each of the 12 Admin controllers**

For each of the 12 files listed above, add the attribute directly above the existing `[ApiController]`/`[Route(...)]` attributes on the controller class. Example for `ApplicationsController.cs`:

```csharp
[ApiController]
[Route("api/v1/admin/applications")]
[Authorize(Policy = "RequireAdmin")]
public class ApplicationsController : ControllerBase
```

Apply the identical one-line addition (`[Authorize(Policy = "RequireAdmin")]` placed alongside the existing `[ApiController]`/`[Route]` attributes) to the remaining 11 files: `EnvironmentsController.cs`, `ModulesController.cs`, `ScreenServicesController.cs`, `OperationsController.cs`, `ProcessesController.cs`, `AppUsersController.cs`, `CustomersController.cs`, `LogSourcesController.cs`, `ApiKeysController.cs`, `DeploymentsController.cs`, `VersionsController.cs`. Each needs `using Microsoft.AspNetCore.Authorization;` added if not already present — check each file's existing usings first (`ApiKeysController.cs`, confirmed above, does not import it yet).

- [ ] **Step 5: Add the deferred success-path tests to `AuthControllerTests.cs`**

Append to `tests/LogsPlatform.Tests/Web/AuthControllerTests.cs` (inside the `AuthControllerTests` class):

```csharp
    [Fact]
    public async Task Login_CorrectCredentials_Returns204AndSetsCookie()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "AuthLoginSuccessTest", "correct-password", isAdmin: false);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("AuthLoginSuccessTest", "correct-password"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.Contains("Set-Cookie") || response.Headers.TryGetValues("Set-Cookie", out _) || true);
    }

    [Fact]
    public async Task Logout_ThenPreviouslyAuthenticatedRequest_Returns401()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedUserAsync(factory, "AuthLogoutTest", "correct-password", isAdmin: false);
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("AuthLogoutTest", "correct-password"));

        var logoutResponse = await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var afterLogout = await client.GetAsync("/api/v1/findings?applicationId=1&environmentId=1");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }
```

(The `response.Headers.Contains("Set-Cookie") || ... || true` assertion in the first test is intentionally a no-op tautology placeholder pattern to avoid — replace it: `WebApplicationFactory`'s default `HttpClient` has cookie handling enabled, so the more meaningful assertion is the `Logout` test's round-trip below, which proves the cookie was actually set and later cleared. Remove the weak first assertion entirely and keep only `Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);` in `Login_CorrectCredentials_Returns204AndSetsCookie` — the round-trip test is the real proof.)

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RequireAdminPolicyTests|FullyQualifiedName~AuthControllerTests"`
Expected: FAIL still, for a different reason — `RequireAdminAdminTest`/`QueryEndpoint_AnyAuthenticatedUser_ReachesTheEndpoint` will 401 because `AuthController` itself is now gated by the new fallback policy (it has no `[Authorize]`/`[AllowAnonymous]` override reaching it correctly — actually it does: `AuthController` already carries `[AllowAnonymous]` from Task 2, which correctly bypasses the fallback). Investigate any remaining failure by reading the actual response body/status before assuming — do not guess. Common cause if `AdminEndpoint_AdminCookie_ReachesTheEndpoint` still fails: the claim value comparison is case-sensitive (`"true"` vs `"True"`) — `AuthController.Login` writes `user.IsAdmin ? "true" : "false"` (lowercase), matching the policy's `RequireClaim("IsAdmin", "true")` exactly; if a mismatch surfaces, align both to lowercase `"true"`.
Expected after any fix: PASS (5/5 RequireAdminPolicyTests, 5/5 AuthControllerTests)

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Program.cs src/LogsPlatform.Web/Controllers/ tests/LogsPlatform.Tests/Web/AuthControllerTests.cs tests/LogsPlatform.Tests/Web/RequireAdminPolicyTests.cs
git commit -m "feat(m6): cookie auth as default scheme, RequireAdmin policy on Admin controllers"
```

**Note:** running the full suite at this point (`dotnet test`) will show a large number of new failures across every other test file — expected and addressed in Tasks 6–8. Do not attempt to fix them here.

---

## Task 4: Blazor auth-state plumbing, `/login` page, `[Authorize]` on every existing page

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Routes.razor`
- Create: `src/LogsPlatform.Web/Components/RedirectToLogin.razor`
- Create: `src/LogsPlatform.Web/Components/Pages/Login.razor`
- Modify: 11 existing page files (listed below) — add `@attribute [Authorize]` or `@attribute [Authorize(Policy = "RequireAdmin")]`

**Interfaces:**
- Consumes: `AuthController` (Task 2), cookie auth (Task 3)
- Produces: every existing Blazor page gated; `/login` reachable without authentication.

Blazor's `AuthorizeRouteView` does **not** consult ASP.NET Core's `SetFallbackPolicy` the way MVC endpoint routing does — a routed page with no `[Authorize]` attribute is rendered regardless of authentication state. This is why every page needs its own explicit attribute rather than relying on Task 3's fallback policy (that fallback only protects the 4 Query/Findings controllers and the API surface — it does not reach Blazor page navigation at all).

**The 11 existing pages and their target attribute:**
| File | Attribute |
|---|---|
| `Home.razor` | `[Authorize]` |
| `Exceptions.razor` | `[Authorize]` |
| `ExceptionDetail.razor` | `[Authorize]` |
| `FindingDetail.razor` | `[Authorize]` |
| `Timeline.razor` | `[Authorize]` |
| `Search.razor` | `[Authorize]` |
| `ApplicationsAdmin.razor` | `[Authorize(Policy = "RequireAdmin")]` |
| `ModulesAdmin.razor` | `[Authorize(Policy = "RequireAdmin")]` |
| `ScreenServicesAdmin.razor` | `[Authorize(Policy = "RequireAdmin")]` |
| `ProcessesAdmin.razor` | `[Authorize(Policy = "RequireAdmin")]` |
| `OperationsAdmin.razor` | `[Authorize(Policy = "RequireAdmin")]` |

All 11 live in `src/LogsPlatform.Web/Components/Pages/`.

- [ ] **Step 1: Rewrite `Routes.razor` to gate via `AuthorizeRouteView`**

Replace the full contents of `src/LogsPlatform.Web/Components/Routes.razor`:

```razor
@using Microsoft.AspNetCore.Components.Authorization

<CascadingAuthenticationState>
    <Router AppAssembly="typeof(Program).Assembly">
        <Found Context="routeData">
            <ErrorBoundary>
                <ChildContent>
                    <AuthorizeRouteView RouteData="routeData" DefaultLayout="typeof(LogsPlatform.Web.Components.Layout.MainLayout)">
                        <NotAuthorized>
                            <RedirectToLogin />
                        </NotAuthorized>
                    </AuthorizeRouteView>
                </ChildContent>
                <ErrorContent>
                    <p>אירעה שגיאה בטעינת הדף. ייתכן שהקישור שגוי או שהדף כבר לא קיים.</p>
                </ErrorContent>
            </ErrorBoundary>
        </Found>
        <NotFound>
            <LayoutView Layout="typeof(LogsPlatform.Web.Components.Layout.MainLayout)">
                <p>הדף לא נמצא.</p>
            </LayoutView>
        </NotFound>
    </Router>
</CascadingAuthenticationState>
```

This covers both the unauthenticated case and the authenticated-but-wrong-policy case (e.g. non-admin hitting an admin page) — `AuthorizeRouteView` routes both to the same `NotAuthorized` template, which redirects to `/login`. A non-admin authenticated user landing back on `/login` after already being logged in is an accepted V1 simplification (no distinct "you don't have permission" page) — matching this milestone's "no granular RBAC" scope.

- [ ] **Step 2: Create `RedirectToLogin`**

```razor
@* src/LogsPlatform.Web/Components/RedirectToLogin.razor *@
@inject NavigationManager Navigation

@code {
    protected override void OnInitialized()
    {
        var returnUrl = Uri.EscapeDataString(Navigation.ToBaseRelativePath(Navigation.Uri));
        Navigation.NavigateTo($"/login?returnUrl={returnUrl}", forceLoad: true);
    }
}
```

- [ ] **Step 3: Create the `/login` page**

```razor
@* src/LogsPlatform.Web/Components/Pages/Login.razor *@
@page "/login"
@attribute [Microsoft.AspNetCore.Authorization.AllowAnonymous]

<h1>התחברות ל-LogsPlatform</h1>

<form id="login-form" class="mt-3" style="max-width:320px" autocomplete="on">
    <div class="mb-3">
        <label class="form-label" for="username">שם משתמש</label>
        <input id="username" name="username" class="form-control" required autocomplete="username" />
    </div>
    <div class="mb-3">
        <label class="form-label" for="password">סיסמה</label>
        <input id="password" name="password" type="password" class="form-control" required autocomplete="current-password" />
    </div>
    <button type="submit" class="btn btn-primary">התחבר/י</button>
    <div id="login-error" class="alert alert-danger mt-3" style="display:none" role="alert">שם משתמש או סיסמה שגויים.</div>
</form>

<script>
    (function () {
        var params = new URLSearchParams(window.location.search);
        var returnUrl = params.get('returnUrl') || '/';
        var form = document.getElementById('login-form');
        form.addEventListener('submit', function (e) {
            e.preventDefault();
            var errorBox = document.getElementById('login-error');
            errorBox.style.display = 'none';
            fetch('/api/v1/auth/login', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    username: document.getElementById('username').value,
                    password: document.getElementById('password').value
                })
            }).then(function (response) {
                if (response.ok) {
                    window.location.href = returnUrl;
                } else {
                    errorBox.style.display = 'block';
                }
            });
        });
    })();
</script>
```

This page deliberately uses a plain HTML form with vanilla JS `fetch()` instead of Blazor's `EditForm`/`@onclick` — `App.razor` forces `@rendermode="InteractiveServer"` on every routed page, and a Blazor Server login page cannot reliably call `HttpContext.SignInAsync` from within an already-established interactive circuit (there is no live HTTP response to attach a `Set-Cookie` header to at that point). Posting via `fetch()` to the real `AuthController.Login` endpoint sidesteps this entirely — it's a genuine HTTP round-trip that both `AuthenticatedTestClientHelper` (Task 6) and this page use identically.

- [ ] **Step 4: Add `@attribute [Authorize]` / `[Authorize(Policy = "RequireAdmin")]` to each of the 11 existing pages**

For the 6 `[Authorize]` pages (`Home.razor`, `Exceptions.razor`, `ExceptionDetail.razor`, `FindingDetail.razor`, `Timeline.razor`, `Search.razor`), add immediately below the `@page` directive:

```razor
@attribute [Microsoft.AspNetCore.Authorization.Authorize]
```

For the 5 `RequireAdmin` pages (`ApplicationsAdmin.razor`, `ModulesAdmin.razor`, `ScreenServicesAdmin.razor`, `ProcessesAdmin.razor`, `OperationsAdmin.razor`), add immediately below the `@page` directive:

```razor
@attribute [Microsoft.AspNetCore.Authorization.Authorize(Policy = "RequireAdmin")]
```

Example for `Home.razor` (currently `@page "/"` on line 2):

```razor
@page "/"
@attribute [Microsoft.AspNetCore.Authorization.Authorize]
```

Apply the identical pattern to the other 10 files per the table above.

- [ ] **Step 5: Manual verification (no automated test for Blazor page gating in this task — covered by Task 3's controller-level tests plus this manual check)**

Run: `dotnet run --project src/LogsPlatform.Web`
Navigate to `http://localhost:<port>/` in a browser.
Expected: redirected to `/login`. Attempting `/admin/applications` directly: also redirected to `/login`. This is a real UI check, not skippable — Blazor auth-state plumbing (`CascadingAuthenticationState`/`AuthorizeRouteView`) is exactly the kind of thing that silently no-ops when wired incorrectly, per this plan's Global Constraints risk note.

- [ ] **Step 6: Run the full existing suite to confirm no build breakage**

Run: `dotnet build`
Expected: builds clean (0 errors). Test failures from gated endpoints are still expected and addressed in Tasks 6–8 — this step only confirms compilation.

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Components/Routes.razor src/LogsPlatform.Web/Components/RedirectToLogin.razor src/LogsPlatform.Web/Components/Pages/Login.razor src/LogsPlatform.Web/Components/Pages/Home.razor src/LogsPlatform.Web/Components/Pages/Exceptions.razor src/LogsPlatform.Web/Components/Pages/ExceptionDetail.razor src/LogsPlatform.Web/Components/Pages/FindingDetail.razor src/LogsPlatform.Web/Components/Pages/Timeline.razor src/LogsPlatform.Web/Components/Pages/Search.razor src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor src/LogsPlatform.Web/Components/Pages/ModulesAdmin.razor src/LogsPlatform.Web/Components/Pages/ScreenServicesAdmin.razor src/LogsPlatform.Web/Components/Pages/ProcessesAdmin.razor src/LogsPlatform.Web/Components/Pages/OperationsAdmin.razor
git commit -m "feat(m6): gate every Blazor page via AuthorizeRouteView, add /login page"
```

---

## Task 5: Startup seeding of the default admin

**Files:**
- Modify: `src/LogsPlatform.Web/Program.cs`
- Test: `tests/LogsPlatform.Tests/Web/StartupSeedingTests.cs`

**Interfaces:**
- Consumes: `IPlatformUserRepository.AnyAsync()`, `AddAsync()` (Task 1), `PasswordHasher.Hash()` (Task 1)

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LogsPlatform.Tests/Web/StartupSeedingTests.cs
using LogsPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class StartupSeedingTests
{
    [Fact]
    public async Task FreshDatabase_SeedsExactlyOneAdminUser()
    {
        using var factory = new TestWebApplicationFactory();
        // Force host startup (and therefore the seeding block) by issuing one request.
        var client = factory.CreateClient();
        await client.GetAsync("/login");

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var users = await context.PlatformUsers.ToListAsync();

        Assert.Single(users);
        Assert.Equal("admin", users[0].Username);
        Assert.True(users[0].IsAdmin);
        Assert.True(users[0].IsActive);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~StartupSeedingTests"`
Expected: FAIL — `users` is empty, no seeding exists yet.

- [ ] **Step 3: Add seeding logic to `Program.cs`**

In `src/LogsPlatform.Web/Program.cs`, after `var app = builder.Build();` (line 56) and before the `if (app.Environment.IsDevelopment())` block:

```csharp
using (var scope = app.Services.CreateScope())
{
    var platformUsers = scope.ServiceProvider.GetRequiredService<IPlatformUserRepository>();
    if (!await platformUsers.AnyAsync())
    {
        var generatedPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
        await platformUsers.AddAsync(new PlatformUser
        {
            Username = "admin",
            PasswordHash = PasswordHasher.Hash(generatedPassword),
            IsAdmin = true,
            CreatedAt = DateTime.UtcNow
        });
        Console.WriteLine("=================================================================");
        Console.WriteLine("No PlatformUser exists yet — seeded a default admin account:");
        Console.WriteLine($"  Username: admin");
        Console.WriteLine($"  Password: {generatedPassword}");
        Console.WriteLine("This password is shown once and is not stored anywhere else.");
        Console.WriteLine("=================================================================");
    }
}
```

Add the required `using` directives at the top of `Program.cs`:

```csharp
using System.Security.Cryptography;
using LogsPlatform.Domain.Entities;
```

(`LogsPlatform.Domain.Repositories` and `LogsPlatform.Infrastructure` are already imported by earlier `using` lines in this file.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~StartupSeedingTests"`
Expected: PASS (1/1)

- [ ] **Step 5: Run the AuthControllerTests and RequireAdminPolicyTests again to confirm seeding doesn't interfere**

Run: `dotnet test --filter "FullyQualifiedName~AuthControllerTests|FullyQualifiedName~RequireAdminPolicyTests"`
Expected: PASS (still 5/5 and 5/5 — the seeded `admin` user has a random, unknown-to-tests password, so it cannot be logged into by any test and does not affect the seeded test users' own usernames, which are all distinct from `"admin"`).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Program.cs tests/LogsPlatform.Tests/Web/StartupSeedingTests.cs
git commit -m "feat(m6): seed a default admin PlatformUser on first startup"
```

---

## Task 6: `AuthenticatedTestClientHelper`

**Files:**
- Create: `tests/LogsPlatform.Tests/Infrastructure/AuthenticatedTestClientHelper.cs`
- Test: `tests/LogsPlatform.Tests/Infrastructure/AuthenticatedTestClientHelperTests.cs`

**Interfaces:**
- Consumes: `LogsPlatformDbContext.PlatformUsers` (Task 1), `PasswordHasher.Hash` (Task 1), `POST /api/v1/auth/login` (Task 2)
- Produces: `AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory) -> Task<HttpClient>` — seeds a fixed test-admin `PlatformUser` directly via `DbContext` if not already present (idempotent, safe to call once per test method against a shared `IClassFixture` factory), then performs a real `POST /api/v1/auth/login` and returns the resulting cookie-bearing client. This is the ONLY change every test file in Tasks 7–9 needs: replace `_factory.CreateClient()` with `await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory)` wherever the client goes on to call a gated endpoint.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/AuthenticatedTestClientHelperTests.cs
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Tests.Web;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class AuthenticatedTestClientHelperTests
{
    [Fact]
    public async Task CreateAuthenticatedClientAsync_ReturnedClientCanReachAGatedAdminEndpoint()
    {
        using var factory = new TestWebApplicationFactory();

        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(factory);
        var response = await client.GetAsync("/api/v1/admin/applications/1");

        // 404 (no Application with id 1), not 401/403 — proves the client is authenticated as an admin.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateAuthenticatedClientAsync_CalledTwiceOnSameFactory_DoesNotFailOnDuplicateSeed()
    {
        using var factory = new TestWebApplicationFactory();

        var first = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(factory);
        var second = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync("/api/v1/admin/applications/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await first.GetAsync("/api/v1/admin/applications/1")).StatusCode);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~AuthenticatedTestClientHelperTests"`
Expected: FAIL (build error — `AuthenticatedTestClientHelper` does not exist)

- [ ] **Step 3: Implement the helper**

```csharp
// tests/LogsPlatform.Tests/Infrastructure/AuthenticatedTestClientHelper.cs
using System.Net.Http.Json;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LogsPlatform.Tests.Infrastructure;

public static class AuthenticatedTestClientHelper
{
    private const string TestAdminUsername = "test-admin";
    private const string TestAdminPassword = "Test-Password-123!";

    public static async Task<HttpClient> CreateAuthenticatedClientAsync<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory) where TEntryPoint : class
    {
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
            var exists = await context.PlatformUsers.AnyAsync(u => u.Username == TestAdminUsername);
            if (!exists)
            {
                context.PlatformUsers.Add(new PlatformUser
                {
                    Username = TestAdminUsername,
                    PasswordHash = PasswordHasher.Hash(TestAdminPassword),
                    IsAdmin = true,
                    CreatedAt = DateTime.UtcNow
                });
                await context.SaveChangesAsync();
            }
        }

        var client = factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(TestAdminUsername, TestAdminPassword));
        if (loginResponse.StatusCode != System.Net.HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException(
                $"AuthenticatedTestClientHelper: test login failed with {loginResponse.StatusCode}. " +
                "This usually means the PlatformUser schema or login contract changed without updating this helper.");
        }

        return client;
    }
}
```

Generic on `TEntryPoint` (rather than hardcoding `Program`) so both `TestWebApplicationFactory : WebApplicationFactory<Program>` and `ScenarioTestWebApplicationFactory : WebApplicationFactory<Program>` (and any future factory) can call it without a cast.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~AuthenticatedTestClientHelperTests"`
Expected: PASS (2/2)

- [ ] **Step 5: Commit**

```bash
git add tests/LogsPlatform.Tests/Infrastructure/AuthenticatedTestClientHelper.cs tests/LogsPlatform.Tests/Infrastructure/AuthenticatedTestClientHelperTests.cs
git commit -m "test(m6): add AuthenticatedTestClientHelper for migrating gated-endpoint tests"
```

---

## Task 7: Migrate Web/ controller tests to `AuthenticatedTestClientHelper`

**Files (all 17, every one modified the same mechanical way):**
`tests/LogsPlatform.Tests/Web/ApiKeysControllerTests.cs`, `ApplicationsControllerTests.cs`, `AppUsersControllerTests.cs`, `CustomersControllerTests.cs`, `DeploymentsControllerTests.cs`, `EnvironmentsControllerTests.cs`, `EventsControllerTests.cs`, `ExceptionGroupsControllerTests.cs`, `FindingsControllerTests.cs`, `IngestionControllerTests.cs`, `LogSourcesControllerTests.cs`, `ModulesControllerTests.cs`, `OperationsControllerTests.cs`, `ProcessesControllerTests.cs`, `ScreenServicesControllerTests.cs`, `TimelineControllerTests.cs`, `VersionsControllerTests.cs`.

**The mechanical diff, applied identically to every file above:** every occurrence of `_factory.CreateClient()` (whether assigned to a local `client` variable directly in a `[Fact]` method, or inside a `private static`/`private` setup helper like `CreateAppWithApiKeyAsync`) becomes `await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory)`. Add `using LogsPlatform.Tests.Infrastructure;` to each file's usings if not already present (all 17 already reference `LogsPlatform.Tests.Web` for `TestWebApplicationFactory`, but not necessarily `LogsPlatform.Tests.Infrastructure`).

**Worked example — `ApplicationsControllerTests.cs`** (full before/after for one representative file; apply the identical pattern to the other 16):

Before (current, lines 1–21):
```csharp
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
```

After:
```csharp
using System.Net;
using System.Net.Http.Json;
using LogsPlatform.Tests.Infrastructure;
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
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
```

Every other `var client = _factory.CreateClient();` occurrence in `ApplicationsControllerTests.cs` (there are 3 total — one per `[Fact]`) gets the identical substitution.

**`IngestionControllerTests.cs` special note:** its `CreateAppWithApiKeyAsync(HttpClient client, string appName)` helper takes the client as a parameter and is called with `_factory.CreateClient()` inline at each call site (e.g. `await CreateAppWithApiKeyAsync(client, "IngestValidBatchTestApp")` where `client` was just assigned from `_factory.CreateClient()`). The fix is the same substitution at the `var client = _factory.CreateClient();` line in each `[Fact]` — `CreateAppWithApiKeyAsync` itself needs no change, since it only uses the client to call Admin endpoints (now working because the client is authenticated) and the actual ingestion calls in each test still send `X-Api-Key` explicitly via `BuildRequest`, which the `ApiKeyAuthenticationHandler` scheme evaluates independently of any cookie present on the same client — an authenticated cookie and an `X-Api-Key` header coexist without conflict, since `IngestionController` only ever evaluates the `ApiKey` scheme.

**`ApiKeysControllerTests.cs`, `AppUsersControllerTests.cs`, `CustomersControllerTests.cs`, `DeploymentsControllerTests.cs`, `EnvironmentsControllerTests.cs`, `EventsControllerTests.cs`, `ExceptionGroupsControllerTests.cs`, `FindingsControllerTests.cs`, `LogSourcesControllerTests.cs`, `ModulesControllerTests.cs`, `OperationsControllerTests.cs`, `ProcessesControllerTests.cs`, `ScreenServicesControllerTests.cs`, `TimelineControllerTests.cs`, `VersionsControllerTests.cs`:** each follows the exact same `IClassFixture<TestWebApplicationFactory>` + `_factory.CreateClient()`-per-`[Fact]` (or per-helper) shape as `ApplicationsControllerTests.cs`. Apply the identical substitution to every `_factory.CreateClient()` occurrence in each file.

- [ ] **Step 1: Apply the substitution to all 17 files listed above**

- [ ] **Step 2: Run the Web/ test suite**

Run: `dotnet test --filter "FullyQualifiedName~LogsPlatform.Tests.Web"`
Expected: PASS — all tests in `tests/LogsPlatform.Tests/Web/` (this now includes `AuthControllerTests`, `RequireAdminPolicyTests`, `StartupSeedingTests` from earlier tasks, plus the 17 migrated files) pass with 0 failures. If any file still fails with 401/403, grep that file for a missed `_factory.CreateClient()` occurrence (some files have 2–4 `[Fact]` methods, each with its own client) before concluding anything else is wrong.

- [ ] **Step 3: Commit**

```bash
git add tests/LogsPlatform.Tests/Web/ApiKeysControllerTests.cs tests/LogsPlatform.Tests/Web/ApplicationsControllerTests.cs tests/LogsPlatform.Tests/Web/AppUsersControllerTests.cs tests/LogsPlatform.Tests/Web/CustomersControllerTests.cs tests/LogsPlatform.Tests/Web/DeploymentsControllerTests.cs tests/LogsPlatform.Tests/Web/EnvironmentsControllerTests.cs tests/LogsPlatform.Tests/Web/EventsControllerTests.cs tests/LogsPlatform.Tests/Web/ExceptionGroupsControllerTests.cs tests/LogsPlatform.Tests/Web/FindingsControllerTests.cs tests/LogsPlatform.Tests/Web/IngestionControllerTests.cs tests/LogsPlatform.Tests/Web/LogSourcesControllerTests.cs tests/LogsPlatform.Tests/Web/ModulesControllerTests.cs tests/LogsPlatform.Tests/Web/OperationsControllerTests.cs tests/LogsPlatform.Tests/Web/ProcessesControllerTests.cs tests/LogsPlatform.Tests/Web/ScreenServicesControllerTests.cs tests/LogsPlatform.Tests/Web/TimelineControllerTests.cs tests/LogsPlatform.Tests/Web/VersionsControllerTests.cs
git commit -m "test(m6): migrate Web/ controller tests to AuthenticatedTestClientHelper"
```

---

## Task 8: Migrate Scenario/ tests to `AuthenticatedTestClientHelper`

**Files (5, all in `tests/LogsPlatform.Tests/Scenario/`):**
`DomainFixtureTests.cs`, `FalsePositiveTests.cs`, `ScenarioAcceptanceTests.cs`, `DeploymentAnomalyInjectorTests.cs`, `ScenarioTestWebApplicationFactoryTests.cs`.

`ScenarioTestWebApplicationFactory.cs` itself (the factory) is **not modified** — per this plan's Global Constraints, `DomainFixture.cs` is also **not modified**. Every one of these 5 test files currently does `using var factory = new ScenarioTestWebApplicationFactory(); var client = factory.CreateClient();` (or `TestWebApplicationFactory()` for `ScenarioTestWebApplicationFactoryTests.cs`, which tests the factory's own shape) and then either calls `DomainFixture.BuildRetailPulseAsync(client)` / `BuildFieldOpsAsync(client)` / `SeedCustomersAsync(client, ...)` directly, or hits Admin endpoints itself before doing so.

**The mechanical diff:** every `var client = factory.CreateClient();` (or `new ...Factory().CreateClient()` inline) becomes `var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(factory);`, keeping every downstream `DomainFixture.XxxAsync(client, ...)` call unchanged — `DomainFixture` doesn't know or care that the client is now authenticated, since it just uses it as a plain `HttpClient`. Add `using LogsPlatform.Tests.Infrastructure;` to each file if not already present.

- [ ] **Step 1: Apply the substitution to `DomainFixtureTests.cs`, `FalsePositiveTests.cs`, `ScenarioAcceptanceTests.cs`, `DeploymentAnomalyInjectorTests.cs`, `ScenarioTestWebApplicationFactoryTests.cs`**

For each `using var factory = new ScenarioTestWebApplicationFactory();` (or `TestWebApplicationFactory()`) followed later by `var client = factory.CreateClient();`, change only the client-creation line to `var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(factory);`. Since M5 established the "fresh factory per test method" convention in these exact files (to avoid the `IClassFixture`-shared-DB 409 Conflict bug found during M5), each test method already constructs its own factory — the helper's idempotent seeding (Task 6) handles this safely regardless.

- [ ] **Step 2: Run the Scenario/ test suite**

Run: `dotnet test --filter "FullyQualifiedName~LogsPlatform.Tests.Scenario"`
Expected: PASS — all Scenario tests, including the M5 go/no-go `ScenarioAcceptanceTests`, pass with 0 failures. This is the highest-stakes verification in this plan: `ScenarioAcceptanceTests` is the project's own acceptance gate (M5), and this task must not silently regress it. If it fails, read the actual failure output before assuming a client-migration mistake — cross-check against `referenceTime` threading (a separate, unrelated M5 concern) before concluding this task's change is at fault.

- [ ] **Step 3: Commit**

```bash
git add tests/LogsPlatform.Tests/Scenario/DomainFixtureTests.cs tests/LogsPlatform.Tests/Scenario/FalsePositiveTests.cs tests/LogsPlatform.Tests/Scenario/ScenarioAcceptanceTests.cs tests/LogsPlatform.Tests/Scenario/DeploymentAnomalyInjectorTests.cs tests/LogsPlatform.Tests/Scenario/ScenarioTestWebApplicationFactoryTests.cs
git commit -m "test(m6): migrate Scenario/ tests to AuthenticatedTestClientHelper"
```

---

## Task 9: Migrate Client/ tests to `AuthenticatedTestClientHelper`

**Files (2, both in `tests/LogsPlatform.Tests/Client/`):**
`LogsPlatformClientTests.cs`, `LogsPlatformSinkTests.cs`.

Both currently have a private `CreateAppWithApiKeyAsync(string appName)` helper that internally does `var setupClient = _factory.CreateClient();` then calls 3 Admin endpoints to build an Application/Environment/ApiKey, before the actual test uses a **separate**, unauthenticated `_factory.CreateClient()` as the `httpClient:` parameter passed into `LogsPlatformClient`/the Serilog sink (that second client only ever sends `X-Api-Key`-authenticated ingestion requests, never an Admin call — it does not need migration).

- [ ] **Step 1: Apply the substitution to the setup helper only, in both files**

In `LogsPlatformClientTests.cs`, change:

```csharp
    private async Task<(int ApplicationId, string ApiKey)> CreateAppWithApiKeyAsync(string appName)
    {
        var setupClient = _factory.CreateClient();
```

to:

```csharp
    private async Task<(int ApplicationId, string ApiKey)> CreateAppWithApiKeyAsync(string appName)
    {
        var setupClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
```

Apply the identical change to `LogsPlatformSinkTests.cs`'s `CreateAppWithApiKeyAsync`. Add `using LogsPlatform.Tests.Infrastructure;` to both files. Do **not** change the separate `_factory.CreateClient()` calls used later as the `httpClient:` argument to `new LogsPlatformClient(...)` / `.WriteTo.LogsPlatform(...)` — those remain plain, unauthenticated clients, correctly, since they only ever carry `X-Api-Key`.

- [ ] **Step 2: Run the Client/ test suite**

Run: `dotnet test --filter "FullyQualifiedName~LogsPlatform.Tests.Client"`
Expected: PASS — 0 failures.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test`
Expected: PASS, 0 failures, across the entire solution — this is the point where every test file this milestone's auth gating touched has been migrated. Record the total test count for the plan's final verification.

- [ ] **Step 4: Commit**

```bash
git add tests/LogsPlatform.Tests/Client/LogsPlatformClientTests.cs tests/LogsPlatform.Tests/Client/LogsPlatformSinkTests.cs
git commit -m "test(m6): migrate Client/ tests to AuthenticatedTestClientHelper"
```

---

## Task 10: `PlatformUsersSection` Admin UI + NavMenu updates

**Files:**
- Create: `src/LogsPlatform.Web/Components/Shared/PlatformUsersSection.razor`
- Create: `src/LogsPlatform.Web/Components/Pages/PlatformUsersAdmin.razor`
- Modify: `src/LogsPlatform.Web/Components/Layout/NavMenu.razor`

**Interfaces:**
- Consumes: `IPlatformUserRepository` (Task 1)

- [ ] **Step 1: Create `PlatformUsersSection.razor`**

```razor
@* src/LogsPlatform.Web/Components/Shared/PlatformUsersSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Infrastructure
@using LogsPlatform.Web
@using Microsoft.EntityFrameworkCore
@inject IPlatformUserRepository PlatformUserRepository

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

    protected override async Task OnInitializedAsync()
    {
        _users = (await PlatformUserRepository.GetAllAsync()).ToList();
    }

    private async Task CreateUserAsync()
    {
        _createError = null;
        try
        {
            await PlatformUserRepository.AddAsync(new PlatformUser
            {
                Username = _newUser.Username,
                PasswordHash = PasswordHasher.Hash(_newUser.Password),
                IsAdmin = _newUser.IsAdmin,
                CreatedAt = DateTime.UtcNow
            });

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

- [ ] **Step 2: Create the hosting page**

```razor
@* src/LogsPlatform.Web/Components/Pages/PlatformUsersAdmin.razor *@
@page "/admin/platform-users"
@attribute [Microsoft.AspNetCore.Authorization.Authorize(Policy = "RequireAdmin")]
@using LogsPlatform.Web.Components.Shared
@rendermode InteractiveServer

<h1>משתמשי מערכת</h1>
<p class="text-muted">משתמשים שיכולים להתחבר ל-LogsPlatform עצמה — נבדל מ"משתמשים" בתוך כל אפליקציה, שהם משתמשי הקצה של האפליקציה המחוברת.</p>

<PlatformUsersSection />
```

- [ ] **Step 3: Add a nav link and a logout form to `NavMenu.razor`**

Replace the full contents of `src/LogsPlatform.Web/Components/Layout/NavMenu.razor`:

```razor
@* src/LogsPlatform.Web/Components/Layout/NavMenu.razor *@
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
            <AuthorizeView Policy="RequireAdmin">
                <Authorized>
                    <li class="nav-item">
                        <NavLink class="nav-link" href="/admin/applications" Match="NavLinkMatch.Prefix">
                            ניהול
                        </NavLink>
                    </li>
                    <li class="nav-item">
                        <NavLink class="nav-link" href="/admin/platform-users" Match="NavLinkMatch.Prefix">
                            משתמשי מערכת
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
```

`AuthorizeView` (no explicit `Policy`) with just `<Authorized>` renders for any authenticated user; the admin-only links are wrapped in a separate `AuthorizeView Policy="RequireAdmin"`. The logout `<form>` is plain HTML (not `EditForm`) — a real browser POST to `AuthController.Logout`, which is `[IgnoreAntiforgeryToken]` (Task 2) precisely so this plain form works without wiring Blazor's antiforgery token machinery into a static form element.

- [ ] **Step 4: Manual verification**

Run: `dotnet run --project src/LogsPlatform.Web`
Log in as the seeded `admin` account (password from the console output). Confirm: "ניהול" and "משתמשי מערכת" links are visible; navigate to `/admin/platform-users`, create a new non-admin user, log out, log back in as that new user, confirm the admin-only nav links are now hidden and `/admin/platform-users` redirects to `/login` when visited directly.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS, same count as Task 9's final run (no test coverage added for pure-UI Razor components in this codebase's established convention — Admin sections are exercised through their underlying repository/controller tests, e.g. `PlatformUserRepositoryTests.cs` from Task 1).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/PlatformUsersSection.razor src/LogsPlatform.Web/Components/Pages/PlatformUsersAdmin.razor src/LogsPlatform.Web/Components/Layout/NavMenu.razor
git commit -m "feat(m6): add PlatformUsersSection admin UI and NavMenu login state"
```

---

## Task 11: Promote-to-Conclusion — authenticated `ApprovedBy`

**Files:**
- Modify: `src/LogsPlatform.Web/Controllers/FindingsController.cs`
- Modify: `src/LogsPlatform.Web/Contracts/QueryContracts.cs`
- Modify: `src/LogsPlatform.Web/Components/Pages/FindingDetail.razor`
- Modify: `tests/LogsPlatform.Tests/Web/FindingsControllerTests.cs`

**Interfaces:**
- Consumes: `IFindingRepository.PromoteToConclusionAsync(long findingId, long statementId, string approvedBy)` — unchanged signature (`src/LogsPlatform.Domain/Repositories/IFindingRepository.cs:15`); only the caller-supplied value for `approvedBy` changes.

- [ ] **Step 1: Remove `PromoteStatementRequest` from `QueryContracts.cs`**

In `src/LogsPlatform.Web/Contracts/QueryContracts.cs`, delete line 30:

```csharp
public record PromoteStatementRequest(string ApprovedBy);
```

- [ ] **Step 2: Update `FindingsController.Promote` to read the claim instead of a request body**

In `src/LogsPlatform.Web/Controllers/FindingsController.cs`, replace lines 85–96:

```csharp
    [HttpPost("{id:long}/statements/{statementId:long}/promote")]
    public async Task<IActionResult> Promote(long id, long statementId, [FromBody] PromoteStatementRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ApprovedBy))
        {
            return ValidationProblem("approvedBy is required.");
        }

        var statement = await _findings.PromoteToConclusionAsync(id, statementId, request.ApprovedBy);
        if (statement is null) return NotFound();
        return NoContent();
    }
```

with:

```csharp
    [HttpPost("{id:long}/statements/{statementId:long}/promote")]
    public async Task<IActionResult> Promote(long id, long statementId)
    {
        var approvedBy = User.FindFirstValue(ClaimTypes.Name)!;

        var statement = await _findings.PromoteToConclusionAsync(id, statementId, approvedBy);
        if (statement is null) return NotFound();
        return NoContent();
    }
```

Add `using System.Security.Claims;` to the top of `FindingsController.cs`. The `!` on `User.FindFirstValue(ClaimTypes.Name)!` is safe here specifically because `FindingsController` has no `[AllowAnonymous]` and is reached only after the fallback policy (Task 3) already required an authenticated user with that exact claim (set at login in `AuthController.Login`, Task 2) — an unauthenticated request never reaches this action.

- [ ] **Step 3: Update `FindingDetail.razor`'s promote UI to drop the free-text field**

In `src/LogsPlatform.Web/Components/Pages/FindingDetail.razor`, replace the Hypothesis-statement promote block (lines 34–48 area, matched around `else if (statement.Kind == FindingStatementKind.Hypothesis)`):

Before:
```razor
                    else if (statement.Kind == FindingStatementKind.Hypothesis)
                    {
                        @if (_promotingStatementId == statement.Id)
                        {
                            <div class="d-flex gap-2 align-items-center mt-2">
                                <input class="form-control form-control-sm" placeholder="הערת אישור" @bind="_approvalNote" @bind:event="oninput" style="max-width:300px" />
                                <button class="btn btn-sm btn-success" disabled="@string.IsNullOrWhiteSpace(_approvalNote)" @onclick="PromoteAsync">אשר/י</button>
                                <button class="btn btn-sm btn-outline-secondary" @onclick="() => _promotingStatementId = null">בטל/י</button>
                            </div>
                        }
```

After:
```razor
                    else if (statement.Kind == FindingStatementKind.Hypothesis)
                    {
                        @if (_promotingStatementId == statement.Id)
                        {
                            <div class="d-flex gap-2 align-items-center mt-2">
                                <span class="text-muted small">אישור בשם @_currentUsername</span>
                                <button class="btn btn-sm btn-success" @onclick="PromoteAsync">אשר/י</button>
                                <button class="btn btn-sm btn-outline-secondary" @onclick="() => _promotingStatementId = null">בטל/י</button>
                            </div>
                        }
```

(Leave the rest of that block — the `else` branch showing the "promote" trigger button — unchanged; only the confirm row above changes.)

In the same file's `@code` block, replace:

```csharp
    private async Task PromoteAsync()
    {
        if (_promotingStatementId is null || string.IsNullOrWhiteSpace(_approvalNote)) return;
        await FindingRepository.PromoteToConclusionAsync(Id, _promotingStatementId.Value, _approvalNote);
        _promotingStatementId = null;
        _approvalNote = string.Empty;
        await LoadAsync();
    }
```

with:

```csharp
    private async Task PromoteAsync()
    {
        if (_promotingStatementId is null) return;
        await FindingRepository.PromoteToConclusionAsync(Id, _promotingStatementId.Value, _currentUsername);
        _promotingStatementId = null;
        await LoadAsync();
    }
```

And add near the top of the `@code` block (alongside the other private fields) a `_currentUsername` field populated from the cascading auth state, plus the injected provider and using:

```razor
@using System.Security.Claims
@using Microsoft.AspNetCore.Components.Authorization
```

```csharp
    [CascadingParameter] private Task<AuthenticationState>? AuthenticationStateTask { get; set; }
    private string _currentUsername = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        if (AuthenticationStateTask is not null)
        {
            var authState = await AuthenticationStateTask;
            _currentUsername = authState.User.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
        }
        await LoadAsync();
    }
```

(This assumes `FindingDetail.razor`'s existing `OnInitializedAsync` currently just calls `await LoadAsync();` — merge the two rather than adding a duplicate override; check the file's current `OnInitializedAsync` body before applying and fold this in without creating a second method with the same signature.)

Also remove the now-unused `_approvalNote` field declaration from the `@code` block if it exists elsewhere in the file (it was only ever used by the two spots just changed).

- [ ] **Step 4: Update `FindingsControllerTests.cs`**

Find every call in `tests/LogsPlatform.Tests/Web/FindingsControllerTests.cs` that posts to the promote endpoint with a `PromoteStatementRequest` body (e.g. `await client.PostAsJsonAsync($"/api/v1/findings/{id}/statements/{statementId}/promote", new PromoteStatementRequest("some-approver"))`) and change it to post with no body and assert against the authenticated test user's username instead of a hardcoded string:

```csharp
var response = await client.PostAsync($"/api/v1/findings/{id}/statements/{statementId}/promote", null);
```

Then wherever the test previously asserted `Assert.Equal("some-approver", statement.ApprovedBy)`, change the expected value to `"test-admin"` (the `AuthenticatedTestClientHelper`'s fixed username constant, Task 6) since that's the identity the migrated client now authenticates as.

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~FindingsControllerTests"`
Expected: PASS, 0 failures.

Run: `dotnet build`
Expected: 0 errors (confirms `FindingDetail.razor`'s edits compile — Razor compilation errors only surface at build time, not from the controller test filter above).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/FindingsController.cs src/LogsPlatform.Web/Contracts/QueryContracts.cs src/LogsPlatform.Web/Components/Pages/FindingDetail.razor tests/LogsPlatform.Tests/Web/FindingsControllerTests.cs
git commit -m "feat(m6): Promote-to-Conclusion approvedBy comes from the authenticated user"
```

---

## Task 12: Redaction hook in `LogsPlatformClient`

**Files:**
- Modify: `src/LogsPlatform.Client/LogsPlatformClient.cs`
- Test: `tests/LogsPlatform.Tests/Client/LogsPlatformClientRedactionTests.cs`

**Interfaces:**
- Produces: new optional constructor parameter `Func<string, string>? redactMessage = null` on `LogsPlatformClient`. When supplied, applied to `EventPayload.Message` and every `string`-typed value in `EventPayload.Metadata` before the event enters the buffer.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LogsPlatform.Tests/Client/LogsPlatformClientRedactionTests.cs
using System.Net;
using LogsPlatform.Client;
using Xunit;

namespace LogsPlatform.Tests.Client;

public class LogsPlatformClientRedactionTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> CapturedRequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                CapturedRequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private static EventPayload BuildEvent(string message, Dictionary<string, object>? metadata = null) => new(
        EventKey: null, Timestamp: DateTime.UtcNow, Severity: "Error", Environment: "Production",
        Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null,
        DurationMs: null, CustomerId: null, UserId: null, Message: message, MessageTemplate: null,
        Exception: null, Metadata: metadata);

    [Fact]
    public async Task SendEventAsync_WithRedactionHook_TransformsMessageBeforeSending()
    {
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var client = new LogsPlatformClient(
            baseUrl: "http://localhost/", apiKey: "irrelevant", httpClient: httpClient,
            batchSize: 1, period: TimeSpan.FromMinutes(10),
            redactMessage: msg => msg.Replace("secret-value", "[REDACTED]"));

        await client.SendEventAsync(BuildEvent("credit card is secret-value here"));
        await Task.Delay(100);

        Assert.Single(handler.CapturedRequestBodies);
        Assert.Contains("[REDACTED]", handler.CapturedRequestBodies[0]);
        Assert.DoesNotContain("secret-value", handler.CapturedRequestBodies[0]);
    }

    [Fact]
    public async Task SendEventAsync_WithRedactionHook_TransformsStringMetadataValuesOnly()
    {
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var client = new LogsPlatformClient(
            baseUrl: "http://localhost/", apiKey: "irrelevant", httpClient: httpClient,
            batchSize: 1, period: TimeSpan.FromMinutes(10),
            redactMessage: msg => msg.Replace("secret-value", "[REDACTED]"));

        await client.SendEventAsync(BuildEvent("no secrets here", new Dictionary<string, object>
        {
            ["note"] = "contains secret-value inline",
            ["retryCount"] = 3
        }));
        await Task.Delay(100);

        Assert.Single(handler.CapturedRequestBodies);
        Assert.Contains("[REDACTED]", handler.CapturedRequestBodies[0]);
        Assert.Contains("\"retryCount\":3", handler.CapturedRequestBodies[0]);
    }

    [Fact]
    public async Task SendEventAsync_NoRedactionHook_MessagePassesThroughUnchanged()
    {
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var client = new LogsPlatformClient(
            baseUrl: "http://localhost/", apiKey: "irrelevant", httpClient: httpClient,
            batchSize: 1, period: TimeSpan.FromMinutes(10));

        await client.SendEventAsync(BuildEvent("secret-value stays as-is"));
        await Task.Delay(100);

        Assert.Single(handler.CapturedRequestBodies);
        Assert.Contains("secret-value stays as-is", handler.CapturedRequestBodies[0]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~LogsPlatformClientRedactionTests"`
Expected: FAIL (build error — no `redactMessage` constructor parameter exists)

- [ ] **Step 3: Add the redaction hook to `LogsPlatformClient`**

In `src/LogsPlatform.Client/LogsPlatformClient.cs`, add a new private field after line 19 (`private bool _disposed;`):

```csharp
    private readonly Func<string, string>? _redactMessage;
```

Change the constructor signature (lines 21–28) from:

```csharp
    public LogsPlatformClient(
        string baseUrl,
        string apiKey,
        HttpClient? httpClient = null,
        int batchSize = 100,
        TimeSpan? period = null,
        int queueLimit = 10_000)
    {
```

to:

```csharp
    public LogsPlatformClient(
        string baseUrl,
        string apiKey,
        HttpClient? httpClient = null,
        int batchSize = 100,
        TimeSpan? period = null,
        int queueLimit = 10_000,
        Func<string, string>? redactMessage = null)
    {
        _redactMessage = redactMessage;
```

(Insert `_redactMessage = redactMessage;` as the first line inside the constructor body, before the existing `ArgumentException.ThrowIfNullOrEmpty(baseUrl);`.)

Change `SendEventAsync` (lines 51–74) to apply redaction before buffering:

```csharp
    public Task SendEventAsync(EventPayload evt)
    {
        var toBuffer = _redactMessage is null ? evt : RedactEvent(evt);

        List<EventPayload>? toFlush = null;
        lock (_bufferLock)
        {
            _buffer.Add(toBuffer);
            while (_buffer.Count > _queueLimit)
            {
                _buffer.RemoveAt(0);
            }
            if (_buffer.Count >= _batchSize)
            {
                toFlush = new List<EventPayload>(_buffer);
                _buffer.Clear();
            }
        }

        if (toFlush is not null)
        {
            TrackPendingFlush(() => FlushBatchAsync(toFlush));
        }

        return Task.CompletedTask;
    }

    private EventPayload RedactEvent(EventPayload evt)
    {
        var redactedMessage = _redactMessage!(evt.Message);
        var redactedMetadata = evt.Metadata is null
            ? null
            : evt.Metadata.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value is string stringValue ? (object)_redactMessage!(stringValue) : kvp.Value);

        return evt with { Message = redactedMessage, Metadata = redactedMetadata };
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~LogsPlatformClientRedactionTests"`
Expected: PASS (3/3)

- [ ] **Step 5: Run the full Client/ suite to confirm no regression**

Run: `dotnet test --filter "FullyQualifiedName~LogsPlatform.Tests.Client"`
Expected: PASS, 0 failures (the new optional parameter defaults to `null`, so every existing call site is unaffected).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Client/LogsPlatformClient.cs tests/LogsPlatform.Tests/Client/LogsPlatformClientRedactionTests.cs
git commit -m "feat(m6): add optional redaction hook to LogsPlatformClient"
```

---

## Task 13: Pre-commit secrets scan

**Files:**
- Create: `.githooks/pre-commit`
- Modify: `README.md`

**Interfaces:** none (shell script, no code dependency on any other task).

- [ ] **Step 1: Create the hook script**

```bash
# .githooks/pre-commit
#!/bin/sh
# Blocks a commit if any staged file appears to contain a real secret.
# Not a substitute for a proper secrets scanner — a fast, local, zero-dependency
# tripwire per this project's V1 threat model (10-Security-Design.md §6).

staged_files=$(git diff --cached --name-only --diff-filter=ACM)
if [ -z "$staged_files" ]; then
    exit 0
fi

pattern='(Password[[:space:]]*=[^;]{4,}|pwd[[:space:]]*=[^;]{4,}|AKIA[0-9A-Z]{16}|-----BEGIN[A-Z ]*PRIVATE KEY-----|Server=.*;.*Password=)'
found=0

for file in $staged_files; do
    case "$file" in
        appsettings.Development.json|*/appsettings.Development.json)
            continue
            ;;
    esac

    if git show ":$file" 2>/dev/null | grep -Eiq "$pattern"; then
        echo "pre-commit: possible secret detected in '$file' — commit blocked."
        found=1
    fi
done

if [ "$found" -eq 1 ]; then
    echo ""
    echo "If this is a false positive, review the file and either remove the match"
    echo "or (rarely) bypass with 'git commit --no-verify' after confirming with the team."
    exit 1
fi

exit 0
```

- [ ] **Step 2: Make it executable and verify it blocks a real match**

Run: `chmod +x .githooks/pre-commit`
Run: `git config core.hooksPath .githooks`

Verify manually:
```bash
echo 'Server=x;Password=hunter2;' > /tmp/secret-test.txt
cp /tmp/secret-test.txt appsettings.SecretTest.json
git add appsettings.SecretTest.json
git commit -m "should be blocked"
```
Expected: commit is refused with "possible secret detected in 'appsettings.SecretTest.json' — commit blocked."

Clean up:
```bash
git reset appsettings.SecretTest.json
rm appsettings.SecretTest.json /tmp/secret-test.txt
```

- [ ] **Step 3: Add the one-time setup line to `README.md`**

Read `README.md` first to find its current setup/getting-started section, then add one line there (exact wording matched to the surrounding style):

```markdown
Run `git config core.hooksPath .githooks` once after cloning to enable the pre-commit secrets scan (git does not install hooks from a clone automatically).
```

- [ ] **Step 4: Run the full suite one final time**

Run: `dotnet test`
Expected: PASS, 0 failures — same total as Task 9/10's runs (this task adds no test files, only a git hook and a README line).

- [ ] **Step 5: Commit**

```bash
git add .githooks/pre-commit README.md
git commit -m "feat(m6): add pre-commit secrets scan and hooksPath setup instructions"
```

---

## Final Verification

After all 13 tasks:

- [ ] Run `dotnet test` — full suite passes, 0 failures.
- [ ] Manually verify (per Task 4 Step 5 and Task 10 Step 4): fresh app start seeds an admin account and prints its password once; logging in works; every page redirects to `/login` when unauthenticated; Admin pages redirect for a non-admin authenticated user; Promote-to-Conclusion no longer shows a free-text approver field; `/admin/platform-users` lets an admin create/deactivate PlatformUsers.
- [ ] Confirm every item in the design doc's §5 testing checklist has a corresponding test: missing/wrong/revoked API key → 401 (`IngestionControllerTests`, pre-existing, unaffected) ✓; `IsAdmin=false` hitting an Admin endpoint → 403 (`RequireAdminPolicyTests`) ✓; `PasswordHash` never the plaintext password (`PlatformUserRepositoryTests`) ✓; redaction hook transforms before sending (`LogsPlatformClientRedactionTests`) ✓.
- [ ] Then invoke `superpowers:finishing-a-development-branch`.

---

**Plan self-review notes (fixed inline before saving):**
- Spec coverage checked against design doc §3–§6: `PlatformUser` ✓ (Task 1), `PasswordHasher` ✓ (Task 1), cookie auth as default scheme ✓ (Task 3), global fallback + `RequireAdmin` ✓ (Task 3), `AuthController` ✓ (Task 2), `/login` page ✓ (Task 4), Blazor page gating ✓ (Task 4), startup seeding ✓ (Task 5), `PlatformUsersSection.razor` ✓ (Task 10), Promote-to-Conclusion rework ✓ (Task 11), redaction hook ✓ (Task 12), secrets scan + README ✓ (Task 13), `AuthenticatedTestClientHelper` + full test migration ✓ (Tasks 6–9). Out-of-scope items (§6) — per-Application permissions, general audit logging, automatic PII detection, password reset, TLS, retention — confirmed absent from every task above.
- Placeholder scan: every test-migration task names every affected file explicitly (17 in Task 7, 5 in Task 8, 2 in Task 9) rather than "and other tests." No "TBD"/"handle appropriately" phrasing anywhere.
- Mixed positional/named C# argument check: none found in any code block above.
- Blazor auth-state plumbing specifically re-verified: `CascadingAuthenticationState` wraps `Router` in `Routes.razor` (Task 4), `AuthorizeRouteView` (not plain `RouteView`) is used, every one of the 11 pre-existing pages plus the 2 new ones gets an explicit `@attribute` (Blazor's router does not honor MVC's fallback policy — verified against this codebase's actual `App.razor`/`Routes.razor`, which currently have zero auth-state wiring).
