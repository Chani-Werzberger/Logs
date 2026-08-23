# M5: Synthetic Data Generator + Scenario Test Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a `SyntheticDataGenerator` that produces 40 days of realistic RetailPulse/FieldOps history through the real Ingestion API, injects the 6 mandated anomaly scenarios, and two automated xUnit tests that prove the Analysis Engine detects exactly what it should (6/6 correct Findings) and nothing it shouldn't (0 false positives across 3 seeds) — the project's actual go/no-go acceptance gate.

**Architecture:** A new dependency-free `tests/SyntheticDataGenerator` class library holds pure generation logic (hour-of-day traffic curves + 6 scenario injectors). New xUnit tests in `tests/LogsPlatform.Tests/Scenario/` own all HTTP/DI wiring: they build the domain via the real Admin API, ingest events via the real `POST /api/v1/ingest/events` in batches, then resolve `AnalysisEngineTickRunner` directly from a real `WebApplicationFactory`'s DI container and call `RunOneTickAsync()` explicitly.

**Tech Stack:** .NET 10, EF Core 10, SQL Server (real DB, no mocking), `WebApplicationFactory<Program>`, xUnit.

## Global Constraints

- **False-positive seed count:** 3 seeds (not the spec's suggested 5), fixed acceptance-test seed for the 6-scenario run.
- **Detection trigger:** tests call `AnalysisEngineTickRunner.RunOneTickAsync()` directly via `factory.Services.CreateScope()` — never wait on the real `AnalysisEngineBackgroundService` timer.
- **All timing is relative to `DateTime.UtcNow` at generation/run time**, never hardcoded calendar dates.
- **Ingestion only** — no direct `DbContext` writes for events/exceptions; Admin API only for hierarchy/customers/deployments (also never direct DB writes).
- **Customer-Specific Anomaly uses `ConfirmOrder`** (the one unused Operation under `CreateOrder`, since `CreateOrder` itself is a Process, not a leaf Operation).
- **Exact detector timing windows this plan's magnitudes and generation windows must respect** (verified against the current code, not assumed):
  - `RateAnomalyDetector` (ErrorSpike/MissingActivity/PerformanceDegradation) evaluates **only the current UTC hour bucket** (`now.Date.AddHours(now.Hour)`) each tick — not a range, not "sometime in the last few days." Scenario data for these 3 types must land in that exact bucket.
  - `BaselineCalculator` looks back **day-offset 1 through 28** relative to "today" — it **never includes today**. Quiet-day generation for days used in baselines must be historical (`dayOffset >= 1`), never "today."
  - `NewExceptionDetector` requires `ExceptionGroup.FirstSeenAt` within **5 minutes** of tick time.
  - `DeploymentCorrelator` requires the `Deployment.DeployedAt` within **60 minutes** before `Finding.DetectedAt` (≈ tick time).
  - `CustomerOutlierDetector` uses a **24-hour** window ending at tick time and does **not** use `Baseline`/`BaselineCalculator` at all — it computes peer statistics fresh from that hour's ingested data.
  - `GetActiveOperationIdsAsync`/`GetActiveExceptionGroupIdsAsync` (used by `BaselineCalculator` and `RateAnomalyDetector`) only consider Operations/ExceptionGroups with **any** activity in the last 28 days — an Operation with zero historical (day-offset ≥ 1) events gets no `Baseline` row at all, so `RateAnomalyDetector` silently skips it (`if (baseline is null) return;`). This plan deliberately relies on that: Operations used only as "today"-only data (New Exception's trigger/downstream Operations, Customer Anomaly's `ConfirmOrder`) never get historical quiet-day traffic, so they can never accidentally trigger an unrelated rate-based Finding.
- **A real pre-existing gap this plan fixes (Task 2):** `CustomerOutlierDetector.cs` hardcodes `ConfidenceLevel.Medium` unconditionally, but `08-Analysis-ו-Anomaly-Detection.md` §6's confidence table applies uniformly to every z-score-based detector (`|z|>5 AND SampleCount>=14` → High). Fixed to use total compared-customer count as the sample-count analog, matching `RateAnomalyDetector`'s exact formula shape — otherwise the Customer Anomaly Finding could never reach `ConfidenceLevel.High`, contradicting `11-Test-Strategy.md` §3's stated "Confidence=High on all 6" acceptance criterion.
- **Magnitude design principle:** every injected anomaly uses a large margin over its threshold (typically 4-5x the expected z-score threshold), not a value tuned to just barely cross it — since the actual realized stddev depends on the seeded RNG's specific draws, not a closed-form guarantee.

---

### Task 1: Scaffold `SyntheticDataGenerator` project + shared HTTP helpers

**Suggested model tier:** cheap-to-standard (mostly mechanical scaffolding, but the batching/hierarchy-resolution helpers need care).

**Files:**
- Create: `tests/SyntheticDataGenerator/SyntheticDataGenerator.csproj`
- Create: `tests/SyntheticDataGenerator/SimulatedEvent.cs`
- Create: `tests/SyntheticDataGenerator/ScenarioConstants.cs`
- Modify: `LogsPlatform.sln` (add the new project)
- Modify: `tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj` (add project reference)

**Interfaces:**
- Consumes: nothing (leaf project).
- Produces (used by every later task): `SimulatedEvent` record; `ScenarioConstants` (named constants for hierarchy names and magnitude numbers, referenced by name everywhere else in this plan instead of repeating magic numbers).

- [ ] **Step 1: Create the project**

```bash
dotnet new classlib -o tests/SyntheticDataGenerator -n LogsPlatform.SyntheticDataGenerator
```

Then edit `tests/SyntheticDataGenerator/SyntheticDataGenerator.csproj` to target net10.0 (matching every other project in this solution) and delete the default `Class1.cs` it scaffolds.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Add the project to the solution and as a test-project reference**

```bash
dotnet sln add tests/SyntheticDataGenerator/LogsPlatform.SyntheticDataGenerator.csproj
dotnet add tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj reference tests/SyntheticDataGenerator/LogsPlatform.SyntheticDataGenerator.csproj
```

- [ ] **Step 3: Create `SimulatedEvent`**

`tests/SyntheticDataGenerator/SimulatedEvent.cs`:

```csharp
namespace LogsPlatform.SyntheticDataGenerator;

public record SimulatedEvent(
    DateTime Timestamp,
    string Severity,
    string? Module,
    string? ScreenService,
    string? Process,
    string? Operation,
    string? CorrelationId,
    double? DurationMs,
    string? CustomerId,
    string Message,
    string? ExceptionType,
    string? StackTrace);
```

- [ ] **Step 4: Create `ScenarioConstants`**

`tests/SyntheticDataGenerator/ScenarioConstants.cs`:

```csharp
namespace LogsPlatform.SyntheticDataGenerator;

public static class ScenarioConstants
{
    // Hierarchy names — must match 06-מודל-אפליקציה.md §4 exactly.
    public const string RetailPulseApp = "RetailPulse";
    public const string OrdersModule = "Orders";
    public const string OrderApiServiceScreenService = "OrderApiService";
    public const string CreateOrderProcess = "CreateOrder";
    public const string ValidateCartOperation = "ValidateCart";
    public const string ReserveStockOperation = "ReserveStock";
    public const string ChargePaymentOperation = "ChargePayment";
    public const string ConfirmOrderOperation = "ConfirmOrder";
    public const string InventoryModule = "Inventory";
    public const string StockServiceScreenService = "StockService";
    public const string StockSyncProcess = "StockSync";
    public const string PullSupplierFeedOperation = "PullSupplierFeed";

    public const string FieldOpsApp = "FieldOps";
    public const string SchedulingModule = "Scheduling";
    public const string SchedulerApiScreenService = "SchedulerApi";
    public const string AssignTechnicianProcess = "AssignTechnician";
    public const string MatchAvailabilityOperation = "MatchAvailability";
    public const string ReportingModule = "Reporting";
    public const string DailyReportScreenService = "DailyReport";
    public const string GenerateReportProcess = "GenerateReport";
    public const string AggregateJobsOperation = "AggregateJobs";

    // Quiet-day traffic curves (events/hour, business hours = 08:00-17:59 inclusive).
    public const int ChargePaymentBusinessHourMean = 50;
    public const int ChargePaymentNightHourMean = 5;
    public const int MatchAvailabilityDurationMeanMs = 200;
    public const int PullSupplierFeedHourlyMean = 15;
    public const int AggregateJobsBusinessHourMean = 20;
    public const int AggregateJobsNightHourMean = 3;

    public const double NoiseRelativeRange = 0.3; // ±30%
    public const int QuietDaysBack = 35;

    // Scenario magnitudes — large margins over SPIKE_THRESHOLD=3 by design (see Global Constraints).
    public const int ErrorSpikeEventCount = 260;          // vs. ChargePayment business-hour mean 50 → z >> 3
    public const int PerformanceDegradationDurationMs = 900; // vs. mean 200ms
    public const int DeploymentAnomalyEventCount = 100;   // vs. AggregateJobs business-hour mean 20

    public const int CustomerAnomalyPeerCount = 14;        // >= MIN_SAMPLES(14) so CustomerAnomaly reaches Confidence=High
    public const int CustomerAnomalyPeerConfirmOrderCount = 10;
    public const int CustomerAnomalyOutlierConfirmOrderCount = 60;

    public static readonly int[] BusinessHours = Enumerable.Range(8, 10).ToArray(); // 08:00-17:59
}
```

- [ ] **Step 5: Verify it builds**

Run: `dotnet build`
Expected: 0 errors, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add tests/SyntheticDataGenerator LogsPlatform.sln tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj
git commit -m "Scaffold SyntheticDataGenerator project"
```

---

### Task 2: Fix `CustomerOutlierDetector`'s Confidence calculation

**Suggested model tier:** standard (a real behavior fix against the source spec, needs a correct test).

**Files:**
- Modify: `src/LogsPlatform.Web/Services/Analysis/CustomerOutlierDetector.cs`
- Modify: `tests/LogsPlatform.Tests/Web/CustomerOutlierDetectorTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `CustomerOutlierDetector`'s Confidence output now follows `08`'s §6 table (`|z|>5 AND totalComparedCustomers>=14` → High; `3<|z|<=5` or (`|z|>5` but `<14` customers) → Medium; this detector never emits Low since `MIN_PEER_CUSTOMERS`=5 already gates whether it runs at all). Used by Task 12's assertions.

- [ ] **Step 1: Write the failing test**

Add to `tests/LogsPlatform.Tests/Web/CustomerOutlierDetectorTests.cs`, inside the class, after `RunAsync_FewerThanMinPeerCustomers_NoFindingCreated`:

```csharp
    [Fact]
    public async Task RunAsync_FourteenOrMoreCustomersCompared_ConfidenceIsHigh()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, opId) = await SeedAppEnvOperationAsync(context, "CustomerOutlierHighConfidenceTestApp");

        var customers = new List<Customer>();
        for (var i = 0; i < 15; i++)
        {
            customers.Add(new Customer { ApplicationId = appId, ExternalCustomerId = $"cust-{i}", Name = $"Customer {i}" });
        }
        context.Customers.AddRange(customers);
        await context.SaveChangesAsync();

        var now = DateTime.UtcNow;
        for (var i = 0; i < 14; i++)
        {
            context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customers[i].Id, Timestamp = now, Severity = 17, Message = $"peer-{i}" });
        }
        for (var i = 0; i < 60; i++)
        {
            context.Events.Add(new Event { ApplicationId = appId, EnvironmentId = envId, OperationId = opId, CustomerId = customers[14].Id, Timestamp = now, Severity = 17, Message = $"outlier-{i}" });
        }
        await context.SaveChangesAsync();

        var metrics = new MetricsRepository(context);
        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var detector = new CustomerOutlierDetector(metrics, writer);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.CustomerAnomaly);

        Assert.NotNull(finding);
        Assert.Equal(ConfidenceLevel.High, finding!.ConfidenceLevel);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter CustomerOutlierDetectorTests`
Expected: FAIL — the new test's `Assert.Equal(ConfidenceLevel.High, ...)` fails since the current code always writes `ConfidenceLevel.Medium`.

- [ ] **Step 3: Fix `CustomerOutlierDetector`**

In `src/LogsPlatform.Web/Services/Analysis/CustomerOutlierDetector.cs`, add a constant and change the confidence computation. Replace the class body:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class CustomerOutlierDetector
{
    private const int MinPeerCustomers = 5;
    private const double CustomerOutlierThreshold = 3;
    private const double MinStdDevFloor = 0.5;
    private const int MinSamplesForHighConfidence = 14;
    private static readonly TimeSpan Window = TimeSpan.FromDays(1);

    private readonly IMetricsRepository _metrics;
    private readonly FindingWriter _writer;

    public CustomerOutlierDetector(IMetricsRepository metrics, FindingWriter writer)
    {
        _metrics = metrics;
        _writer = writer;
    }

    public async Task RunAsync(int applicationId, int environmentId)
    {
        var windowStart = DateTime.UtcNow - Window;

        var operationIds = await _metrics.GetActiveOperationIdsAsync(applicationId, environmentId);
        foreach (var operationId in operationIds)
        {
            var rates = await _metrics.GetCustomerRatesAsync(applicationId, environmentId, operationId, null, windowStart);
            await EvaluatePeersAsync(applicationId, environmentId, AnalysisScopeType.Operation, operationId, rates);
        }

        var exceptionGroupIds = await _metrics.GetActiveExceptionGroupIdsAsync(applicationId, environmentId);
        foreach (var exceptionGroupId in exceptionGroupIds)
        {
            var rates = await _metrics.GetCustomerRatesAsync(applicationId, environmentId, null, exceptionGroupId, windowStart);
            await EvaluatePeersAsync(applicationId, environmentId, AnalysisScopeType.ExceptionGroup, exceptionGroupId, rates);
        }
    }

    private async Task EvaluatePeersAsync(int applicationId, int environmentId, AnalysisScopeType scopeType, long scopeId, IReadOnlyDictionary<int, double> rates)
    {
        if (rates.Count < MinPeerCustomers)
        {
            return;
        }

        foreach (var (customerId, rate) in rates)
        {
            var peerRates = rates.Where(r => r.Key != customerId).Select(r => r.Value).ToList();
            var populationMean = peerRates.Average();
            var variance = peerRates.Count > 1
                ? peerRates.Sum(v => (v - populationMean) * (v - populationMean)) / peerRates.Count
                : 0;
            var populationStdDev = Math.Sqrt(variance);
            var stdDev = Math.Max(populationStdDev, MinStdDevFloor);
            var z = (rate - populationMean) / stdDev;

            if (Math.Abs(z) > CustomerOutlierThreshold)
            {
                var absZ = Math.Abs(z);
                var severity = absZ > 5 ? FindingSeverity.High : FindingSeverity.Medium;
                var confidence = absZ > 5 && rates.Count >= MinSamplesForHighConfidence ? ConfidenceLevel.High
                    : ConfidenceLevel.Medium;

                var fact = $"Customer {customerId} recorded a rate of {rate:F1} in the last 24 hours.";
                var observation = $"That is {absZ:F1} standard deviations from its {peerRates.Count} peers (peer average: {populationMean:F1}±{populationStdDev:F1}).";

                var draft = new FindingDraft(
                    applicationId, environmentId, FindingType.CustomerAnomaly, scopeType, scopeId,
                    $"Customer {customerId}: unusual activity", severity, confidence,
                    new[] { (DetectorStatementKind.Fact, fact), (DetectorStatementKind.Observation, observation) });

                await _writer.WriteAsync(draft);
            }
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter CustomerOutlierDetectorTests`
Expected: PASS — 3/3 tests (2 pre-existing + 1 new). The pre-existing `RunAsync_OneCustomerFarAbovePeers_CreatesCustomerAnomalyFinding` test (6 total customers) still passes since it doesn't assert Confidence, only that a Finding exists.

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Web/Services/Analysis/CustomerOutlierDetector.cs tests/LogsPlatform.Tests/Web/CustomerOutlierDetectorTests.cs
git commit -m "Fix CustomerOutlierDetector to compute Confidence per 08's spec table instead of hardcoding Medium"
```

---

### Task 3: `DomainFixture` — build RetailPulse + FieldOps through the real Admin API

**Suggested model tier:** standard (HTTP orchestration across many nested endpoints — mechanical per call, but the full chain has real ordering dependencies).

**Files:**
- Create: `tests/SyntheticDataGenerator/DomainFixture.cs`
- Test: `tests/LogsPlatform.Tests/Scenario/DomainFixtureTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks besides `ScenarioConstants` (Task 1).
- Produces (used by Tasks 5-12): `AppFixture(int ApplicationId, int EnvironmentId, string ApiKey)`; `DomainFixture.BuildRetailPulseAsync(HttpClient client)` → `Task<AppFixture>`; `DomainFixture.BuildFieldOpsAsync(HttpClient client)` → `Task<AppFixture>`; `DomainFixture.SeedCustomersAsync(HttpClient client, int applicationId, int count)` → `Task<IReadOnlyList<string>>` (returns the created `ExternalCustomerId` strings, `"cust-0"`, `"cust-1"`, ... in order).

- [ ] **Step 1: Write the failing test**

Create `tests/LogsPlatform.Tests/Scenario/DomainFixtureTests.cs`:

```csharp
using System.Net.Http.Json;
using LogsPlatform.SyntheticDataGenerator;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

[Collection("Database")]
public class DomainFixtureTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public DomainFixtureTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task BuildRetailPulseAsync_CreatesApplicationWithApiKeyAndEnvironment()
    {
        var client = _factory.CreateClient();

        var fixture = await DomainFixture.BuildRetailPulseAsync(client);

        Assert.True(fixture.ApplicationId > 0);
        Assert.True(fixture.EnvironmentId > 0);
        Assert.StartsWith("lgp_", fixture.ApiKey);

        var response = await client.GetAsync($"/api/v1/admin/applications/{fixture.ApplicationId}");
        response.EnsureSuccessStatusCode();
        var app = await response.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.Equal("RetailPulse", app!.Name);
    }

    [Fact]
    public async Task BuildFieldOpsAsync_CreatesApplicationWithApiKeyAndEnvironment()
    {
        var client = _factory.CreateClient();

        var fixture = await DomainFixture.BuildFieldOpsAsync(client);

        Assert.True(fixture.ApplicationId > 0);
        Assert.True(fixture.EnvironmentId > 0);
        Assert.StartsWith("lgp_", fixture.ApiKey);
    }

    [Fact]
    public async Task SeedCustomersAsync_CreatesRequestedCount()
    {
        var client = _factory.CreateClient();
        var fixture = await DomainFixture.BuildRetailPulseAsync(client);

        var customerIds = await DomainFixture.SeedCustomersAsync(client, fixture.ApplicationId, 15);

        Assert.Equal(15, customerIds.Count);
        Assert.Equal("cust-0", customerIds[0]);
        Assert.Equal("cust-14", customerIds[14]);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter DomainFixtureTests`
Expected: FAIL — compile error, `DomainFixture`/`AppFixture` don't exist.

- [ ] **Step 3: Implement `DomainFixture`**

Create `tests/SyntheticDataGenerator/DomainFixture.cs`:

```csharp
using System.Net.Http.Json;

namespace LogsPlatform.SyntheticDataGenerator;

public record AppFixture(int ApplicationId, int EnvironmentId, string ApiKey);

public static class DomainFixture
{
    public static async Task<AppFixture> BuildRetailPulseAsync(HttpClient client)
    {
        var appId = await CreateApplicationAsync(client, ScenarioConstants.RetailPulseApp);
        var envId = await CreateEnvironmentAsync(client, appId, "Production");
        var apiKey = await CreateApiKeyAsync(client, appId);

        var ordersModuleId = await CreateModuleAsync(client, appId, ScenarioConstants.OrdersModule);
        var orderApiServiceId = await CreateScreenServiceAsync(client, ordersModuleId, ScenarioConstants.OrderApiServiceScreenService);
        var createOrderProcessId = await CreateProcessAsync(client, orderApiServiceId, ScenarioConstants.CreateOrderProcess);
        await CreateOperationAsync(client, createOrderProcessId, ScenarioConstants.ValidateCartOperation);
        await CreateOperationAsync(client, createOrderProcessId, ScenarioConstants.ReserveStockOperation);
        await CreateOperationAsync(client, createOrderProcessId, ScenarioConstants.ChargePaymentOperation);
        await CreateOperationAsync(client, createOrderProcessId, ScenarioConstants.ConfirmOrderOperation);

        var inventoryModuleId = await CreateModuleAsync(client, appId, ScenarioConstants.InventoryModule);
        var stockServiceId = await CreateScreenServiceAsync(client, inventoryModuleId, ScenarioConstants.StockServiceScreenService);
        var stockSyncProcessId = await CreateProcessAsync(client, stockServiceId, ScenarioConstants.StockSyncProcess);
        await CreateOperationAsync(client, stockSyncProcessId, ScenarioConstants.PullSupplierFeedOperation);

        return new AppFixture(appId, envId, apiKey);
    }

    public static async Task<AppFixture> BuildFieldOpsAsync(HttpClient client)
    {
        var appId = await CreateApplicationAsync(client, ScenarioConstants.FieldOpsApp);
        var envId = await CreateEnvironmentAsync(client, appId, "Production");
        var apiKey = await CreateApiKeyAsync(client, appId);

        var schedulingModuleId = await CreateModuleAsync(client, appId, ScenarioConstants.SchedulingModule);
        var schedulerApiId = await CreateScreenServiceAsync(client, schedulingModuleId, ScenarioConstants.SchedulerApiScreenService);
        var assignTechnicianProcessId = await CreateProcessAsync(client, schedulerApiId, ScenarioConstants.AssignTechnicianProcess);
        await CreateOperationAsync(client, assignTechnicianProcessId, ScenarioConstants.MatchAvailabilityOperation);

        var reportingModuleId = await CreateModuleAsync(client, appId, ScenarioConstants.ReportingModule);
        var dailyReportId = await CreateScreenServiceAsync(client, reportingModuleId, ScenarioConstants.DailyReportScreenService);
        var generateReportProcessId = await CreateProcessAsync(client, dailyReportId, ScenarioConstants.GenerateReportProcess);
        await CreateOperationAsync(client, generateReportProcessId, ScenarioConstants.AggregateJobsOperation);

        return new AppFixture(appId, envId, apiKey);
    }

    public static async Task<IReadOnlyList<string>> SeedCustomersAsync(HttpClient client, int applicationId, int count)
    {
        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var externalId = $"cust-{i}";
            await client.PostAsJsonAsync($"/api/v1/admin/applications/{applicationId}/customers",
                new { ExternalCustomerId = externalId, Name = $"Customer {i}" });
            ids.Add(externalId);
        }
        return ids;
    }

    private static async Task<int> CreateApplicationAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/admin/applications", new { Name = name, Description = (string?)null });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    private static async Task<int> CreateEnvironmentAsync(HttpClient client, int appId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/environments", new { Name = name, IsProduction = true });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    private static async Task<string> CreateApiKeyAsync(HttpClient client, int appId)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/api-keys", new { Label = "SyntheticDataGenerator" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiKeyResponse>();
        return body!.ApiKey;
    }

    private static async Task<int> CreateModuleAsync(HttpClient client, int appId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/modules", new { Name = name, Description = (string?)null });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    private static async Task<int> CreateScreenServiceAsync(HttpClient client, int moduleId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/admin/modules/{moduleId}/screen-services", new { Name = name, Type = "Service", Description = (string?)null });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    private static async Task<int> CreateProcessAsync(HttpClient client, int screenServiceId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/admin/screen-services/{screenServiceId}/processes", new { Name = name, Description = (string?)null });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    private static async Task<int> CreateOperationAsync(HttpClient client, int processId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/admin/processes/{processId}/operations", new { Name = name, Description = (string?)null });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    private record IdResponse(int Id);
    private record ApiKeyResponse(int Id, int ApplicationId, string Label, DateTime CreatedAt, string ApiKey);
}
```

Note: `CreateApplicationAsync`/etc. deserialize only the `Id` field via a private `IdResponse(int Id)` record — every Admin API create response (`ApplicationResponse`, `ModuleResponse`, `ScreenServiceResponse`, `ProcessResponse`, `OperationResponse`, `EnvironmentResponse`) starts with an `Id` property, and `System.Text.Json`'s default deserialization ignores extra fields, so one minimal record works for all of them without `SyntheticDataGenerator` needing a reference to `LogsPlatform.Web.Contracts`.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter DomainFixtureTests`
Expected: PASS — 3/3 tests.

- [ ] **Step 5: Commit**

```bash
git add tests/SyntheticDataGenerator/DomainFixture.cs tests/LogsPlatform.Tests/Scenario/DomainFixtureTests.cs
git commit -m "Add DomainFixture to build RetailPulse/FieldOps through the real Admin API"
```

---

### Task 4: `ScenarioTestWebApplicationFactory` — deterministic control over the Analysis Engine

**Suggested model tier:** standard (small change, but a critical correctness catch — see the note below).

**Files:**
- Create: `tests/LogsPlatform.Tests/Scenario/ScenarioTestWebApplicationFactory.cs`
- Test: `tests/LogsPlatform.Tests/Scenario/ScenarioTestWebApplicationFactoryTests.cs`

**Interfaces:**
- Consumes: `TestWebApplicationFactory`'s established pattern (pre-existing, from M2a).
- Produces (used by Tasks 11-12): `ScenarioTestWebApplicationFactory : WebApplicationFactory<Program>` — same DB setup as `TestWebApplicationFactory`, plus the fix below.

**A real bug this task prevents, caught during this plan's own writing:** every existing `WebApplicationFactory<Program>`-based test (M2a onward) hosts the *real* `Program.cs`, which registers `AnalysisEngineBackgroundService` via `AddHostedService`. That means the real background timer starts running the moment `factory.CreateClient()` triggers host startup — including its own automatic `RunOneTickAsync()` call, on its own schedule, independent of whatever the test explicitly calls. For every other test this has been harmless (no data shaped to trigger a false Finding). For this plan's scenario tests specifically, an uncontrolled automatic tick firing partway through the ~10,000-event generation/ingestion phase could run `BaselineCalculator` against partial data, corrupting the baseline the test's own explicit tick later depends on. `ScenarioTestWebApplicationFactory` removes the hosted service registration so detection only ever runs when the test calls it.

- [ ] **Step 1: Write the failing test**

Create `tests/LogsPlatform.Tests/Scenario/ScenarioTestWebApplicationFactoryTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

[Collection("Database")]
public class ScenarioTestWebApplicationFactoryTests : IClassFixture<ScenarioTestWebApplicationFactory>
{
    private readonly ScenarioTestWebApplicationFactory _factory;

    public ScenarioTestWebApplicationFactoryTests(ScenarioTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void HostedServices_DoesNotIncludeAnalysisEngineBackgroundService()
    {
        _factory.CreateClient(); // triggers host startup

        using var scope = _factory.Services.CreateScope();
        var hostedServices = scope.ServiceProvider.GetServices<IHostedService>();

        Assert.Empty(hostedServices);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter ScenarioTestWebApplicationFactoryTests`
Expected: FAIL — compile error, `ScenarioTestWebApplicationFactory` doesn't exist.

- [ ] **Step 3: Implement `ScenarioTestWebApplicationFactory`**

Create `tests/LogsPlatform.Tests/Scenario/ScenarioTestWebApplicationFactory.cs`:

```csharp
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Tests.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LogsPlatform.Tests.Scenario;

/// <summary>
/// Same real-DB setup as <see cref="TestWebApplicationFactory"/>, but additionally removes
/// AnalysisEngineBackgroundService so detection only ever runs when a test explicitly calls
/// AnalysisEngineTickRunner.RunOneTickAsync() — the scenario tests need full manual control
/// over exactly when the Analysis Engine runs, not the real 5-minute timer racing their own
/// multi-thousand-event generation phase.
/// </summary>
public class ScenarioTestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<LogsPlatformDbContext>>();
            services.AddDbContext<LogsPlatformDbContext>(options => options.UseSqlServer(TestDatabase.ConnectionString));
            services.RemoveAll<IHostedService>();

            using var scope = services.BuildServiceProvider().CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<LogsPlatformDbContext>();
            context.Database.EnsureDeleted();
            context.Database.Migrate();
        });
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter ScenarioTestWebApplicationFactoryTests`
Expected: PASS — 1/1 test.

- [ ] **Step 5: Commit**

```bash
git add tests/LogsPlatform.Tests/Scenario/ScenarioTestWebApplicationFactory.cs tests/LogsPlatform.Tests/Scenario/ScenarioTestWebApplicationFactoryTests.cs
git commit -m "Add ScenarioTestWebApplicationFactory (disables the auto-ticking BackgroundService for deterministic scenario tests)"
```

---

### Task 5: `QuietDayGenerator` — hour-of-day traffic curves with seeded noise

**Suggested model tier:** standard-to-high (the noise model is real judgment: it must be non-constant, reproducible per seed, and bounded predictably enough to reason about the scenario magnitudes in Tasks 6-10).

**Files:**
- Create: `tests/SyntheticDataGenerator/QuietDayGenerator.cs`
- Test: `tests/LogsPlatform.Tests/Scenario/QuietDayGeneratorTests.cs`

**Interfaces:**
- Consumes: `SimulatedEvent`, `ScenarioConstants` (Task 1).
- Produces (used by Tasks 11-12): `QuietDayGenerator.GenerateHourlyEventCounts(Func<int,double> hourlyMean, int daysBack, bool includeToday, Random random)` → `IReadOnlyList<(DateTime HourStart, int Count)>`; `QuietDayGenerator.ToEvents(IReadOnlyList<(DateTime HourStart, int Count)> hourlyCounts, string severity, string message, Func<DateTime,double?>? durationMs = null)` → `IReadOnlyList<SimulatedEvent>` (spreads each hour's count evenly across the hour's 60 minutes).

- [ ] **Step 1: Write the failing tests**

Create `tests/LogsPlatform.Tests/Scenario/QuietDayGeneratorTests.cs`:

```csharp
using LogsPlatform.SyntheticDataGenerator;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

public class QuietDayGeneratorTests
{
    [Fact]
    public void GenerateHourlyEventCounts_ExcludesToday_WhenIncludeTodayFalse()
    {
        var random = new Random(42);
        var counts = QuietDayGenerator.GenerateHourlyEventCounts(hour => 50, daysBack: 5, includeToday: false, random);

        var today = DateTime.UtcNow.Date;
        Assert.DoesNotContain(counts, c => c.HourStart.Date == today);
        Assert.Equal(5 * 24, counts.Count);
    }

    [Fact]
    public void GenerateHourlyEventCounts_IncludesToday_WhenIncludeTodayTrue()
    {
        var random = new Random(42);
        var counts = QuietDayGenerator.GenerateHourlyEventCounts(hour => 50, daysBack: 5, includeToday: true, random);

        var currentHour = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        Assert.Contains(counts, c => c.HourStart == currentHour);
    }

    [Fact]
    public void GenerateHourlyEventCounts_CountsVaryAroundMean_NotConstant()
    {
        var random = new Random(42);
        var counts = QuietDayGenerator.GenerateHourlyEventCounts(hour => 50, daysBack: 10, includeToday: false, random);

        var distinctValues = counts.Select(c => c.Count).Distinct().Count();
        Assert.True(distinctValues > 1, "Noise model produced a constant value across all hours.");

        var average = counts.Average(c => c.Count);
        Assert.InRange(average, 35, 65); // mean 50 ± the ±30% noise range, generously bounded
    }

    [Fact]
    public void ToEvents_SpreadsCountAcrossTheHour()
    {
        var hourStart = DateTime.UtcNow.Date;
        var counts = new List<(DateTime HourStart, int Count)> { (hourStart, 10) };

        var events = QuietDayGenerator.ToEvents(counts, "Info", "quiet traffic");

        Assert.Equal(10, events.Count);
        Assert.All(events, e => Assert.InRange(e.Timestamp, hourStart, hourStart.AddHours(1)));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter QuietDayGeneratorTests`
Expected: FAIL — compile error, `QuietDayGenerator` doesn't exist.

- [ ] **Step 3: Implement `QuietDayGenerator`**

Create `tests/SyntheticDataGenerator/QuietDayGenerator.cs`:

```csharp
namespace LogsPlatform.SyntheticDataGenerator;

public static class QuietDayGenerator
{
    /// <summary>
    /// Produces one (hour bucket start, event count) pair per hour, for `daysBack` historical days
    /// (day-offset 1..daysBack relative to "today") and optionally today's hours up to the current
    /// one (inclusive) when includeToday is true. Each count is drawn from hourlyMean(hourOfDay) with
    /// ±ScenarioConstants.NoiseRelativeRange relative jitter — never a fixed value, so BaselineCalculator
    /// sees real variance, but bounded predictably enough to reason about scenario magnitudes.
    /// </summary>
    public static IReadOnlyList<(DateTime HourStart, int Count)> GenerateHourlyEventCounts(
        Func<int, double> hourlyMean, int daysBack, bool includeToday, Random random)
    {
        var results = new List<(DateTime HourStart, int Count)>();
        var today = DateTime.UtcNow.Date;
        var currentHour = DateTime.UtcNow.Hour;

        for (var dayOffset = daysBack; dayOffset >= 1; dayOffset--)
        {
            var day = today.AddDays(-dayOffset);
            for (var hour = 0; hour < 24; hour++)
            {
                results.Add((day.AddHours(hour), SampleCount(hourlyMean(hour), random)));
            }
        }

        if (includeToday)
        {
            for (var hour = 0; hour <= currentHour; hour++)
            {
                results.Add((today.AddHours(hour), SampleCount(hourlyMean(hour), random)));
            }
        }

        return results;
    }

    public static IReadOnlyList<SimulatedEvent> ToEvents(
        IReadOnlyList<(DateTime HourStart, int Count)> hourlyCounts, string severity, string message,
        Func<DateTime, double?>? durationMs = null,
        string? module = null, string? screenService = null, string? process = null, string? operation = null,
        string? customerId = null)
    {
        var events = new List<SimulatedEvent>();
        foreach (var (hourStart, count) in hourlyCounts)
        {
            if (count == 0) continue;
            var spacingMinutes = 60.0 / count;
            for (var i = 0; i < count; i++)
            {
                var timestamp = hourStart.AddMinutes(i * spacingMinutes);
                events.Add(new SimulatedEvent(
                    timestamp, severity, module, screenService, process, operation,
                    CorrelationId: null, DurationMs: durationMs?.Invoke(timestamp), CustomerId: customerId,
                    Message: message, ExceptionType: null, StackTrace: null));
            }
        }
        return events;
    }

    private static int SampleCount(double mean, Random random)
    {
        var jitter = 1.0 + (random.NextDouble() * 2 - 1) * ScenarioConstants.NoiseRelativeRange; // [1-range, 1+range]
        var value = (int)Math.Round(mean * jitter);
        return Math.Max(0, value);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter QuietDayGeneratorTests`
Expected: PASS — 4/4 tests.

- [ ] **Step 5: Commit**

```bash
git add tests/SyntheticDataGenerator/QuietDayGenerator.cs tests/LogsPlatform.Tests/Scenario/QuietDayGeneratorTests.cs
git commit -m "Add QuietDayGenerator (hour-of-day traffic curves with seeded, non-constant noise)"
```

---

### Task 6: `ErrorSpikeInjector` + `PerformanceDegradationInjector`

**Suggested model tier:** standard-to-high (the two rate-based scenarios; magnitude reasoning is the real judgment here).

**Files:**
- Create: `tests/SyntheticDataGenerator/ScenarioInjectors/ErrorSpikeInjector.cs`
- Create: `tests/SyntheticDataGenerator/ScenarioInjectors/PerformanceDegradationInjector.cs`
- Test: `tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs`

**Interfaces:**
- Consumes: `SimulatedEvent`, `ScenarioConstants` (Task 1).
- Produces (used by Task 12): `ErrorSpikeInjector.Inject()` → `IReadOnlyList<SimulatedEvent>` (RetailPulse/ChargePayment, current-hour event count = `ScenarioConstants.ErrorSpikeEventCount`, `Severity="Error"`); `PerformanceDegradationInjector.Inject(int eventCount)` → `IReadOnlyList<SimulatedEvent>` (FieldOps/MatchAvailability, `eventCount` events in the current hour, each with `DurationMs=ScenarioConstants.PerformanceDegradationDurationMs`).

- [ ] **Step 1: Write the failing tests**

Create `tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs`:

```csharp
using LogsPlatform.SyntheticDataGenerator;
using LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

public class ScenarioInjectorTests
{
    [Fact]
    public void ErrorSpikeInjector_ProducesEventsInCurrentHourOnChargePayment()
    {
        var events = ErrorSpikeInjector.Inject();

        Assert.Equal(ScenarioConstants.ErrorSpikeEventCount, events.Count);
        Assert.All(events, e => Assert.Equal(ScenarioConstants.ChargePaymentOperation, e.Operation));
        Assert.All(events, e => Assert.Equal("Error", e.Severity));
        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        Assert.All(events, e => Assert.InRange(e.Timestamp, currentHourStart, currentHourStart.AddHours(1)));
    }

    [Fact]
    public void PerformanceDegradationInjector_ProducesElevatedDurationOnMatchAvailability()
    {
        var events = PerformanceDegradationInjector.Inject(eventCount: 20);

        Assert.Equal(20, events.Count);
        Assert.All(events, e => Assert.Equal(ScenarioConstants.MatchAvailabilityOperation, e.Operation));
        Assert.All(events, e => Assert.Equal(ScenarioConstants.PerformanceDegradationDurationMs, e.DurationMs));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter ScenarioInjectorTests`
Expected: FAIL — compile error, `ErrorSpikeInjector`/`PerformanceDegradationInjector` don't exist.

- [ ] **Step 3: Implement both injectors**

Create `tests/SyntheticDataGenerator/ScenarioInjectors/ErrorSpikeInjector.cs`:

```csharp
namespace LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;

/// <summary>
/// RetailPulse/ChargePayment starts failing at a rate far above baseline for the current hour.
/// ChargePaymentBusinessHourMean=50, so 260 events (ErrorSpikeEventCount) is ~5x even the
/// business-hour peak — reliably crosses SPIKE_THRESHOLD=3 regardless of the realized noise/stddev.
/// </summary>
public static class ErrorSpikeInjector
{
    public static IReadOnlyList<SimulatedEvent> Inject()
    {
        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        var hourlyCounts = new List<(DateTime HourStart, int Count)> { (currentHourStart, ScenarioConstants.ErrorSpikeEventCount) };

        return QuietDayGenerator.ToEvents(hourlyCounts, "Error", "Card authorization failed",
            module: ScenarioConstants.OrdersModule, screenService: ScenarioConstants.OrderApiServiceScreenService,
            process: ScenarioConstants.CreateOrderProcess, operation: ScenarioConstants.ChargePaymentOperation);
    }
}
```

Create `tests/SyntheticDataGenerator/ScenarioInjectors/PerformanceDegradationInjector.cs`:

```csharp
namespace LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;

/// <summary>
/// FieldOps/MatchAvailability's per-event duration climbs to 900ms (PerformanceDegradationDurationMs)
/// vs. a 200ms quiet-day mean — event COUNT for the hour stays at a normal level (eventCount is passed
/// in matching the caller's own quiet-hour curve) so only the DurationMs baseline is disturbed, never
/// the EventCount one (which would otherwise also fire an unrelated ErrorSpike/MissingActivity Finding
/// on the same Operation).
/// </summary>
public static class PerformanceDegradationInjector
{
    public static IReadOnlyList<SimulatedEvent> Inject(int eventCount)
    {
        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        var hourlyCounts = new List<(DateTime HourStart, int Count)> { (currentHourStart, eventCount) };

        return QuietDayGenerator.ToEvents(hourlyCounts, "Info", "Technician availability matched",
            durationMs: _ => ScenarioConstants.PerformanceDegradationDurationMs,
            module: ScenarioConstants.SchedulingModule, screenService: ScenarioConstants.SchedulerApiScreenService,
            process: ScenarioConstants.AssignTechnicianProcess, operation: ScenarioConstants.MatchAvailabilityOperation);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter ScenarioInjectorTests`
Expected: PASS — 2/2 tests.

- [ ] **Step 5: Commit**

```bash
git add tests/SyntheticDataGenerator/ScenarioInjectors/ErrorSpikeInjector.cs tests/SyntheticDataGenerator/ScenarioInjectors/PerformanceDegradationInjector.cs tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs
git commit -m "Add ErrorSpikeInjector and PerformanceDegradationInjector"
```

---

### Task 7: `NewExceptionInjector` (+ downstream-correlated event)

**Suggested model tier:** standard-to-high (needs to satisfy both `NewExceptionDetector`'s 5-minute window and `DownstreamFailureCorrelator`'s correlation rules simultaneously).

**Files:**
- Create: `tests/SyntheticDataGenerator/ScenarioInjectors/NewExceptionInjector.cs`
- Test: `tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs` (append)

**Interfaces:**
- Consumes: `SimulatedEvent`, `ScenarioConstants` (Task 1).
- Produces (used by Task 12): `NewExceptionInjector.Inject()` → `IReadOnlyList<SimulatedEvent>` (exactly 2 events: the new-exception trigger on `ReserveStock`, and a downstream Error-severity event on `ValidateCart` 5 seconds later, sharing a `CorrelationId`).

- [ ] **Step 1: Write the failing test**

Append to `tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs`, inside the class:

```csharp
    [Fact]
    public void NewExceptionInjector_ProducesTriggerAndDownstreamEventsSharingCorrelationId()
    {
        var events = NewExceptionInjector.Inject();

        Assert.Equal(2, events.Count);

        var trigger = events.Single(e => e.Operation == ScenarioConstants.ReserveStockOperation);
        Assert.NotNull(trigger.ExceptionType);
        Assert.NotNull(trigger.CorrelationId);

        var downstream = events.Single(e => e.Operation == ScenarioConstants.ValidateCartOperation);
        Assert.Equal(trigger.CorrelationId, downstream.CorrelationId);
        Assert.Equal("Error", downstream.Severity);
        Assert.True(downstream.Timestamp > trigger.Timestamp);

        var fiveMinutesAgo = DateTime.UtcNow.AddMinutes(-5);
        Assert.True(trigger.Timestamp >= fiveMinutesAgo, "Trigger event must fall inside NewExceptionDetector's 5-minute window.");
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter ScenarioInjectorTests`
Expected: FAIL — compile error, `NewExceptionInjector` doesn't exist.

- [ ] **Step 3: Implement `NewExceptionInjector`**

Create `tests/SyntheticDataGenerator/ScenarioInjectors/NewExceptionInjector.cs`:

```csharp
namespace LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;

/// <summary>
/// RetailPulse/ReserveStock throws an exception type never seen in this test's history (nothing else
/// in this plan uses "StockUnavailableException"), triggering NewExceptionDetector. A second event,
/// 5 seconds later on a DIFFERENT Operation (ValidateCart) sharing the same CorrelationId with
/// Severity=Error, satisfies DownstreamFailureCorrelator's exact matching rule (Timestamp after
/// trigger, different OperationId, Severity >= ERROR_SEVERITY_FLOOR=17), so the resulting Finding
/// also carries a Downstream-Failure Hypothesis + Evidence, per 11-Test-Strategy.md §3's criterion.
/// Neither Operation ever appears in quiet-day generation, so no historical Baseline exists for
/// either — RateAnomalyDetector silently skips both (see Global Constraints), avoiding any
/// confounding rate-based Finding.
/// </summary>
public static class NewExceptionInjector
{
    public static IReadOnlyList<SimulatedEvent> Inject()
    {
        var correlationId = $"order-{Guid.NewGuid():N}";
        var triggerTime = DateTime.UtcNow.AddSeconds(-30);

        var trigger = new SimulatedEvent(
            triggerTime, "Error", ScenarioConstants.OrdersModule, ScenarioConstants.OrderApiServiceScreenService,
            ScenarioConstants.CreateOrderProcess, ScenarioConstants.ReserveStockOperation,
            correlationId, DurationMs: null, CustomerId: null,
            Message: "Stock reservation failed unexpectedly",
            ExceptionType: "StockUnavailableException", StackTrace: "at StockService.Reserve() line 42");

        var downstream = new SimulatedEvent(
            triggerTime.AddSeconds(5), "Error", ScenarioConstants.OrdersModule, ScenarioConstants.OrderApiServiceScreenService,
            ScenarioConstants.CreateOrderProcess, ScenarioConstants.ValidateCartOperation,
            correlationId, DurationMs: null, CustomerId: null,
            Message: "Cart validation failed after stock reservation error",
            ExceptionType: null, StackTrace: null);

        return new[] { trigger, downstream };
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter ScenarioInjectorTests`
Expected: PASS — 3/3 tests.

- [ ] **Step 5: Commit**

```bash
git add tests/SyntheticDataGenerator/ScenarioInjectors/NewExceptionInjector.cs tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs
git commit -m "Add NewExceptionInjector with a downstream-correlated event"
```

---

### Task 8: `DeploymentAnomalyInjector`

**Suggested model tier:** standard-to-high (combines a real Deployment via Admin API with a rate-based spike).

**Files:**
- Create: `tests/SyntheticDataGenerator/ScenarioInjectors/DeploymentAnomalyInjector.cs`
- Test: `tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs` (append)

**Interfaces:**
- Consumes: `SimulatedEvent`, `ScenarioConstants`, `AppFixture` (Task 3).
- Produces (used by Task 12): `DeploymentAnomalyInjector.CreateDeploymentAsync(HttpClient client, AppFixture fieldOps)` → `Task` (creates a new `AppVersion` + `Deployment` in FieldOps/Production, `DeployedAt` = now minus 20 minutes — inside `DeploymentCorrelator`'s 60-minute window); `DeploymentAnomalyInjector.InjectEvents()` → `IReadOnlyList<SimulatedEvent>` (FieldOps/AggregateJobs, current-hour event count = `ScenarioConstants.DeploymentAnomalyEventCount`, `Severity="Error"`).

- [ ] **Step 1: Write the failing test**

Append to `tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs`, inside the class:

```csharp
    [Fact]
    public void DeploymentAnomalyInjector_InjectEvents_ProducesEventsInCurrentHourOnAggregateJobs()
    {
        var events = DeploymentAnomalyInjector.InjectEvents();

        Assert.Equal(ScenarioConstants.DeploymentAnomalyEventCount, events.Count);
        Assert.All(events, e => Assert.Equal(ScenarioConstants.AggregateJobsOperation, e.Operation));
        Assert.All(events, e => Assert.Equal("Error", e.Severity));
    }
```

Also add an integration test using `ScenarioTestWebApplicationFactory` (from Task 4) and `DomainFixture` (from Task 3) to `tests/LogsPlatform.Tests/Scenario/DeploymentAnomalyInjectorTests.cs`:

```csharp
using LogsPlatform.SyntheticDataGenerator;
using LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;
using LogsPlatform.Web.Contracts;
using System.Net.Http.Json;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

[Collection("Database")]
public class DeploymentAnomalyInjectorTests : IClassFixture<ScenarioTestWebApplicationFactory>
{
    private readonly ScenarioTestWebApplicationFactory _factory;

    public DeploymentAnomalyInjectorTests(ScenarioTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateDeploymentAsync_CreatesDeploymentWithinCorrelationWindow()
    {
        var client = _factory.CreateClient();
        var fieldOps = await DomainFixture.BuildFieldOpsAsync(client);

        await DeploymentAnomalyInjector.CreateDeploymentAsync(client, fieldOps);

        var response = await client.GetAsync($"/api/v1/admin/applications/{fieldOps.ApplicationId}/deployments");
        response.EnsureSuccessStatusCode();
        var deployments = await response.Content.ReadFromJsonAsync<List<DeploymentResponse>>();

        Assert.Single(deployments!);
        var deployedAt = deployments![0].DeployedAt;
        Assert.True(deployedAt > DateTime.UtcNow.AddMinutes(-60) && deployedAt < DateTime.UtcNow);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter "ScenarioInjectorTests|DeploymentAnomalyInjectorTests"`
Expected: FAIL — compile error, `DeploymentAnomalyInjector` doesn't exist.

- [ ] **Step 3: Implement `DeploymentAnomalyInjector`**

Create `tests/SyntheticDataGenerator/ScenarioInjectors/DeploymentAnomalyInjector.cs`:

```csharp
using System.Net.Http.Json;

namespace LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;

/// <summary>
/// A new FieldOps version is deployed to Production 20 minutes ago (inside DeploymentCorrelator's
/// 60-minute DEPLOYMENT_CORRELATION_WINDOW), then AggregateJobs's event count spikes far above its
/// quiet-day mean (AggregateJobsBusinessHourMean=20) in the current hour — DeploymentCorrelator picks
/// up the Deployment automatically once RateAnomalyDetector's ErrorSpike Finding is written, since it
/// runs generically over every new Finding, not scenario-specific logic.
/// </summary>
public static class DeploymentAnomalyInjector
{
    public static async Task CreateDeploymentAsync(HttpClient client, AppFixture fieldOps)
    {
        var versionResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{fieldOps.ApplicationId}/versions",
            new { VersionNumber = "2.0.0", ReleaseNotes = "Synthetic scenario deployment" });
        versionResponse.EnsureSuccessStatusCode();
        var version = await versionResponse.Content.ReadFromJsonAsync<IdResponse>();

        var deployedAt = DateTime.UtcNow.AddMinutes(-20);
        var deploymentResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/applications/{fieldOps.ApplicationId}/deployments",
            new { EnvironmentId = fieldOps.EnvironmentId, VersionId = version!.Id, DeployedAt = deployedAt, Notes = (string?)null });
        deploymentResponse.EnsureSuccessStatusCode();
    }

    public static IReadOnlyList<SimulatedEvent> InjectEvents()
    {
        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        var hourlyCounts = new List<(DateTime HourStart, int Count)> { (currentHourStart, ScenarioConstants.DeploymentAnomalyEventCount) };

        return QuietDayGenerator.ToEvents(hourlyCounts, "Error", "Job aggregation failed",
            module: ScenarioConstants.ReportingModule, screenService: ScenarioConstants.DailyReportScreenService,
            process: ScenarioConstants.GenerateReportProcess, operation: ScenarioConstants.AggregateJobsOperation);
    }

    private record IdResponse(int Id);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter "ScenarioInjectorTests|DeploymentAnomalyInjectorTests"`
Expected: PASS — 5/5 tests total (`ScenarioInjectorTests` now has 4: `ErrorSpikeInjector`/`PerformanceDegradationInjector` from Task 6, `NewExceptionInjector` from Task 7, and this task's `DeploymentAnomalyInjector_InjectEvents_...` test; `DeploymentAnomalyInjectorTests` has 1: `CreateDeploymentAsync_...`).

- [ ] **Step 5: Commit**

```bash
git add tests/SyntheticDataGenerator/ScenarioInjectors/DeploymentAnomalyInjector.cs tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs tests/LogsPlatform.Tests/Scenario/DeploymentAnomalyInjectorTests.cs
git commit -m "Add DeploymentAnomalyInjector"
```

---

### Task 9: `MissingActivityInjector`

**Suggested model tier:** standard.

**Files:**
- Create: `tests/SyntheticDataGenerator/ScenarioInjectors/MissingActivityInjector.cs`
- Test: `tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs` (append)

**Interfaces:**
- Consumes: `SimulatedEvent`, `ScenarioConstants` (Task 1).
- Produces (used by Task 12): `MissingActivityInjector.Inject()` → `IReadOnlyList<SimulatedEvent>` (always an **empty list** — the scenario *is* the absence of the current hour's usual `PullSupplierFeed` traffic; documented so the caller doesn't mistake "no events" for a missing implementation).

- [ ] **Step 1: Write the failing test**

Append to `tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs`, inside the class:

```csharp
    [Fact]
    public void MissingActivityInjector_Inject_ProducesNoEvents()
    {
        var events = MissingActivityInjector.Inject();

        Assert.Empty(events);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter ScenarioInjectorTests`
Expected: FAIL — compile error, `MissingActivityInjector` doesn't exist.

- [ ] **Step 3: Implement `MissingActivityInjector`**

Create `tests/SyntheticDataGenerator/ScenarioInjectors/MissingActivityInjector.cs`:

```csharp
namespace LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;

/// <summary>
/// RetailPulse/PullSupplierFeed's normally-hourly activity (PullSupplierFeedHourlyMean=15, well above
/// MIN_MEANINGFUL_ACTIVITY=5 so the "drop" isn't dismissed as noise) goes silent for the current hour.
/// The scenario IS the absence of data — this deliberately returns an empty list rather than "no
/// implementation needed," so the caller (Task 12) doesn't need a special case: it simply doesn't add
/// anything to the ingestion batch for this Operation's current hour, while 35 days of quiet-day
/// history already established a real Baseline with mean well above 5.
/// </summary>
public static class MissingActivityInjector
{
    public static IReadOnlyList<SimulatedEvent> Inject() => Array.Empty<SimulatedEvent>();
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter ScenarioInjectorTests`
Expected: PASS — all tests in the file pass (verify current count against prior tasks' totals).

- [ ] **Step 5: Commit**

```bash
git add tests/SyntheticDataGenerator/ScenarioInjectors/MissingActivityInjector.cs tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs
git commit -m "Add MissingActivityInjector"
```

---

### Task 10: `CustomerAnomalyInjector`

**Suggested model tier:** standard-to-high (must avoid confounding `ConfirmOrder`'s own operation-level rate — see the note below).

**Files:**
- Create: `tests/SyntheticDataGenerator/ScenarioInjectors/CustomerAnomalyInjector.cs`
- Test: `tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs` (append)

**Interfaces:**
- Consumes: `SimulatedEvent`, `ScenarioConstants` (Task 1), the customer id list from `DomainFixture.SeedCustomersAsync` (Task 3).
- Produces (used by Task 12): `CustomerAnomalyInjector.Inject(IReadOnlyList<string> customerIds)` → `IReadOnlyList<SimulatedEvent>` (requires `customerIds.Count >= ScenarioConstants.CustomerAnomalyPeerCount + 1`; the first `CustomerAnomalyPeerCount` ids get `CustomerAnomalyPeerConfirmOrderCount` events each, spread across the last 24 hours; the last id gets `CustomerAnomalyOutlierConfirmOrderCount` events, also spread across 24 hours — not concentrated in one hour).

**A real confound this task's design avoids:** `CustomerOutlierDetector` uses a 24-hour window and is independent of `Baseline`, but `RateAnomalyDetector` also runs against `ConfirmOrder` if it ever gets a `Baseline` row. Since this Operation never appears in quiet-day generation (Task 5's curves only cover `ChargePayment`, `MatchAvailability`, `PullSupplierFeed`, `AggregateJobs`), `ConfirmOrder` has zero historical (day-offset ≥ 1) events, so `BaselineCalculator` never creates a `Baseline` row for it and `RateAnomalyDetector` silently skips it (`if (baseline is null) return;` — see Global Constraints). Spreading the outlier's events across all 24 hours (rather than dumping them into the current hour) is an extra, independent safeguard even if that reasoning ever changes.

- [ ] **Step 1: Write the failing test**

Append to `tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs`, inside the class:

```csharp
    [Fact]
    public void CustomerAnomalyInjector_Inject_GivesOneCustomerFarMoreEventsThanPeers()
    {
        var customerIds = Enumerable.Range(0, ScenarioConstants.CustomerAnomalyPeerCount + 1).Select(i => $"cust-{i}").ToList();

        var events = CustomerAnomalyInjector.Inject(customerIds);

        var byCustomer = events.GroupBy(e => e.CustomerId).ToDictionary(g => g.Key!, g => g.Count());
        Assert.Equal(ScenarioConstants.CustomerAnomalyPeerCount + 1, byCustomer.Count);

        var outlierId = customerIds[^1];
        Assert.Equal(ScenarioConstants.CustomerAnomalyOutlierConfirmOrderCount, byCustomer[outlierId]);

        foreach (var peerId in customerIds.Take(ScenarioConstants.CustomerAnomalyPeerCount))
        {
            Assert.Equal(ScenarioConstants.CustomerAnomalyPeerConfirmOrderCount, byCustomer[peerId]);
        }

        Assert.All(events, e => Assert.Equal(ScenarioConstants.ConfirmOrderOperation, e.Operation));

        var oneDayAgo = DateTime.UtcNow.AddHours(-24);
        var outlierEvents = events.Where(e => e.CustomerId == outlierId).ToList();
        Assert.True(outlierEvents.Select(e => e.Timestamp.Hour).Distinct().Count() > 1,
            "Outlier's events must be spread across multiple hours, not concentrated in one.");
        Assert.All(outlierEvents, e => Assert.InRange(e.Timestamp, oneDayAgo, DateTime.UtcNow));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter ScenarioInjectorTests`
Expected: FAIL — compile error, `CustomerAnomalyInjector` doesn't exist.

- [ ] **Step 3: Implement `CustomerAnomalyInjector`**

Create `tests/SyntheticDataGenerator/ScenarioInjectors/CustomerAnomalyInjector.cs`:

```csharp
namespace LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;

public static class CustomerAnomalyInjector
{
    public static IReadOnlyList<SimulatedEvent> Inject(IReadOnlyList<string> customerIds)
    {
        if (customerIds.Count < ScenarioConstants.CustomerAnomalyPeerCount + 1)
        {
            throw new ArgumentException($"Need at least {ScenarioConstants.CustomerAnomalyPeerCount + 1} customer ids.", nameof(customerIds));
        }

        var events = new List<SimulatedEvent>();
        var windowStart = DateTime.UtcNow.AddHours(-24);

        for (var i = 0; i < ScenarioConstants.CustomerAnomalyPeerCount; i++)
        {
            events.AddRange(SpreadAcrossWindow(customerIds[i], ScenarioConstants.CustomerAnomalyPeerConfirmOrderCount, windowStart));
        }

        var outlierId = customerIds[ScenarioConstants.CustomerAnomalyPeerCount];
        events.AddRange(SpreadAcrossWindow(outlierId, ScenarioConstants.CustomerAnomalyOutlierConfirmOrderCount, windowStart));

        return events;
    }

    private static IEnumerable<SimulatedEvent> SpreadAcrossWindow(string customerId, int count, DateTime windowStart)
    {
        var spacingMinutes = (24.0 * 60) / count;
        for (var i = 0; i < count; i++)
        {
            yield return new SimulatedEvent(
                windowStart.AddMinutes(i * spacingMinutes), "Info",
                ScenarioConstants.OrdersModule, ScenarioConstants.OrderApiServiceScreenService,
                ScenarioConstants.CreateOrderProcess, ScenarioConstants.ConfirmOrderOperation,
                CorrelationId: null, DurationMs: null, CustomerId: customerId,
                Message: "Order confirmed", ExceptionType: null, StackTrace: null);
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter ScenarioInjectorTests`
Expected: PASS — all tests in the file pass.

- [ ] **Step 5: Commit**

```bash
git add tests/SyntheticDataGenerator/ScenarioInjectors/CustomerAnomalyInjector.cs tests/LogsPlatform.Tests/Scenario/ScenarioInjectorTests.cs
git commit -m "Add CustomerAnomalyInjector"
```

---

### Task 11: `FalsePositiveTests` — 3 seeds, quiet-only, assert 0 Findings

**Suggested model tier:** high (the first full integration of every prior task; the batched-ingestion + real-tick pattern is used here for the first time).

**Files:**
- Create: `tests/LogsPlatform.Tests/Scenario/IngestionSender.cs`
- Create: `tests/LogsPlatform.Tests/Scenario/FalsePositiveTests.cs`

**Interfaces:**
- Consumes: `DomainFixture` (Task 3), `ScenarioTestWebApplicationFactory` (Task 4), `QuietDayGenerator` (Task 5), `SimulatedEvent`/`ScenarioConstants` (Task 1).
- Produces (used by Task 12): `IngestionSender.SendBatchedAsync(HttpClient client, string apiKey, IReadOnlyList<SimulatedEvent> events, int batchSize = 500)` → `Task` (converts `SimulatedEvent` → `IngestEventRequest` and POSTs in batches, asserting every batch returns `Accepted == events.Count` for that batch and `Rejected == 0`).

- [ ] **Step 1: Implement `IngestionSender`**

This is shared infrastructure both `FalsePositiveTests` and `ScenarioAcceptanceTests` (Task 12) need — no separate failing-test step, since its correctness is proven by every test that uses it in this and the next task.

Create `tests/LogsPlatform.Tests/Scenario/IngestionSender.cs`:

```csharp
using System.Net.Http.Json;
using LogsPlatform.SyntheticDataGenerator;
using LogsPlatform.Web.Contracts;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

public static class IngestionSender
{
    public static async Task SendBatchedAsync(HttpClient client, string apiKey, IReadOnlyList<SimulatedEvent> events, int batchSize = 500)
    {
        foreach (var batch in Chunk(events, batchSize))
        {
            var requests = batch.Select(ToRequest).ToList();
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/events") { Content = JsonContent.Create(requests) };
            httpRequest.Headers.Add("X-Api-Key", apiKey);

            var response = await client.SendAsync(httpRequest);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<IngestResponse>();

            Assert.Equal(batch.Count, body!.Accepted);
            Assert.Equal(0, body.Rejected);
        }
    }

    private static IngestEventRequest ToRequest(SimulatedEvent evt)
    {
        IngestHierarchyRequest? hierarchy = evt.Operation is null ? null
            : new IngestHierarchyRequest(evt.Module, evt.ScreenService, evt.Process, evt.Operation);

        IngestExceptionRequest? exception = evt.ExceptionType is null ? null
            : new IngestExceptionRequest(evt.ExceptionType, evt.StackTrace);

        return new IngestEventRequest(
            EventKey: null, Timestamp: evt.Timestamp, Severity: evt.Severity, Environment: "Production",
            Version: null, Hierarchy: hierarchy, CorrelationId: evt.CorrelationId, TraceId: null, SpanId: null,
            ParentSpanId: null, DurationMs: evt.DurationMs, CustomerId: evt.CustomerId, UserId: null,
            Message: evt.Message, MessageTemplate: null, Exception: exception, Metadata: null);
    }

    private static IEnumerable<List<SimulatedEvent>> Chunk(IReadOnlyList<SimulatedEvent> events, int size)
    {
        for (var i = 0; i < events.Count; i += size)
        {
            yield return events.Skip(i).Take(size).ToList();
        }
    }
}
```

- [ ] **Step 2: Write `FalsePositiveTests`**

Create `tests/LogsPlatform.Tests/Scenario/FalsePositiveTests.cs`:

```csharp
using LogsPlatform.Domain.Repositories;
using LogsPlatform.SyntheticDataGenerator;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

[Collection("Database")]
public class FalsePositiveTests : IClassFixture<ScenarioTestWebApplicationFactory>
{
    private readonly ScenarioTestWebApplicationFactory _factory;

    public FalsePositiveTests(ScenarioTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(2002)]
    [InlineData(3003)]
    public async Task QuietHistoryOnly_ProducesZeroFindings(int seed)
    {
        var client = _factory.CreateClient();
        var random = new Random(seed);

        var retailPulse = await DomainFixture.BuildRetailPulseAsync(client);
        var fieldOps = await DomainFixture.BuildFieldOpsAsync(client);

        await IngestQuietTrafficAsync(client, retailPulse, fieldOps, random);

        using var scope = _factory.Services.CreateScope();
        var tickRunner = scope.ServiceProvider.GetRequiredService<AnalysisEngineTickRunner>();
        await tickRunner.RunOneTickAsync();

        var retailPulseFindings = await GetFindingsAsync(client, retailPulse);
        var fieldOpsFindings = await GetFindingsAsync(client, fieldOps);

        Assert.Empty(retailPulseFindings);
        Assert.Empty(fieldOpsFindings);
    }

    private static async Task IngestQuietTrafficAsync(HttpClient client, AppFixture retailPulse, AppFixture fieldOps, Random random)
    {
        var chargePaymentCounts = QuietDayGenerator.GenerateHourlyEventCounts(
            hour => ScenarioConstants.BusinessHours.Contains(hour) ? ScenarioConstants.ChargePaymentBusinessHourMean : ScenarioConstants.ChargePaymentNightHourMean,
            ScenarioConstants.QuietDaysBack, includeToday: true, random);
        var chargePaymentEvents = QuietDayGenerator.ToEvents(chargePaymentCounts, "Info", "Card authorized",
            module: ScenarioConstants.OrdersModule, screenService: ScenarioConstants.OrderApiServiceScreenService,
            process: ScenarioConstants.CreateOrderProcess, operation: ScenarioConstants.ChargePaymentOperation);
        await IngestionSender.SendBatchedAsync(client, retailPulse.ApiKey, chargePaymentEvents);

        var pullSupplierFeedCounts = QuietDayGenerator.GenerateHourlyEventCounts(
            _ => ScenarioConstants.PullSupplierFeedHourlyMean, ScenarioConstants.QuietDaysBack, includeToday: true, random);
        var pullSupplierFeedEvents = QuietDayGenerator.ToEvents(pullSupplierFeedCounts, "Info", "Supplier feed pulled",
            module: ScenarioConstants.InventoryModule, screenService: ScenarioConstants.StockServiceScreenService,
            process: ScenarioConstants.StockSyncProcess, operation: ScenarioConstants.PullSupplierFeedOperation);
        await IngestionSender.SendBatchedAsync(client, retailPulse.ApiKey, pullSupplierFeedEvents);

        var matchAvailabilityCounts = QuietDayGenerator.GenerateHourlyEventCounts(
            hour => ScenarioConstants.BusinessHours.Contains(hour) ? 20 : 3,
            ScenarioConstants.QuietDaysBack, includeToday: true, random);
        var matchAvailabilityEvents = QuietDayGenerator.ToEvents(matchAvailabilityCounts, "Info", "Technician availability matched",
            durationMs: _ => ScenarioConstants.MatchAvailabilityDurationMeanMs + (random.NextDouble() * 2 - 1) * ScenarioConstants.MatchAvailabilityDurationMeanMs * ScenarioConstants.NoiseRelativeRange,
            module: ScenarioConstants.SchedulingModule, screenService: ScenarioConstants.SchedulerApiScreenService,
            process: ScenarioConstants.AssignTechnicianProcess, operation: ScenarioConstants.MatchAvailabilityOperation);
        await IngestionSender.SendBatchedAsync(client, fieldOps.ApiKey, matchAvailabilityEvents);

        var aggregateJobsCounts = QuietDayGenerator.GenerateHourlyEventCounts(
            hour => ScenarioConstants.BusinessHours.Contains(hour) ? ScenarioConstants.AggregateJobsBusinessHourMean : ScenarioConstants.AggregateJobsNightHourMean,
            ScenarioConstants.QuietDaysBack, includeToday: true, random);
        var aggregateJobsEvents = QuietDayGenerator.ToEvents(aggregateJobsCounts, "Info", "Jobs aggregated",
            module: ScenarioConstants.ReportingModule, screenService: ScenarioConstants.DailyReportScreenService,
            process: ScenarioConstants.GenerateReportProcess, operation: ScenarioConstants.AggregateJobsOperation);
        await IngestionSender.SendBatchedAsync(client, fieldOps.ApiKey, aggregateJobsEvents);
    }

    private static async Task<List<FindingSummary>> GetFindingsAsync(HttpClient client, AppFixture app)
    {
        var response = await client.GetAsync($"/api/v1/findings?applicationId={app.ApplicationId}&environmentId={app.EnvironmentId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<FindingSummary>>())!;
    }
}
```

Note: `matchAvailabilityCounts`/`aggregateJobsCounts` use inline hour-mean lambdas (20/3, matching `ScenarioConstants`'s intended values but not yet promoted to named constants for the false-positive path specifically) — this mirrors exactly what Task 12's `ScenarioAcceptanceTests` also needs, so Task 12 factors this shared quiet-traffic-generation logic into a reusable private helper rather than duplicating it a third time (see Task 12, Step 3's note).

- [ ] **Step 3: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter FalsePositiveTests`
Expected: PASS — 3/3 tests (one per seed). **This test is slow** (generates and ingests ~35 days × 24 hours × 4 Operations of quiet traffic, 3 times) — expect several minutes, not seconds. If any seed fails, do not adjust `ScenarioConstants`' magnitudes to make it pass artificially; per the design doc, first check whether the noise model itself produced an unrealistic outlier hour (a bug in `QuietDayGenerator`), and only recalibrate `08`'s thresholds if the generator is confirmed correct and the current numbers are genuinely too sensitive.

- [ ] **Step 4: Commit**

```bash
git add tests/LogsPlatform.Tests/Scenario/IngestionSender.cs tests/LogsPlatform.Tests/Scenario/FalsePositiveTests.cs
git commit -m "Add FalsePositiveTests (3 seeds, quiet-only, assert 0 Findings)"
```

---

### Task 12: `ScenarioAcceptanceTests` — the real go/no-go

**Suggested model tier:** high (the actual acceptance gate; every prior task's output converges here).

**Files:**
- Create: `tests/LogsPlatform.Tests/Scenario/ScenarioAcceptanceTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-11.
- Produces: nothing — this is the terminal deliverable of the plan.

- [ ] **Step 1: Write `ScenarioAcceptanceTests`**

Create `tests/LogsPlatform.Tests/Scenario/ScenarioAcceptanceTests.cs`:

```csharp
using LogsPlatform.Domain.Entities;
using LogsPlatform.SyntheticDataGenerator;
using LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

[Collection("Database")]
public class ScenarioAcceptanceTests : IClassFixture<ScenarioTestWebApplicationFactory>
{
    private const int Seed = 777;

    private readonly ScenarioTestWebApplicationFactory _factory;

    public ScenarioAcceptanceTests(ScenarioTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task QuietHistoryPlusSixScenarios_ProducesExactlySixCorrectFindings()
    {
        var client = _factory.CreateClient();
        var random = new Random(Seed);

        var retailPulse = await DomainFixture.BuildRetailPulseAsync(client);
        var fieldOps = await DomainFixture.BuildFieldOpsAsync(client);
        var customerIds = await DomainFixture.SeedCustomersAsync(client, retailPulse.ApplicationId, ScenarioConstants.CustomerAnomalyPeerCount + 1);

        await IngestQuietHistoryAsync(client, retailPulse, fieldOps, random);

        await IngestionSender.SendBatchedAsync(client, retailPulse.ApiKey, ErrorSpikeInjector.Inject());
        await IngestionSender.SendBatchedAsync(client, retailPulse.ApiKey, NewExceptionInjector.Inject());
        await IngestionSender.SendBatchedAsync(client, retailPulse.ApiKey, CustomerAnomalyInjector.Inject(customerIds));
        await IngestionSender.SendBatchedAsync(client, fieldOps.ApiKey, PerformanceDegradationInjector.Inject(eventCount: 20));
        await DeploymentAnomalyInjector.CreateDeploymentAsync(client, fieldOps);
        await IngestionSender.SendBatchedAsync(client, fieldOps.ApiKey, DeploymentAnomalyInjector.InjectEvents());
        // MissingActivityInjector.Inject() returns no events by design — PullSupplierFeed's current
        // hour is simply never ingested, leaving it silent against its established Baseline.

        using (var scope = _factory.Services.CreateScope())
        {
            var tickRunner = scope.ServiceProvider.GetRequiredService<AnalysisEngineTickRunner>();
            await tickRunner.RunOneTickAsync();
        }

        var retailPulseFindings = await GetFindingsAsync(client, retailPulse);
        var fieldOpsFindings = await GetFindingsAsync(client, fieldOps);
        var allFindings = retailPulseFindings.Concat(fieldOpsFindings).ToList();

        Assert.Equal(6, allFindings.Count);

        await AssertErrorSpikeAsync(client, retailPulseFindings);
        await AssertNewExceptionAsync(client, retailPulseFindings);
        AssertMissingActivity(retailPulseFindings);
        AssertCustomerAnomaly(retailPulseFindings);
        await AssertPerformanceDegradationAsync(client, fieldOpsFindings);
        await AssertDeploymentAnomalyAsync(client, fieldOpsFindings);
    }

    private static async Task IngestQuietHistoryAsync(HttpClient client, AppFixture retailPulse, AppFixture fieldOps, Random random)
    {
        var chargePaymentCounts = QuietDayGenerator.GenerateHourlyEventCounts(
            hour => ScenarioConstants.BusinessHours.Contains(hour) ? ScenarioConstants.ChargePaymentBusinessHourMean : ScenarioConstants.ChargePaymentNightHourMean,
            ScenarioConstants.QuietDaysBack, includeToday: false, random);
        await IngestionSender.SendBatchedAsync(client, retailPulse.ApiKey, QuietDayGenerator.ToEvents(chargePaymentCounts, "Info", "Card authorized",
            module: ScenarioConstants.OrdersModule, screenService: ScenarioConstants.OrderApiServiceScreenService,
            process: ScenarioConstants.CreateOrderProcess, operation: ScenarioConstants.ChargePaymentOperation));

        var pullSupplierFeedCounts = QuietDayGenerator.GenerateHourlyEventCounts(
            _ => ScenarioConstants.PullSupplierFeedHourlyMean, ScenarioConstants.QuietDaysBack, includeToday: false, random);
        await IngestionSender.SendBatchedAsync(client, retailPulse.ApiKey, QuietDayGenerator.ToEvents(pullSupplierFeedCounts, "Info", "Supplier feed pulled",
            module: ScenarioConstants.InventoryModule, screenService: ScenarioConstants.StockServiceScreenService,
            process: ScenarioConstants.StockSyncProcess, operation: ScenarioConstants.PullSupplierFeedOperation));

        var matchAvailabilityCounts = QuietDayGenerator.GenerateHourlyEventCounts(
            hour => ScenarioConstants.BusinessHours.Contains(hour) ? 20 : 3,
            ScenarioConstants.QuietDaysBack, includeToday: false, random);
        await IngestionSender.SendBatchedAsync(client, fieldOps.ApiKey, QuietDayGenerator.ToEvents(matchAvailabilityCounts, "Info", "Technician availability matched",
            durationMs: _ => ScenarioConstants.MatchAvailabilityDurationMeanMs + (random.NextDouble() * 2 - 1) * ScenarioConstants.MatchAvailabilityDurationMeanMs * ScenarioConstants.NoiseRelativeRange,
            module: ScenarioConstants.SchedulingModule, screenService: ScenarioConstants.SchedulerApiScreenService,
            process: ScenarioConstants.AssignTechnicianProcess, operation: ScenarioConstants.MatchAvailabilityOperation));

        var aggregateJobsCounts = QuietDayGenerator.GenerateHourlyEventCounts(
            hour => ScenarioConstants.BusinessHours.Contains(hour) ? ScenarioConstants.AggregateJobsBusinessHourMean : ScenarioConstants.AggregateJobsNightHourMean,
            ScenarioConstants.QuietDaysBack, includeToday: false, random);
        await IngestionSender.SendBatchedAsync(client, fieldOps.ApiKey, QuietDayGenerator.ToEvents(aggregateJobsCounts, "Info", "Jobs aggregated",
            module: ScenarioConstants.ReportingModule, screenService: ScenarioConstants.DailyReportScreenService,
            process: ScenarioConstants.GenerateReportProcess, operation: ScenarioConstants.AggregateJobsOperation));
    }

    private static async Task<List<FindingSummary>> GetFindingsAsync(HttpClient client, AppFixture app)
    {
        var response = await client.GetAsync($"/api/v1/findings?applicationId={app.ApplicationId}&environmentId={app.EnvironmentId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<FindingSummary>>())!;
    }

    private static async Task<FindingDetail> GetDetailAsync(HttpClient client, long findingId)
    {
        var response = await client.GetAsync($"/api/v1/findings/{findingId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FindingDetail>())!;
    }

    private static async Task AssertErrorSpikeAsync(HttpClient client, List<FindingSummary> findings)
    {
        var finding = Assert.Single(findings, f => f.Type == nameof(FindingType.ErrorSpike));
        Assert.Equal(nameof(FindingSeverity.High), finding.Severity);
        Assert.Equal(nameof(ConfidenceLevel.High), finding.ConfidenceLevel);
        Assert.Equal(ScenarioConstants.ChargePaymentOperation, finding.OperationName);

        var detail = await GetDetailAsync(client, finding.Id);
        var fact = Assert.Single(detail.Statements, s => s.Kind == nameof(FindingStatementKind.Fact));
        Assert.Contains(ScenarioConstants.ErrorSpikeEventCount.ToString(), fact.Text);
    }

    private static async Task AssertNewExceptionAsync(HttpClient client, List<FindingSummary> findings)
    {
        var finding = Assert.Single(findings, f => f.Type == nameof(FindingType.NewException));
        Assert.Equal(nameof(FindingSeverity.High), finding.Severity);
        Assert.Equal(nameof(ConfidenceLevel.High), finding.ConfidenceLevel);

        var detail = await GetDetailAsync(client, finding.Id);
        Assert.Contains(detail.Statements, s => s.Kind == nameof(FindingStatementKind.Hypothesis));
        Assert.Contains(detail.Evidence, e => e.EvidenceType == nameof(EvidenceType.Event));
    }

    private static void AssertMissingActivity(List<FindingSummary> findings)
    {
        var finding = Assert.Single(findings, f => f.Type == nameof(FindingType.MissingActivity));
        Assert.Equal(nameof(ConfidenceLevel.High), finding.ConfidenceLevel);
        Assert.Equal(ScenarioConstants.PullSupplierFeedOperation, finding.OperationName);
    }

    private static void AssertCustomerAnomaly(List<FindingSummary> findings)
    {
        var finding = Assert.Single(findings, f => f.Type == nameof(FindingType.CustomerAnomaly));
        Assert.Equal(nameof(ConfidenceLevel.High), finding.ConfidenceLevel);
    }

    private static async Task AssertPerformanceDegradationAsync(HttpClient client, List<FindingSummary> findings)
    {
        var finding = Assert.Single(findings, f => f.Type == nameof(FindingType.PerformanceDegradation));
        Assert.Equal(nameof(ConfidenceLevel.High), finding.ConfidenceLevel);
        Assert.Equal(ScenarioConstants.MatchAvailabilityOperation, finding.OperationName);

        var detail = await GetDetailAsync(client, finding.Id);
        var fact = Assert.Single(detail.Statements, s => s.Kind == nameof(FindingStatementKind.Fact));
        Assert.Contains(ScenarioConstants.PerformanceDegradationDurationMs.ToString(), fact.Text);
    }

    private static async Task AssertDeploymentAnomalyAsync(HttpClient client, List<FindingSummary> findings)
    {
        var finding = Assert.Single(findings, f => f.Type == nameof(FindingType.ErrorSpike) && f.OperationName == ScenarioConstants.AggregateJobsOperation);
        Assert.Equal(nameof(ConfidenceLevel.High), finding.ConfidenceLevel);

        var detail = await GetDetailAsync(client, finding.Id);
        Assert.Contains(detail.Statements, s => s.Kind == nameof(FindingStatementKind.Hypothesis));
        Assert.Contains(detail.Evidence, e => e.EvidenceType == nameof(EvidenceType.Deployment));
    }
}
```

Note: `AssertErrorSpikeAsync`/`AssertDeploymentAnomalyAsync` both query `f.Type == nameof(FindingType.ErrorSpike)` — RetailPulse's `ChargePayment` and FieldOps's `AggregateJobs` are in different applications (queried separately via `retailPulseFindings`/`fieldOpsFindings`, filtered per-app), so there's no collision between the two ErrorSpike Findings despite sharing a `FindingType`; `AssertDeploymentAnomalyAsync` additionally filters by `OperationName` for clarity even though the app-level split alone is already unambiguous.

- [ ] **Step 2: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter ScenarioAcceptanceTests`
Expected: PASS — 1/1 test. **This is the slowest test in the project** (35 days × 24 hours × 4 quiet Operations + 6 scenario injections, one seed) — expect several minutes. If it fails, per `12-תוכנית-עבודה-ואבני-דרך.md` §3's explicit instruction: **do not loosen this test's assertions or the generator's scenario magnitudes to make it pass** — investigate whether the failure is a genuine detector/threshold problem (fix `08`'s parameters, already centralized as named constants) or a generator bug (fix `QuietDayGenerator`/the injectors), and re-run.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test tests/LogsPlatform.Tests`
Expected: all tests pass (281 pre-M5 baseline + this plan's new tests). This full run will take noticeably longer than any prior milestone's, due to Tasks 11-12's data volume — that's expected and matches the Test Strategy doc's own acknowledgment that this is the project's most expensive test.

- [ ] **Step 4: Commit**

```bash
git add tests/LogsPlatform.Tests/Scenario/ScenarioAcceptanceTests.cs
git commit -m "Add ScenarioAcceptanceTests: the M5 go/no-go — 6/6 correct Findings from synthetic history"
```

---

## Self-Review Notes

**Spec coverage:** All 6 scenarios from `06`'s §4 table are covered (Tasks 6-10, executed together in Task 12). `11`'s §3 acceptance criteria are checked per-Finding in `ScenarioAcceptanceTests`: correct `Type`/`Scope` (via `OperationName` and app-level separation), `Fact` statement carrying the actual injected numeric value (not an approximation — `ErrorSpikeEventCount`/`PerformanceDegradationDurationMs` are asserted as exact substrings), `Confidence=High` on all 6 (which required Task 2's fix), Deployment Evidence+Hypothesis for the Deployment scenario, and Downstream Hypothesis+Evidence for the New Exception scenario. `11`'s §4 (false-positive test, 3 seeds per this plan's locked-in reduction from the spec's suggested 5) is Task 11. `11`'s §5 (Baseline-learning-in-isolation unit test) and §6 (Security tests) are out of scope per the design doc — not silently dropped, explicitly deferred to a follow-up / M6 respectively.

**A critical timing-window correction made during this plan's writing, not present in the design doc:** the design doc described scenario injection loosely as "days 36-40." Reading the actual detector code revealed each detector checks a *different*, narrow, real-time-relative window — `RateAnomalyDetector` only the current UTC hour bucket, `NewExceptionDetector` only 5 minutes, `DeploymentCorrelator` only 60 minutes, `CustomerOutlierDetector` 24 hours — none of which "day 36-40" (a generation-time-relative concept) maps onto correctly if generation and test-run happen at different moments, or if the test takes any real wall-clock time to run (which it does, non-trivially, given the data volume). Every injector in this plan targets "now" (at test-run time, not generation time) directly, and quiet-day history is generated relative to the same "now."

**A second real gap found and fixed (Task 2):** `CustomerOutlierDetector` unconditionally wrote `ConfidenceLevel.Medium`, contradicting `08`'s own §6 confidence table, which the spec's own wording (`11`'s §3: "Confidence=High בכל 6") requires apply uniformly. Fixed to mirror `RateAnomalyDetector`'s exact High/Medium split logic, using total compared-customer count as the `SampleCount` analog — which in turn required `CustomerAnomalyInjector` to seed at least `MIN_SAMPLES`(14) peer customers, not just `MIN_PEER_CUSTOMERS`(5).

**A third real risk found and fixed (Task 4):** every prior `WebApplicationFactory`-based test in this project has silently hosted a live, auto-ticking `AnalysisEngineBackgroundService` since M4a — harmless until now, since no prior test's data was ever shaped to risk a false Finding, but a real race risk for a test that (a) needs full deterministic control over exactly when detection runs and (b) takes long enough to generate/ingest its data that the real 5-minute timer could plausibly fire mid-generation. `ScenarioTestWebApplicationFactory` removes the hosted service registration; this is scoped to the new factory only, not a retroactive change to `TestWebApplicationFactory` (out of scope for this plan, though worth a future look).

**Type consistency:** `SimulatedEvent`, `AppFixture`, `ScenarioConstants` field/property names are used identically across every task from Task 3 onward — verified by re-reading each call site. `IngestionSender.ToRequest`'s field mapping matches `IngestEventRequest`'s exact constructor order (Task 1's contract-reading pass). `FindingSummary`/`FindingDetail`/`FindingStatementDto`/`EvidenceDto` (all pre-existing from M4b) are consumed with their exact property names (`Type`, `Severity`, `ConfidenceLevel`, `OperationName`, `Kind`, `EvidenceType`) in Task 12's assertions, using `nameof(...)` against the real enums rather than hardcoded strings, so a future rename would break the build here instead of silently mismatching.

**Illegal mixed named/positional C# arguments:** every multi-argument call in this plan is either fully positional (`new SimulatedEvent(...)`, `new IngestEventRequest(...)`) or fully named (the anonymous objects passed to `PostAsJsonAsync` in `DomainFixture`/`DeploymentAnomalyInjector`) — no call mixes named-then-positional. Checked specifically since this class of bug has been caught in M3's and re-checked in every plan since.

**DI lifetime audit:** no new `BackgroundService`/`AddHostedService` registration in this plan (Task 4 *removes* one, for tests only). `AnalysisEngineTickRunner` and everything it depends on are already `Scoped` (confirmed against `Program.cs`), so `factory.Services.CreateScope().ServiceProvider.GetRequiredService<AnalysisEngineTickRunner>()` resolves the exact same dependency graph the real app would build — no hand-wiring, no lifetime mismatch.

**FK/cascade behavior:** no new entities or migrations in this plan.
