# M4b: Findings API + Lifecycle Actions + "What's Unusual" UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose M4a's `Finding`/`FindingStatement`/`Evidence` data through a Findings API and lifecycle actions (Acknowledge/Resolve/Dismiss/Promote-to-Conclusion), wire the previously-deferred `DownstreamFailureCorrelator` inline into the two detectors that have real trigger-event context, and build the "What's Unusual" home page + Finding detail page.

**Architecture:** `IFindingRepository` gains 3 methods (query, status update, promote). A new `FindingsController` exposes them over HTTP for external consumers (Swagger/scripts) — but per this project's established convention (confirmed in M3: Blazor pages call repositories directly via DI, never through the Web API's own controllers), the UI pages inject `IFindingRepository` directly rather than calling `FindingsController` over HTTP. `RateAnomalyDetector` and `NewExceptionDetector` each take a constructor-injected `DownstreamFailureCorrelator` and call it inline right after writing a Finding, while the triggering event's `CorrelationId`/`OperationId`/`Timestamp` are still in scope.

**Tech Stack:** .NET 10, EF Core 10, SQL Server (real DB in tests, no mocking), ASP.NET Core Controllers, Blazor Server (Interactive Server render mode), xUnit.

## Global Constraints

- **Home page:** "What's Unusual" (the Findings list) replaces `Home.razor`'s current nav-hub content at `/` — this is the locked decision from the design doc, not a new task-level choice.
- **Approval identity:** free-text "approved by" string, no real auth/user-identity system — `FindingStatement.ApprovedBy`/`ApprovedAt` already exist on the entity from M4a.
- **Status transitions:** free — no state-machine validation on `PATCH .../status`.
- **Lifecycle auth:** unauthenticated (no `[Authorize]`), consistent with every other endpoint/page in this project.
- **`DownstreamFailureCorrelator` wiring:** called inline by the detector that found the anomaly (`NewExceptionDetector` always; `RateAnomalyDetector` only for `ErrorSpike` findings scoped to an `Operation`, via a representative Event from the spike hour) — not driven uniformly from `AnalysisEngineTickRunner`.
- **Hebrew UI:** all new UI text in Hebrew, matching this project's fully-Hebrew convention (entity/domain nouns translated too, per the session's earlier "Everything, including Admin's English titles" decision) — only "Stack Trace" and real domain data (exception type names) stay in English, per established precedent in `ExceptionDetail.razor`.
- **Test convention:** real SQL Server DB in every test, no mocking, no InMemory provider — `[Collection("Database")]`, verification reads via a fresh `DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options` context, never a second `TestDatabase.CreateContext()` (which wipes the DB).
- **No new `Program.cs` DI registrations are needed** for the repository/controller tasks — `IFindingRepository`, `IApplicationRepository`, `IOperationRepository`, `IAppEnvironmentRepository` are all already registered from M1–M4a. Only the detector-wiring tasks touch `Program.cs`-adjacent constructor wiring indirectly (via DI auto-resolution — no explicit `Program.cs` edit needed there either, since `DownstreamFailureCorrelator` is already registered).

---

### Task 1: `IFindingRepository` additions — `QueryAsync`, `UpdateStatusAsync`, `PromoteToConclusionAsync`

**Suggested model tier:** standard (real query/mutation logic, not pure scaffolding).

**Files:**
- Modify: `src/LogsPlatform.Domain/Repositories/IFindingRepository.cs`
- Modify: `src/LogsPlatform.Infrastructure/Repositories/FindingRepository.cs`
- Test: `tests/LogsPlatform.Tests/Infrastructure/FindingRepositoryTests.cs` (append)

**Interfaces:**
- Consumes: `Finding`, `FindingStatement`, `FindingStatus`, `FindingSeverity`, `FindingType`, `AnalysisScopeType`, `FindingStatementKind` (all from M4a's `Finding.cs`/`FindingStatement.cs`).
- Produces (used by Tasks 2, 3, 6, 7): `FindingQueryParameters(int ApplicationId, int EnvironmentId, FindingStatus? Status, FindingSeverity? Severity, FindingType? Type, DateTime? From, DateTime? To)`; `IFindingRepository.QueryAsync(FindingQueryParameters)` returning `IReadOnlyList<Finding>` ordered by `Severity` desc then `DetectedAt` desc; `UpdateStatusAsync(long findingId, FindingStatus status)` returning `Finding?` (null if not found); `PromoteToConclusionAsync(long findingId, long statementId, string approvedBy)` returning `FindingStatement?` (null if not found, or if the statement isn't currently `Hypothesis`).

- [ ] **Step 1: Write the failing tests**

Append to `tests/LogsPlatform.Tests/Infrastructure/FindingRepositoryTests.cs` (inside the existing `FindingRepositoryTests` class, after `AddStatementAsync_PersistsWithCorrectKind`):

```csharp
    [Fact]
    public async Task QueryAsync_FiltersByStatusAndOrdersBySeverityThenDetectedAt()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoQueryTestApp");
        var repository = new FindingRepository(context);

        var now = DateTime.UtcNow;
        await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "low, older",
            DetectedAt = now.AddMinutes(-10), Severity = FindingSeverity.Low, ConfidenceLevel = ConfidenceLevel.Medium, Status = FindingStatus.New
        });
        var high = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 2, Title = "high, newer",
            DetectedAt = now, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });
        await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 3, Title = "high, but resolved",
            DetectedAt = now.AddMinutes(-5), Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.Resolved
        });

        var results = await repository.QueryAsync(new FindingQueryParameters(appId, envId, FindingStatus.New, null, null, null, null));

        Assert.Equal(2, results.Count);
        Assert.Equal(high.Id, results[0].Id);
    }

    [Fact]
    public async Task UpdateStatusAsync_ExistingFinding_UpdatesAndReturnsIt()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoUpdateStatusTestApp");
        var repository = new FindingRepository(context);
        var finding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });

        var updated = await repository.UpdateStatusAsync(finding.Id, FindingStatus.Acknowledged);

        Assert.NotNull(updated);
        Assert.Equal(FindingStatus.Acknowledged, updated!.Status);

        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var reloaded = await verifyContext.Findings.FindAsync(finding.Id);
        Assert.Equal(FindingStatus.Acknowledged, reloaded!.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_NoSuchFinding_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var repository = new FindingRepository(context);

        var updated = await repository.UpdateStatusAsync(999999, FindingStatus.Acknowledged);

        Assert.Null(updated);
    }

    [Fact]
    public async Task PromoteToConclusionAsync_HypothesisStatement_PromotesWithApprovalMetadata()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoPromoteTestApp");
        var repository = new FindingRepository(context);
        var finding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });
        await repository.AddStatementAsync(finding.Id, DetectorStatementKind.Hypothesis, "Maybe a deployment caused this.");
        var reloaded = await repository.GetByIdAsync(finding.Id);
        var statementId = reloaded!.Statements[0].Id;

        var promoted = await repository.PromoteToConclusionAsync(finding.Id, statementId, "Dana");

        Assert.NotNull(promoted);
        Assert.Equal(FindingStatementKind.Conclusion, promoted!.Kind);
        Assert.Equal("Dana", promoted.ApprovedBy);
        Assert.NotNull(promoted.ApprovedAt);
    }

    [Fact]
    public async Task PromoteToConclusionAsync_StatementNotHypothesis_ReturnsNull()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "FindingRepoPromoteFactTestApp");
        var repository = new FindingRepository(context);
        var finding = await repository.AddAsync(new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = DateTime.UtcNow, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        });
        await repository.AddStatementAsync(finding.Id, DetectorStatementKind.Fact, "A measured fact.");
        var reloaded = await repository.GetByIdAsync(finding.Id);
        var statementId = reloaded!.Statements[0].Id;

        var promoted = await repository.PromoteToConclusionAsync(finding.Id, statementId, "Dana");

        Assert.Null(promoted);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter FindingRepositoryTests`
Expected: FAIL — compile error, `FindingQueryParameters`/`QueryAsync`/`UpdateStatusAsync`/`PromoteToConclusionAsync` don't exist.

- [ ] **Step 3: Implement the interface additions**

In `src/LogsPlatform.Domain/Repositories/IFindingRepository.cs`, add after the existing `GetDetectedSinceAsync` line (inside the interface body) and add the new record after `FindingWithDetails`:

```csharp
    Task<IReadOnlyList<Finding>> QueryAsync(FindingQueryParameters parameters);
    Task<Finding?> UpdateStatusAsync(long findingId, FindingStatus status);
    Task<FindingStatement?> PromoteToConclusionAsync(long findingId, long statementId, string approvedBy);
```

```csharp
public record FindingQueryParameters(int ApplicationId, int EnvironmentId, FindingStatus? Status, FindingSeverity? Severity, FindingType? Type, DateTime? From, DateTime? To);
```

Full resulting file:

```csharp
using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IFindingRepository
{
    Task<Finding?> FindOpenAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type, DateTime cooldownSince);
    Task<Finding> AddAsync(Finding finding);
    Task AddStatementAsync(long findingId, DetectorStatementKind kind, string text);
    Task AddEvidenceAsync(long findingId, EvidenceType evidenceType, long referenceId, string description);
    Task<FindingWithDetails?> GetByIdAsync(long id);
    Task<IReadOnlyList<Finding>> GetDetectedSinceAsync(int applicationId, int environmentId, DateTime since);
    Task<IReadOnlyList<Finding>> QueryAsync(FindingQueryParameters parameters);
    Task<Finding?> UpdateStatusAsync(long findingId, FindingStatus status);
    Task<FindingStatement?> PromoteToConclusionAsync(long findingId, long statementId, string approvedBy);
}

public record FindingWithDetails(Finding Finding, IReadOnlyList<FindingStatement> Statements, IReadOnlyList<Evidence> Evidence)
{
    public long Id => Finding.Id;
    public FindingStatus Status => Finding.Status;
}

public record FindingQueryParameters(int ApplicationId, int EnvironmentId, FindingStatus? Status, FindingSeverity? Severity, FindingType? Type, DateTime? From, DateTime? To);
```

- [ ] **Step 4: Implement the repository methods**

Add to `src/LogsPlatform.Infrastructure/Repositories/FindingRepository.cs`, after `GetDetectedSinceAsync` (before the closing `}` of the class):

```csharp
    public async Task<IReadOnlyList<Finding>> QueryAsync(FindingQueryParameters parameters)
    {
        var query = _context.Findings.AsNoTracking()
            .Where(f => f.ApplicationId == parameters.ApplicationId && f.EnvironmentId == parameters.EnvironmentId);

        if (parameters.Status is not null) query = query.Where(f => f.Status == parameters.Status);
        if (parameters.Severity is not null) query = query.Where(f => f.Severity == parameters.Severity);
        if (parameters.Type is not null) query = query.Where(f => f.Type == parameters.Type);
        if (parameters.From is not null) query = query.Where(f => f.DetectedAt >= parameters.From);
        if (parameters.To is not null) query = query.Where(f => f.DetectedAt <= parameters.To);

        return await query.OrderByDescending(f => f.Severity).ThenByDescending(f => f.DetectedAt).ToListAsync();
    }

    public async Task<Finding?> UpdateStatusAsync(long findingId, FindingStatus status)
    {
        var finding = await _context.Findings.FirstOrDefaultAsync(f => f.Id == findingId);
        if (finding is null)
        {
            return null;
        }

        finding.Status = status;
        await _context.SaveChangesAsync();
        return finding;
    }

    public async Task<FindingStatement?> PromoteToConclusionAsync(long findingId, long statementId, string approvedBy)
    {
        var statement = await _context.FindingStatements.FirstOrDefaultAsync(s => s.Id == statementId && s.FindingId == findingId);
        if (statement is null || statement.Kind != FindingStatementKind.Hypothesis)
        {
            return null;
        }

        statement.Kind = FindingStatementKind.Conclusion;
        statement.ApprovedBy = approvedBy;
        statement.ApprovedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return statement;
    }
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter FindingRepositoryTests`
Expected: PASS — 8/8 tests (3 pre-existing + 5 new).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Domain/Repositories/IFindingRepository.cs src/LogsPlatform.Infrastructure/Repositories/FindingRepository.cs tests/LogsPlatform.Tests/Infrastructure/FindingRepositoryTests.cs
git commit -m "Add QueryAsync/UpdateStatusAsync/PromoteToConclusionAsync to IFindingRepository"
```

---

### Task 2: `FindingsController` — `GET /api/v1/findings`, `GET /api/v1/findings/{id}`

**Suggested model tier:** standard (integration-shaped, mirrors an established pattern but needs care getting the DTO shapes right).

**Files:**
- Create: `src/LogsPlatform.Web/Controllers/FindingsController.cs`
- Modify: `src/LogsPlatform.Web/Contracts/QueryContracts.cs`
- Test: `tests/LogsPlatform.Tests/Web/FindingsControllerTests.cs`

**Interfaces:**
- Consumes: `IFindingRepository.QueryAsync`/`GetByIdAsync` (Task 1), `IApplicationRepository.GetByIdAsync(int)` → `Application?`, `IAppEnvironmentRepository.GetByIdAsync(int)` → `AppEnvironment?`, `IOperationRepository.GetByIdAsync(int)` → `Operation?` (all pre-existing).
- Produces (used by Task 3, and reused by Tasks 6/7 as the DTO shape the UI pages independently assemble): `FindingSummary`, `FindingStatementDto`, `EvidenceDto`, `FindingDetail` records in `LogsPlatform.Web.Contracts`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LogsPlatform.Tests/Web/FindingsControllerTests.cs`:

```csharp
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
public class FindingsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public FindingsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<(int ApplicationId, int EnvironmentId)> CreateAppWithEnvironmentAsync(HttpClient client, string appName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var envResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));
        var env = await envResponse.Content.ReadFromJsonAsync<EnvironmentResponse>();
        return (app.Id, env!.Id);
    }

    private async Task<Finding> SeedFindingAsync(int applicationId, int environmentId, FindingStatus status, FindingSeverity severity)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
        var finding = new Finding
        {
            ApplicationId = applicationId, EnvironmentId = environmentId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "Seeded finding",
            DetectedAt = DateTime.UtcNow, Severity = severity, ConfidenceLevel = ConfidenceLevel.High, Status = status
        };
        context.Findings.Add(finding);
        await context.SaveChangesAsync();
        context.FindingStatements.Add(new FindingStatement { FindingId = finding.Id, Kind = FindingStatementKind.Fact, Text = "A fact.", OrderIndex = 0 });
        context.Evidence.Add(new Evidence { FindingId = finding.Id, EvidenceType = EvidenceType.Deployment, ReferenceId = 1, Description = "Deployment #1" });
        await context.SaveChangesAsync();
        return finding;
    }

    [Fact]
    public async Task GetFindings_FiltersByStatus()
    {
        var client = _factory.CreateClient();
        var (appId, envId) = await CreateAppWithEnvironmentAsync(client, "FindingsQueryTestApp");
        await SeedFindingAsync(appId, envId, FindingStatus.New, FindingSeverity.High);
        await SeedFindingAsync(appId, envId, FindingStatus.Resolved, FindingSeverity.Low);

        var response = await client.GetAsync($"/api/v1/findings?applicationId={appId}&environmentId={envId}&status=New");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<FindingSummary>>();
        Assert.Single(results!);
        Assert.Equal("New", results![0].Status);
    }

    [Fact]
    public async Task GetFindingById_ReturnsStatementsAndEvidence()
    {
        var client = _factory.CreateClient();
        var (appId, envId) = await CreateAppWithEnvironmentAsync(client, "FindingsDetailTestApp");
        var finding = await SeedFindingAsync(appId, envId, FindingStatus.New, FindingSeverity.High);

        var response = await client.GetAsync($"/api/v1/findings/{finding.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<FindingDetail>();
        Assert.Single(detail!.Statements);
        Assert.Single(detail.Evidence);
        Assert.Equal("Deployment", detail.Evidence[0].EvidenceType);
    }

    [Fact]
    public async Task GetFindingById_NotFound_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/findings/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter FindingsControllerTests`
Expected: FAIL — compile error, `FindingSummary`/`FindingDetail` and `/api/v1/findings` don't exist yet.

- [ ] **Step 3: Add the contract records**

Append to `src/LogsPlatform.Web/Contracts/QueryContracts.cs` (after the existing `ExceptionGroupDetail` line):

```csharp
public record FindingSummary(long Id, string Type, string Title, string Severity, string ConfidenceLevel, string Status, DateTime DetectedAt, string ApplicationName, string? OperationName);

public record FindingStatementDto(long Id, string Kind, string Text, int OrderIndex, string? ApprovedBy, DateTime? ApprovedAt);

public record EvidenceDto(long Id, string EvidenceType, long ReferenceId, string Description);

public record FindingDetail(long Id, string Type, string Title, string Severity, string ConfidenceLevel, string Status, DateTime DetectedAt, string ApplicationName, string EnvironmentName, IReadOnlyList<FindingStatementDto> Statements, IReadOnlyList<EvidenceDto> Evidence);
```

- [ ] **Step 4: Implement `FindingsController`**

Create `src/LogsPlatform.Web/Controllers/FindingsController.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/findings")]
public class FindingsController : ControllerBase
{
    private readonly IFindingRepository _findings;
    private readonly IApplicationRepository _applications;
    private readonly IAppEnvironmentRepository _environments;
    private readonly IOperationRepository _operations;

    public FindingsController(IFindingRepository findings, IApplicationRepository applications, IAppEnvironmentRepository environments, IOperationRepository operations)
    {
        _findings = findings;
        _applications = applications;
        _environments = environments;
        _operations = operations;
    }

    [HttpGet]
    public async Task<ActionResult<List<FindingSummary>>> Query(
        [FromQuery] int applicationId, [FromQuery] int environmentId,
        [FromQuery] FindingStatus? status, [FromQuery] FindingSeverity? severity, [FromQuery] FindingType? type,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var findings = await _findings.QueryAsync(new FindingQueryParameters(applicationId, environmentId, status, severity, type, from, to));
        var application = await _applications.GetByIdAsync(applicationId);

        var result = new List<FindingSummary>();
        foreach (var finding in findings)
        {
            string? operationName = null;
            if (finding.ScopeType == AnalysisScopeType.Operation)
            {
                var operation = await _operations.GetByIdAsync((int)finding.ScopeId);
                operationName = operation?.Name;
            }

            result.Add(new FindingSummary(finding.Id, finding.Type.ToString(), finding.Title, finding.Severity.ToString(),
                finding.ConfidenceLevel.ToString(), finding.Status.ToString(), finding.DetectedAt, application?.Name ?? string.Empty, operationName));
        }

        return result;
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<FindingDetail>> GetById(long id)
    {
        var details = await _findings.GetByIdAsync(id);
        if (details is null) return NotFound();

        var application = await _applications.GetByIdAsync(details.Finding.ApplicationId);
        var environment = await _environments.GetByIdAsync(details.Finding.EnvironmentId);

        var statements = details.Statements
            .Select(s => new FindingStatementDto(s.Id, s.Kind.ToString(), s.Text, s.OrderIndex, s.ApprovedBy, s.ApprovedAt))
            .ToList();
        var evidence = details.Evidence
            .Select(e => new EvidenceDto(e.Id, e.EvidenceType.ToString(), e.ReferenceId, e.Description))
            .ToList();

        return new FindingDetail(details.Id, details.Finding.Type.ToString(), details.Finding.Title, details.Finding.Severity.ToString(),
            details.Finding.ConfidenceLevel.ToString(), details.Status.ToString(), details.Finding.DetectedAt,
            application?.Name ?? string.Empty, environment?.Name ?? string.Empty, statements, evidence);
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter FindingsControllerTests`
Expected: PASS — 3/3 tests.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/FindingsController.cs src/LogsPlatform.Web/Contracts/QueryContracts.cs tests/LogsPlatform.Tests/Web/FindingsControllerTests.cs
git commit -m "Add FindingsController GET endpoints (list + detail)"
```

---

### Task 3: `FindingsController` — `PATCH .../status`, `POST .../promote`

**Suggested model tier:** standard.

**Files:**
- Modify: `src/LogsPlatform.Web/Controllers/FindingsController.cs`
- Modify: `src/LogsPlatform.Web/Contracts/QueryContracts.cs`
- Test: `tests/LogsPlatform.Tests/Web/FindingsControllerTests.cs` (append)

**Interfaces:**
- Consumes: `IFindingRepository.UpdateStatusAsync`/`PromoteToConclusionAsync` (Task 1).
- Produces: no new types consumed elsewhere — these are terminal HTTP actions.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LogsPlatform.Tests/Web/FindingsControllerTests.cs` (inside the class, after `GetFindingById_NotFound_Returns404`):

```csharp
    [Fact]
    public async Task UpdateStatus_ValidStatus_Returns204AndPersists()
    {
        var client = _factory.CreateClient();
        var (appId, envId) = await CreateAppWithEnvironmentAsync(client, "FindingsStatusTestApp");
        var finding = await SeedFindingAsync(appId, envId, FindingStatus.New, FindingSeverity.High);

        var response = await client.PatchAsJsonAsync($"/api/v1/findings/{finding.Id}/status", new UpdateFindingStatusRequest("Acknowledged"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var detailResponse = await client.GetAsync($"/api/v1/findings/{finding.Id}");
        var detail = await detailResponse.Content.ReadFromJsonAsync<FindingDetail>();
        Assert.Equal("Acknowledged", detail!.Status);
    }

    [Fact]
    public async Task UpdateStatus_InvalidStatusValue_Returns400()
    {
        var client = _factory.CreateClient();
        var (appId, envId) = await CreateAppWithEnvironmentAsync(client, "FindingsStatusInvalidTestApp");
        var finding = await SeedFindingAsync(appId, envId, FindingStatus.New, FindingSeverity.High);

        var response = await client.PatchAsJsonAsync($"/api/v1/findings/{finding.Id}/status", new UpdateFindingStatusRequest("NotAStatus"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PromoteStatement_HypothesisStatement_Returns204AndPersists()
    {
        var client = _factory.CreateClient();
        var (appId, envId) = await CreateAppWithEnvironmentAsync(client, "FindingsPromoteTestApp");
        var finding = await SeedFindingAsync(appId, envId, FindingStatus.New, FindingSeverity.High);

        long statementId;
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
            var statement = new FindingStatement { FindingId = finding.Id, Kind = FindingStatementKind.Hypothesis, Text = "Maybe a deployment.", OrderIndex = 1 };
            context.FindingStatements.Add(statement);
            await context.SaveChangesAsync();
            statementId = statement.Id;
        }

        var response = await client.PostAsJsonAsync($"/api/v1/findings/{finding.Id}/statements/{statementId}/promote", new PromoteStatementRequest("Dana"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var detailResponse = await client.GetAsync($"/api/v1/findings/{finding.Id}");
        var detail = await detailResponse.Content.ReadFromJsonAsync<FindingDetail>();
        var promoted = detail!.Statements.Single(s => s.Id == statementId);
        Assert.Equal("Conclusion", promoted.Kind);
        Assert.Equal("Dana", promoted.ApprovedBy);
    }

    [Fact]
    public async Task PromoteStatement_BlankApprovedBy_Returns400()
    {
        var client = _factory.CreateClient();
        var (appId, envId) = await CreateAppWithEnvironmentAsync(client, "FindingsPromoteBlankTestApp");
        var finding = await SeedFindingAsync(appId, envId, FindingStatus.New, FindingSeverity.High);

        long statementId;
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
            var statement = new FindingStatement { FindingId = finding.Id, Kind = FindingStatementKind.Hypothesis, Text = "Maybe.", OrderIndex = 1 };
            context.FindingStatements.Add(statement);
            await context.SaveChangesAsync();
            statementId = statement.Id;
        }

        var response = await client.PostAsJsonAsync($"/api/v1/findings/{finding.Id}/statements/{statementId}/promote", new PromoteStatementRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter FindingsControllerTests`
Expected: FAIL — compile error, `UpdateFindingStatusRequest`/`PromoteStatementRequest` don't exist, `PatchAsJsonAsync` needs `System.Net.Http.Json`'s extension (already imported via the `using` at the top of the file), and the two new routes don't exist.

- [ ] **Step 3: Add the request contracts**

Append to `src/LogsPlatform.Web/Contracts/QueryContracts.cs` (after `FindingDetail`):

```csharp
public record UpdateFindingStatusRequest(string Status);

public record PromoteStatementRequest(string ApprovedBy);
```

- [ ] **Step 4: Add the two endpoints**

Add to `src/LogsPlatform.Web/Controllers/FindingsController.cs`, inside the class, after `GetById`:

```csharp
    [HttpPatch("{id:long}/status")]
    public async Task<IActionResult> UpdateStatus(long id, [FromBody] UpdateFindingStatusRequest request)
    {
        if (!Enum.TryParse<FindingStatus>(request.Status, ignoreCase: true, out var status))
        {
            return ValidationProblem($"status: invalid value '{request.Status}'.");
        }

        var finding = await _findings.UpdateStatusAsync(id, status);
        if (finding is null) return NotFound();
        return NoContent();
    }

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

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter FindingsControllerTests`
Expected: PASS — 7/7 tests (3 pre-existing + 4 new).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/FindingsController.cs src/LogsPlatform.Web/Contracts/QueryContracts.cs tests/LogsPlatform.Tests/Web/FindingsControllerTests.cs
git commit -m "Add Finding status update and Promote-to-Conclusion endpoints"
```

---

### Task 4: Wire `DownstreamFailureCorrelator` into `NewExceptionDetector`

**Suggested model tier:** standard (touches 4 files with coordinated constructor-signature changes — integration risk, not just a mechanical edit).

**Files:**
- Modify: `src/LogsPlatform.Web/Services/Analysis/NewExceptionDetector.cs`
- Modify: `tests/LogsPlatform.Tests/Web/NewExceptionDetectorTests.cs`
- Modify: `tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs`
- Modify: `tests/LogsPlatform.Tests/Web/AnalysisEngineBackgroundServiceTests.cs`

**Interfaces:**
- Consumes: `DownstreamFailureCorrelator.RunAsync(Finding finding, string correlationId, int triggeringOperationId, DateTime triggerTimestamp)` (from M4a, unchanged).
- Produces: `NewExceptionDetector`'s constructor gains a 3rd parameter — every call site across the codebase and tests must be updated in this task (both because M4a's existing tests currently construct it with 2 args, and because `AnalysisEngineBackgroundServiceTests`' DI container needs `DownstreamFailureCorrelator` registered or the whole tick will throw at resolution time).

- [ ] **Step 1: Write the failing test**

Add to `tests/LogsPlatform.Tests/Web/NewExceptionDetectorTests.cs`, inside the class, after `RunAsync_OldExistingGroup_NoFindingCreated` — and update the `using` list at the top to add `LogsPlatform.Domain.Repositories`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.EntityFrameworkCore;
```

```csharp
    [Fact]
    public async Task RunAsync_TriggerEventHasCorrelationIdWithDownstreamFailure_AddsHypothesisFromCorrelator()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "NewExceptionDownstreamTestApp");

        var module = new AppModule { ApplicationId = appId, Name = "Billing" };
        context.Modules.Add(module);
        await context.SaveChangesAsync();
        var screenService = new ScreenService { ModuleId = module.Id, Name = "Invoicing", Type = ScreenServiceType.Service };
        context.ScreenServices.Add(screenService);
        await context.SaveChangesAsync();
        var process = new ProcessNode { ScreenServiceId = screenService.Id, Name = "ChargeCard" };
        context.Processes.Add(process);
        await context.SaveChangesAsync();
        var triggerOperation = new Operation { ProcessId = process.Id, Name = "Authorize" };
        var downstreamOperation = new Operation { ProcessId = process.Id, Name = "Capture" };
        context.Operations.AddRange(triggerOperation, downstreamOperation);
        await context.SaveChangesAsync();

        var group = new ExceptionGroup
        {
            ApplicationId = appId, Fingerprint = "fp-downstream", ExceptionType = "System.TimeoutException",
            MessageTemplate = "timed out", RepresentativeStackTrace = "at Foo.Bar()",
            FirstSeenAt = DateTime.UtcNow.AddMinutes(-1), LastSeenAt = DateTime.UtcNow, OccurrenceCount = 1
        };
        context.ExceptionGroups.Add(group);
        await context.SaveChangesAsync();

        var triggerTime = DateTime.UtcNow;
        context.Events.Add(new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = triggerOperation.Id, CorrelationId = "order-99",
            Timestamp = triggerTime, Severity = 17, Message = "boom", ExceptionGroupId = group.Id
        });
        context.Events.Add(new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = downstreamOperation.Id, CorrelationId = "order-99",
            Timestamp = triggerTime.AddSeconds(2), Severity = 17, Message = "downstream failure"
        });
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var downstreamCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var detector = new NewExceptionDetector(context, writer, downstreamCorrelator);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.NewException);
        Assert.NotNull(finding);

        var findingRepositoryForVerify = new FindingRepository(verifyContext);
        var details = await findingRepositoryForVerify.GetByIdAsync(finding!.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
    }
```

Also update the two existing tests' `new NewExceptionDetector(context, writer)` calls to pass a 3rd argument — `RunAsync_RecentlyFirstSeenGroup_CreatesNewExceptionFinding` and `RunAsync_OldExistingGroup_NoFindingCreated`, each right before their `new NewExceptionDetector(...)` line:

```csharp
        var downstreamCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var detector = new NewExceptionDetector(context, writer, downstreamCorrelator);
```

(This replaces the old `var detector = new NewExceptionDetector(context, writer);` line in both of those two pre-existing tests.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter NewExceptionDetectorTests`
Expected: FAIL — compile error, `NewExceptionDetector` has no 3-argument constructor.

- [ ] **Step 3: Modify `NewExceptionDetector`**

Replace the full contents of `src/LogsPlatform.Web/Services/Analysis/NewExceptionDetector.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Services.Analysis;

public class NewExceptionDetector
{
    private static readonly TimeSpan DetectionWindow = TimeSpan.FromMinutes(5);

    private readonly LogsPlatformDbContext _context;
    private readonly FindingWriter _writer;
    private readonly DownstreamFailureCorrelator _downstreamCorrelator;

    public NewExceptionDetector(LogsPlatformDbContext context, FindingWriter writer, DownstreamFailureCorrelator downstreamCorrelator)
    {
        _context = context;
        _writer = writer;
        _downstreamCorrelator = downstreamCorrelator;
    }

    public async Task RunAsync(int applicationId, int environmentId)
    {
        var windowStart = DateTime.UtcNow - DetectionWindow;

        var newGroups = await _context.ExceptionGroups.AsNoTracking()
            .Where(g => g.ApplicationId == applicationId && g.FirstSeenAt >= windowStart)
            .ToListAsync();

        foreach (var group in newGroups)
        {
            var events = await _context.Events.AsNoTracking()
                .Where(e => e.ExceptionGroupId == group.Id && e.EnvironmentId == environmentId)
                .OrderBy(e => e.Timestamp)
                .ToListAsync();

            var environmentIds = events.Select(e => e.EnvironmentId).Distinct();

            foreach (var envId in environmentIds)
            {
                var draft = new FindingDraft(
                    applicationId, envId, FindingType.NewException, AnalysisScopeType.ExceptionGroup, group.Id,
                    $"New exception: {group.ExceptionType}", FindingSeverity.High, ConfidenceLevel.High,
                    new[] { (DetectorStatementKind.Fact, $"This exception type ({group.ExceptionType}) has never been seen before. First occurrence at {group.FirstSeenAt:u}.") });

                var finding = await _writer.WriteAsync(draft);

                var triggerEvent = events.First(e => e.EnvironmentId == envId);
                if (triggerEvent.CorrelationId is not null && triggerEvent.OperationId is not null)
                {
                    await _downstreamCorrelator.RunAsync(finding, triggerEvent.CorrelationId, triggerEvent.OperationId.Value, triggerEvent.Timestamp);
                }
            }
        }
    }
}
```

- [ ] **Step 4: Fix the two other test files broken by the constructor change**

In `tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs`, in the `BuildRunner` method, add a `downstreamFailureCorrelator` local and pass it to `NewExceptionDetector`'s constructor:

```csharp
    private static AnalysisEngineTickRunner BuildRunner(LogsPlatformDbContext context)
    {
        var applicationRepository = new ApplicationRepository(context);
        var environmentRepository = new AppEnvironmentRepository(context);
        var metricsRepository = new MetricsRepository(context);
        var baselineRepository = new BaselineRepository(context);
        var findingRepository = new FindingRepository(context);
        var deploymentRepository = new DeploymentRepository(context);
        var writer = new FindingWriter(findingRepository);
        var downstreamFailureCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var baselineCalculator = new BaselineCalculator(metricsRepository, baselineRepository);
        var rateAnomalyDetector = new RateAnomalyDetector(metricsRepository, baselineRepository, writer);
        var newExceptionDetector = new NewExceptionDetector(context, writer, downstreamFailureCorrelator);
        var customerOutlierDetector = new CustomerOutlierDetector(metricsRepository, writer);
        var deploymentCorrelator = new DeploymentCorrelator(findingRepository, deploymentRepository);

        return new AnalysisEngineTickRunner(
            applicationRepository, environmentRepository, baselineRepository, findingRepository,
            baselineCalculator, rateAnomalyDetector, newExceptionDetector, customerOutlierDetector, deploymentCorrelator);
    }
```

(`rateAnomalyDetector`'s construction is unchanged in this task — Task 5 will update it.)

In `tests/LogsPlatform.Tests/Web/AnalysisEngineBackgroundServiceTests.cs`, in `BuildService`, add a registration for `DownstreamFailureCorrelator` right after the `DeploymentCorrelator` line:

```csharp
        services.AddSingleton<DeploymentCorrelator>();
        services.AddSingleton<DownstreamFailureCorrelator>();
        services.AddScoped<AnalysisEngineTickRunner>();
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter "NewExceptionDetectorTests|AnalysisEngineTickRunnerTests|AnalysisEngineBackgroundServiceTests"`
Expected: PASS — 3/3 + 2/2 + 1/1 = 6/6 tests.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Services/Analysis/NewExceptionDetector.cs tests/LogsPlatform.Tests/Web/NewExceptionDetectorTests.cs tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs tests/LogsPlatform.Tests/Web/AnalysisEngineBackgroundServiceTests.cs
git commit -m "Wire DownstreamFailureCorrelator into NewExceptionDetector"
```

---

### Task 5: Wire `DownstreamFailureCorrelator` into `RateAnomalyDetector`

**Suggested model tier:** standard-to-high (the representative-event lookup is new logic, plus another coordinated constructor-signature change).

**Files:**
- Modify: `src/LogsPlatform.Web/Services/Analysis/RateAnomalyDetector.cs`
- Modify: `tests/LogsPlatform.Tests/Web/RateAnomalyDetectorTests.cs`
- Modify: `tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs`

**Interfaces:**
- Consumes: `DownstreamFailureCorrelator.RunAsync` (unchanged, from M4a).
- Produces: `RateAnomalyDetector`'s constructor gains 2 parameters (`DownstreamFailureCorrelator`, `LogsPlatformDbContext`).

- [ ] **Step 1: Write the failing test**

Add to `tests/LogsPlatform.Tests/Web/RateAnomalyDetectorTests.cs`, inside the class, after `RunAsync_CurrentHourWithinNormalRange_NoFindingCreated` — and update the `using` list at the top to add `LogsPlatform.Web.Services.Analysis`'s sibling class references are already imported via the existing `using LogsPlatform.Web.Services.Analysis;` line, so no new `using` is needed:

```csharp
    [Fact]
    public async Task RunAsync_ErrorSpikeWithCorrelatedDownstreamFailure_AddsHypothesisFromCorrelator()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "RateAnomalyDownstreamTestApp");

        var triggerOperation = await context.Operations.FirstAsync(o => o.Id == opId);
        var downstreamOperation = new Operation { ProcessId = triggerOperation.ProcessId, Name = "Capture" };
        context.Operations.Add(downstreamOperation);
        await context.SaveChangesAsync();

        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        context.Baselines.Add(new Baseline
        {
            ApplicationId = appId, EnvironmentId = envId, ScopeType = AnalysisScopeType.Operation, ScopeId = opId,
            MetricType = AnalysisMetricType.EventCount, BucketHourOfDay = (byte)currentHourStart.Hour,
            MeanValue = 5, StdDevValue = 1, SampleCount = 20, LastUpdatedAt = DateTime.UtcNow
        });
        for (var i = 0; i < 40; i++)
        {
            context.Events.Add(new Event
            {
                ApplicationId = appId, EnvironmentId = envId, OperationId = opId,
                Timestamp = currentHourStart.AddMinutes(i % 59), Severity = 17, Message = $"spike-{i}"
            });
        }
        var triggerTime = currentHourStart.AddMinutes(45);
        context.Events.Add(new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CorrelationId = "order-77",
            Timestamp = triggerTime, Severity = 17, Message = "the trigger event"
        });
        context.Events.Add(new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = downstreamOperation.Id, CorrelationId = "order-77",
            Timestamp = triggerTime.AddSeconds(3), Severity = 17, Message = "downstream failure"
        });
        await context.SaveChangesAsync();

        var metrics = new MetricsRepository(context);
        var baselines = new BaselineRepository(context);
        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var downstreamCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var detector = new RateAnomalyDetector(metrics, baselines, writer, downstreamCorrelator, context);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.ErrorSpike);
        Assert.NotNull(finding);

        var findingRepositoryForVerify = new FindingRepository(verifyContext);
        var details = await findingRepositoryForVerify.GetByIdAsync(finding!.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
    }
```

Also update the two existing tests' `new RateAnomalyDetector(metrics, baselines, writer)` calls (in `RunAsync_CurrentHourFarAboveBaseline_CreatesErrorSpikeFinding` and `RunAsync_CurrentHourWithinNormalRange_NoFindingCreated`) to:

```csharp
        var downstreamCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var detector = new RateAnomalyDetector(metrics, baselines, writer, downstreamCorrelator, context);
```

(These two pre-existing tests seed no `CorrelationId` on any Event, so the new correlator branch finds no trigger event and no behavior changes for them — only the constructor call needs updating.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter RateAnomalyDetectorTests`
Expected: FAIL — compile error, `RateAnomalyDetector` has no 5-argument constructor.

- [ ] **Step 3: Modify `RateAnomalyDetector`**

Replace the full contents of `src/LogsPlatform.Web/Services/Analysis/RateAnomalyDetector.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Services.Analysis;

public class RateAnomalyDetector
{
    private const double SpikeThreshold = 3;
    private const double MinStdDevFloor = 0.5;
    private const double MinMeaningfulActivity = 5;

    private readonly IMetricsRepository _metrics;
    private readonly IBaselineRepository _baselines;
    private readonly FindingWriter _writer;
    private readonly DownstreamFailureCorrelator _downstreamCorrelator;
    private readonly LogsPlatformDbContext _context;

    public RateAnomalyDetector(IMetricsRepository metrics, IBaselineRepository baselines, FindingWriter writer, DownstreamFailureCorrelator downstreamCorrelator, LogsPlatformDbContext context)
    {
        _metrics = metrics;
        _baselines = baselines;
        _writer = writer;
        _downstreamCorrelator = downstreamCorrelator;
        _context = context;
    }

    public async Task RunAsync(int applicationId, int environmentId)
    {
        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        var hour = (byte)currentHourStart.Hour;

        var operationIds = await _metrics.GetActiveOperationIdsAsync(applicationId, environmentId);
        foreach (var operationId in operationIds)
        {
            var eventCount = await _metrics.GetHourlyEventCountAsync(applicationId, environmentId, operationId, currentHourStart);
            await EvaluateAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, AnalysisMetricType.EventCount, hour, currentHourStart,
                current: eventCount, positiveType: FindingType.ErrorSpike, negativeType: FindingType.MissingActivity,
                titlePrefix: $"Operation {operationId}");

            var averageDuration = await _metrics.GetHourlyAverageDurationAsync(applicationId, environmentId, operationId, currentHourStart);
            if (averageDuration is not null)
            {
                await EvaluateAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, AnalysisMetricType.DurationMs, hour, currentHourStart,
                    current: averageDuration.Value, positiveType: FindingType.PerformanceDegradation, negativeType: null,
                    titlePrefix: $"Operation {operationId}");
            }
        }

        var exceptionGroupIds = await _metrics.GetActiveExceptionGroupIdsAsync(applicationId, environmentId);
        foreach (var exceptionGroupId in exceptionGroupIds)
        {
            var exceptionCount = await _metrics.GetHourlyExceptionCountAsync(applicationId, environmentId, exceptionGroupId, currentHourStart);
            await EvaluateAsync(applicationId, environmentId, AnalysisScopeType.ExceptionGroup, exceptionGroupId, AnalysisMetricType.ExceptionCount, hour, currentHourStart,
                current: exceptionCount, positiveType: FindingType.ErrorSpike, negativeType: null,
                titlePrefix: $"ExceptionGroup {exceptionGroupId}");
        }
    }

    private async Task EvaluateAsync(
        int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, AnalysisMetricType metricType, byte hour, DateTime hourStart,
        double current, FindingType positiveType, FindingType? negativeType, string titlePrefix)
    {
        var baseline = await _baselines.GetAsync(applicationId, environmentId, scopeType, scopeId, metricType, hour);
        if (baseline is null)
        {
            return;
        }

        var stdDev = Math.Max(baseline.StdDevValue, MinStdDevFloor);
        var z = (current - baseline.MeanValue) / stdDev;

        if (z > SpikeThreshold)
        {
            await WriteRateFindingAsync(applicationId, environmentId, scopeType, scopeId, positiveType, z, current, baseline, titlePrefix, "above", hourStart);
        }
        else if (negativeType is not null && z < -SpikeThreshold && baseline.MeanValue > MinMeaningfulActivity)
        {
            await WriteRateFindingAsync(applicationId, environmentId, scopeType, scopeId, negativeType.Value, z, current, baseline, titlePrefix, "below", hourStart);
        }
    }

    private async Task WriteRateFindingAsync(
        int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, FindingType type,
        double z, double current, Baseline baseline, string titlePrefix, string direction, DateTime hourStart)
    {
        var absZ = Math.Abs(z);
        var severity = absZ > 5 ? FindingSeverity.High : FindingSeverity.Medium;
        var confidence = absZ > 5 && baseline.SampleCount >= 14 ? ConfidenceLevel.High
            : baseline.SampleCount < 14 ? ConfidenceLevel.Low
            : ConfidenceLevel.Medium;

        var fact = $"{titlePrefix} recorded {current:F1} in the current hour.";
        var observation = $"That is {direction} the normal rate for this hour (baseline: {baseline.MeanValue:F1}±{baseline.StdDevValue:F1}, based on {baseline.SampleCount} days).";

        var draft = new FindingDraft(
            applicationId, environmentId, type, scopeType, scopeId,
            $"{titlePrefix}: {type}", severity, confidence,
            new[] { (DetectorStatementKind.Fact, fact), (DetectorStatementKind.Observation, observation) });

        var finding = await _writer.WriteAsync(draft);

        if (type == FindingType.ErrorSpike && scopeType == AnalysisScopeType.Operation)
        {
            var operationId = (int)scopeId;
            var hourEnd = hourStart.AddHours(1);
            var triggerEvent = await _context.Events.AsNoTracking()
                .Where(e => e.ApplicationId == applicationId && e.EnvironmentId == environmentId
                    && e.OperationId == operationId && e.Timestamp >= hourStart && e.Timestamp < hourEnd && e.CorrelationId != null)
                .OrderByDescending(e => e.Timestamp)
                .FirstOrDefaultAsync();

            if (triggerEvent is not null)
            {
                await _downstreamCorrelator.RunAsync(finding, triggerEvent.CorrelationId!, operationId, triggerEvent.Timestamp);
            }
        }
    }
}
```

- [ ] **Step 4: Fix `AnalysisEngineTickRunnerTests.cs`'s `BuildRunner`**

In `tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs`, update the `rateAnomalyDetector` line inside `BuildRunner` (the `downstreamFailureCorrelator` local already exists from Task 4):

```csharp
        var rateAnomalyDetector = new RateAnomalyDetector(metricsRepository, baselineRepository, writer, downstreamFailureCorrelator, context);
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter "RateAnomalyDetectorTests|AnalysisEngineTickRunnerTests|AnalysisEngineBackgroundServiceTests"`
Expected: PASS — 3/3 + 2/2 + 1/1 = 6/6 tests.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test tests/LogsPlatform.Tests`
Expected: all tests pass (267 pre-M4b baseline + Tasks 1–5's new tests).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Services/Analysis/RateAnomalyDetector.cs tests/LogsPlatform.Tests/Web/RateAnomalyDetectorTests.cs tests/LogsPlatform.Tests/Web/AnalysisEngineTickRunnerTests.cs
git commit -m "Wire DownstreamFailureCorrelator into RateAnomalyDetector for Operation-scoped ErrorSpike findings"
```

---

### Task 6: "What's Unusual" home page (replaces `Home.razor`) + `NavMenu` update

**Suggested model tier:** standard (UI logic, Hebrew copy, no algorithmic risk — but needs to match established Razor conventions exactly).

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Pages/Home.razor` (full rewrite)
- Modify: `src/LogsPlatform.Web/Components/Layout/NavMenu.razor`

**Interfaces:**
- Consumes: `IFindingRepository.QueryAsync` (Task 1), `IApplicationRepository.GetByIdAsync`, `IOperationRepository.GetByIdAsync` (both pre-existing), `AppEnvironmentSelector` component (pre-existing, from M3).
- Produces: nothing consumed by later tasks — this is a leaf UI page. Per this project's established convention (confirmed in M3's design memory), Blazor pages call repositories directly via DI rather than through `FindingsController`'s HTTP routes — Task 2/3's controller exists for external consumers only.

- [ ] **Step 1: Replace `Home.razor`**

Replace the full contents of `src/LogsPlatform.Web/Components/Pages/Home.razor`:

```razor
@* src/LogsPlatform.Web/Components/Pages/Home.razor *@
@page "/"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web.Components.Shared
@inject IFindingRepository FindingRepository
@inject IApplicationRepository ApplicationRepository
@inject IOperationRepository OperationRepository
@inject NavigationManager Navigation
@rendermode InteractiveServer

<h1>מה חריג</h1>

<AppEnvironmentSelector ApplicationId="_applicationId" EnvironmentId="_environmentId" OnSelectionChanged="OnAppEnvironmentChangedAsync" />

@if (_applicationId is not null && _environmentId is not null)
{
    <div class="card mb-4">
        <div class="card-body">
            <div class="row g-3">
                <div class="col-auto">
                    <label class="form-label">חומרה</label>
                    <select class="form-select form-select-sm" value="@_severity" @onchange="OnSeverityChangedAsync">
                        <option value="">-- הכל --</option>
                        @foreach (var value in Enum.GetValues<FindingSeverity>())
                        {
                            <option value="@value">@value</option>
                        }
                    </select>
                </div>
                <div class="col-auto">
                    <label class="form-label">רמת ביטחון</label>
                    <select class="form-select form-select-sm" value="@_confidence" @onchange="OnConfidenceChangedAsync">
                        <option value="">-- הכל --</option>
                        @foreach (var value in Enum.GetValues<ConfidenceLevel>())
                        {
                            <option value="@value">@value</option>
                        }
                    </select>
                </div>
                <div class="col-auto">
                    <label class="form-label">סטטוס</label>
                    <select class="form-select form-select-sm" value="@_status" @onchange="OnStatusChangedAsync">
                        <option value="">-- הכל --</option>
                        @foreach (var value in Enum.GetValues<FindingStatus>())
                        {
                            <option value="@value">@value</option>
                        }
                    </select>
                </div>
                <div class="col-auto">
                    <label class="form-label">סוג</label>
                    <select class="form-select form-select-sm" value="@_type" @onchange="OnTypeChangedAsync">
                        <option value="">-- הכל --</option>
                        @foreach (var value in Enum.GetValues<FindingType>())
                        {
                            <option value="@value">@value</option>
                        }
                    </select>
                </div>
            </div>
        </div>
    </div>

    @if (_results.Count == 0)
    {
        <p class="text-muted">לא נמצאו חריגות בטווח הזמן/הסינון הנוכחי.</p>
    }
    else
    {
        <table class="table table-striped table-hover align-middle">
            <thead>
                <tr>
                    <th>סוג</th>
                    <th>חומרה</th>
                    <th>ביטחון</th>
                    <th>כותרת</th>
                    <th>אפליקציה / פעולה</th>
                    <th>זוהה ב-</th>
                    <th>סטטוס</th>
                </tr>
            </thead>
            <tbody>
                @foreach (var finding in _results)
                {
                    <tr @key="finding.Id" style="cursor:pointer" @onclick="() => Navigation.NavigateTo($"/findings/{finding.Id}")">
                        <td>@finding.Type</td>
                        <td><span class="badge @SeverityBadgeClass(finding.Severity)">@finding.Severity</span></td>
                        <td><span class="badge @ConfidenceBadgeClass(finding.ConfidenceLevel)">@finding.ConfidenceLevel</span></td>
                        <td>@finding.Title</td>
                        <td>@_applicationName@(_operationNames.TryGetValue(finding.Id, out var name) ? $" / {name}" : "")</td>
                        <td>@finding.DetectedAt.ToString("u")</td>
                        <td>@finding.Status</td>
                    </tr>
                }
            </tbody>
        </table>
    }
}

@code {
    private int? _applicationId;
    private int? _environmentId;
    private string _applicationName = string.Empty;

    private FindingSeverity? _severity;
    private ConfidenceLevel? _confidence;
    private FindingStatus? _status;
    private FindingType? _type;

    private List<Finding> _results = new();
    private Dictionary<long, string> _operationNames = new();

    private async Task OnAppEnvironmentChangedAsync((int ApplicationId, int EnvironmentId) selection)
    {
        _applicationId = selection.ApplicationId;
        _environmentId = selection.EnvironmentId;
        var application = await ApplicationRepository.GetByIdAsync(_applicationId.Value);
        _applicationName = application?.Name ?? string.Empty;
        await SearchAsync();
    }

    private async Task OnSeverityChangedAsync(ChangeEventArgs e)
    {
        _severity = Enum.TryParse<FindingSeverity>(e.Value?.ToString(), out var value) ? value : null;
        await SearchAsync();
    }

    private async Task OnConfidenceChangedAsync(ChangeEventArgs e)
    {
        _confidence = Enum.TryParse<ConfidenceLevel>(e.Value?.ToString(), out var value) ? value : null;
        await SearchAsync();
    }

    private async Task OnStatusChangedAsync(ChangeEventArgs e)
    {
        _status = Enum.TryParse<FindingStatus>(e.Value?.ToString(), out var value) ? value : null;
        await SearchAsync();
    }

    private async Task OnTypeChangedAsync(ChangeEventArgs e)
    {
        _type = Enum.TryParse<FindingType>(e.Value?.ToString(), out var value) ? value : null;
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        if (_applicationId is null || _environmentId is null) return;

        _results = (await FindingRepository.QueryAsync(new FindingQueryParameters(
            _applicationId.Value, _environmentId.Value, _status, _severity, _type, null, null))).ToList();

        _operationNames.Clear();
        foreach (var finding in _results.Where(f => f.ScopeType == AnalysisScopeType.Operation))
        {
            if (_operationNames.ContainsKey(finding.Id)) continue;
            var operation = await OperationRepository.GetByIdAsync((int)finding.ScopeId);
            if (operation is not null)
            {
                _operationNames[finding.Id] = operation.Name;
            }
        }
    }

    private static string SeverityBadgeClass(FindingSeverity severity) => severity switch
    {
        FindingSeverity.High => "text-bg-danger",
        FindingSeverity.Medium => "text-bg-warning",
        _ => "text-bg-secondary"
    };

    private static string ConfidenceBadgeClass(ConfidenceLevel confidence) => confidence switch
    {
        ConfidenceLevel.High => "text-bg-success",
        ConfidenceLevel.Medium => "text-bg-warning",
        _ => "text-bg-secondary"
    };
}
```

- [ ] **Step 2: Update `NavMenu.razor`**

Replace the full contents of `src/LogsPlatform.Web/Components/Layout/NavMenu.razor`:

```razor
@* src/LogsPlatform.Web/Components/Layout/NavMenu.razor *@
<nav class="navbar navbar-expand navbar-dark bg-dark mb-4">
    <div class="container-fluid">
        <a class="navbar-brand" href="/">LogsPlatform</a>
        <ul class="navbar-nav">
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
            <li class="nav-item">
                <NavLink class="nav-link" href="/admin/applications" Match="NavLinkMatch.Prefix">
                    ניהול
                </NavLink>
            </li>
        </ul>
    </div>
</nav>
```

- [ ] **Step 3: Verify it builds**

Run: `dotnet build`
Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/Home.razor src/LogsPlatform.Web/Components/Layout/NavMenu.razor
git commit -m "Replace Home.razor with the What's Unusual Findings list"
```

---

### Task 7: Finding detail page (`/findings/{id}`)

**Suggested model tier:** standard (the Kind-colored statement rendering + lifecycle actions are real UI logic, not boilerplate).

**Files:**
- Create: `src/LogsPlatform.Web/Components/Pages/FindingDetail.razor`

**Interfaces:**
- Consumes: `IFindingRepository.GetByIdAsync`/`UpdateStatusAsync`/`PromoteToConclusionAsync` (Task 1), `IApplicationRepository.GetByIdAsync`, `IAppEnvironmentRepository.GetByIdAsync`, `IOperationRepository.GetByIdAsync` (all pre-existing).
- Produces: nothing consumed elsewhere — leaf page, linked to from `Home.razor` (Task 6).

- [ ] **Step 1: Create the page**

Create `src/LogsPlatform.Web/Components/Pages/FindingDetail.razor`:

```razor
@* src/LogsPlatform.Web/Components/Pages/FindingDetail.razor *@
@page "/findings/{Id:long}"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@inject IFindingRepository FindingRepository
@inject IApplicationRepository ApplicationRepository
@inject IOperationRepository OperationRepository
@rendermode InteractiveServer

@if (_details is null)
{
    <p>לא נמצא.</p>
}
else
{
    <h1>@_details.Finding.Title</h1>
    <p>
        <span class="badge @SeverityBadgeClass(_details.Finding.Severity)">@_details.Finding.Severity</span>
        <span class="badge @ConfidenceBadgeClass(_details.Finding.ConfidenceLevel)">@_details.Finding.ConfidenceLevel</span>
        <span class="badge text-bg-light text-dark">@_details.Finding.Status</span>
    </p>

    <div class="mb-4">
        @foreach (var statement in _details.Statements)
        {
            <div class="card mb-2 @StatementCardClass(statement.Kind)">
                <div class="card-body py-2">
                    <span class="badge @StatementBadgeClass(statement.Kind) mb-1">@StatementKindLabel(statement.Kind)</span>
                    <p class="mb-1">@statement.Text</p>
                    @if (statement.Kind == FindingStatementKind.Conclusion)
                    {
                        <p class="text-muted small mb-0">אושר ע"י @statement.ApprovedBy ב-@statement.ApprovedAt?.ToString("u")</p>
                    }
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
                        else
                        {
                            <button class="btn btn-sm btn-outline-success mt-1" @onclick="() => { _promotingStatementId = statement.Id; _approvalNote = string.Empty; }">קדם/י ל-Conclusion</button>
                        }
                    }
                </div>
            </div>
        }
    </div>

    <h4>ראיות</h4>
    <ul class="list-group mb-4">
        @foreach (var item in _details.Evidence)
        {
            <li class="list-group-item">
                @if (item.EvidenceType.ToString() == "Event")
                {
                    <a href="/search?applicationId=@_details.Finding.ApplicationId&environmentId=@_details.Finding.EnvironmentId">@item.Description</a>
                }
                else if (item.EvidenceType.ToString() == "ExceptionGroup")
                {
                    <a href="/exceptions/@item.ReferenceId">@item.Description</a>
                }
                else if (item.EvidenceType.ToString() == "Deployment")
                {
                    <a href="/admin/applications">@item.Description</a>
                }
                else
                {
                    @item.Description
                }
            </li>
        }
    </ul>

    <div class="d-flex gap-2 mb-4">
        <button class="btn btn-sm btn-outline-primary" @onclick="() => UpdateStatusAsync(FindingStatus.Acknowledged)">אשר קבלה</button>
        <button class="btn btn-sm btn-outline-success" @onclick="() => UpdateStatusAsync(FindingStatus.Resolved)">סמן כנפתר</button>
        <button class="btn btn-sm btn-outline-secondary" @onclick="() => UpdateStatusAsync(FindingStatus.Dismissed)">התעלם</button>
    </div>

    @if (_operationName is not null)
    {
        <a class="btn btn-sm btn-outline-primary" href="/search?applicationId=@_details.Finding.ApplicationId&environmentId=@_details.Finding.EnvironmentId">צפייה באירועים המקוריים</a>
    }
    @if (_details.Finding.ScopeType == AnalysisScopeType.ExceptionGroup)
    {
        <a class="btn btn-sm btn-outline-primary" href="/exceptions/@_details.Finding.ScopeId">צפייה בקבוצת ה-Exception</a>
    }
}

@code {
    [Parameter] public long Id { get; set; }

    private FindingWithDetails? _details;
    private string? _operationName;
    private long? _promotingStatementId;
    private string _approvalNote = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _details = await FindingRepository.GetByIdAsync(Id);
        _operationName = null;
        if (_details is not null && _details.Finding.ScopeType == AnalysisScopeType.Operation)
        {
            var operation = await OperationRepository.GetByIdAsync((int)_details.Finding.ScopeId);
            _operationName = operation?.Name;
        }
    }

    private async Task UpdateStatusAsync(FindingStatus status)
    {
        await FindingRepository.UpdateStatusAsync(Id, status);
        await LoadAsync();
    }

    private async Task PromoteAsync()
    {
        if (_promotingStatementId is null || string.IsNullOrWhiteSpace(_approvalNote)) return;
        await FindingRepository.PromoteToConclusionAsync(Id, _promotingStatementId.Value, _approvalNote);
        _promotingStatementId = null;
        _approvalNote = string.Empty;
        await LoadAsync();
    }

    private static string SeverityBadgeClass(FindingSeverity severity) => severity switch
    {
        FindingSeverity.High => "text-bg-danger",
        FindingSeverity.Medium => "text-bg-warning",
        _ => "text-bg-secondary"
    };

    private static string ConfidenceBadgeClass(ConfidenceLevel confidence) => confidence switch
    {
        ConfidenceLevel.High => "text-bg-success",
        ConfidenceLevel.Medium => "text-bg-warning",
        _ => "text-bg-secondary"
    };

    private static string StatementCardClass(FindingStatementKind kind) => kind switch
    {
        FindingStatementKind.Observation => "border-primary",
        FindingStatementKind.Hypothesis => "border-warning",
        FindingStatementKind.Conclusion => "border-success",
        _ => ""
    };

    private static string StatementBadgeClass(FindingStatementKind kind) => kind switch
    {
        FindingStatementKind.Observation => "text-bg-primary",
        FindingStatementKind.Hypothesis => "text-bg-warning",
        FindingStatementKind.Conclusion => "text-bg-success",
        _ => "text-bg-secondary"
    };

    private static string StatementKindLabel(FindingStatementKind kind) => kind switch
    {
        FindingStatementKind.Fact => "עובדה",
        FindingStatementKind.Observation => "תצפית",
        FindingStatementKind.Hypothesis => "השערה — טרם אושר",
        FindingStatementKind.Conclusion => "מסקנה",
        _ => kind.ToString()
    };
}
```

Note: `FindingWithDetails` (returned by `GetByIdAsync`) wraps the raw `Finding`/`FindingStatement`/`Evidence` entities directly — not the `FindingDetail`/`FindingStatementDto`/`EvidenceDto` DTOs from Task 2 (those are the Controller's own HTTP response shape, used only by external API consumers). This page reads `statement.Kind` as the real `FindingStatementKind` enum (for the `switch` expressions above) and `item.EvidenceType` as the real `EvidenceType` enum value — the three `@if` conditions in the "ראיות" section above already call `item.EvidenceType.ToString() == "..."` rather than comparing the enum directly to a string literal, which wouldn't compile.

- [ ] **Step 2: Verify it builds**

Run: `dotnet build`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Run the full test suite one more time**

Run: `dotnet test tests/LogsPlatform.Tests`
Expected: all tests still pass (UI pages have no automated tests, per this project's established convention — Blazor pages are verified via live manual browser checks at finish time, not component tests).

- [ ] **Step 4: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/FindingDetail.razor
git commit -m "Add Finding detail page with Kind-colored statements, Evidence links, and lifecycle actions"
```

---

## Self-Review Notes

**Spec coverage:** All of the design doc's §3 (repository layer), §4 (Findings API), §5 (detector wiring), §6 (UI) are covered by Tasks 1–7. §7's testing approach (real-DB repository/controller tests, no UI component tests, live manual walkthrough at finish) is followed throughout — the live walkthrough itself belongs to `finishing-a-development-branch`, not a numbered task here.

**A real defect caught during this plan's own writing, before any code existed:** the design doc's UI section implied the Findings list/detail pages would consume `GET /api/v1/findings` over HTTP, but this project's actual established convention (confirmed during M3) is that Blazor pages call repositories directly via DI, never through the Web API's own controllers. Tasks 6 and 7 inject `IFindingRepository` directly — `FindingsController` (Tasks 2–3) exists purely for external HTTP consumers (Swagger, scripts), matching every other Query API controller in this codebase.

**A second real defect caught during writing:** `NewExceptionDetector` and `RateAnomalyDetector` both gain a `DownstreamFailureCorrelator` constructor dependency, but 3 *existing* M4a test files construct these detectors directly (`NewExceptionDetectorTests.cs`, `RateAnomalyDetectorTests.cs`, `AnalysisEngineTickRunnerTests.cs`'s `BuildRunner`) or via a hand-built `ServiceCollection` (`AnalysisEngineBackgroundServiceTests.cs`'s `BuildService`, which doesn't yet register `DownstreamFailureCorrelator`). Tasks 4 and 5 explicitly update all 4 of these files — missing any one of them would either fail to compile (the two detector test files) or throw `InvalidOperationException` at DI-resolution time inside the one already-passing concurrency-guard test (`AnalysisEngineBackgroundServiceTests`), since `AnalysisEngineTickRunner`'s full dependency graph gets resolved the moment `TryRunOneTickAsync` runs.

**Type consistency:** `FindingQueryParameters`, `FindingSummary`, `FindingDetail`, `FindingStatementDto`, `EvidenceDto` are used identically across Tasks 1–3 — verified by re-reading each call site. `IFindingRepository.GetByIdAsync`'s existing `FindingWithDetails` return type (raw entities) is deliberately *not* reused as the Controller's DTO shape and vice versa — Task 7's note under Step 1 calls this out explicitly so the UI page's code doesn't accidentally try to bind against the Controller's DTOs.

**Illegal mixed named/positional C# arguments (the M3/M4a-established check):** every multi-argument constructor/record call in this plan's code is either fully positional in declared order (`new FindingQueryParameters(appId, envId, FindingStatus.New, null, null, null, null)`, all `new Finding { ... }` object-initializer calls) or fully named (none needed here) — no call mixes named-then-positional. Checked specifically in the new test methods (Tasks 1, 2, 3, 4, 5) and the two Razor pages (Tasks 6, 7).

**DI lifetime audit (per M4a's critical Scoped-vs-Singleton catch):** this plan adds no new `BackgroundService`/`AddHostedService` registration, so the specific singleton-consuming-scoped hazard from M4a doesn't recur. `FindingsController` and the two Razor pages are all request-scoped by ASP.NET Core/Blazor Server's own lifetime rules and depend only on already-`Scoped` repositories — no lifetime mismatch. `RateAnomalyDetector`/`NewExceptionDetector`'s new `DownstreamFailureCorrelator` dependency is itself `Scoped` (registered in M4a, unchanged) and both detectors are already `Scoped`, so no mismatch there either.

**FK/cascade behavior:** no new entities or migrations in this plan — Tasks 1–7 only add repository methods, controller endpoints, constructor parameters, and UI pages. No schema changes.
