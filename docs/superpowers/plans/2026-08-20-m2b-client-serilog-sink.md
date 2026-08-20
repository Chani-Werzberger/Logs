# M2b: LogsPlatform.Client + Serilog Sink Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a standalone `LogsPlatform.Client` library exposing a batching HTTP client for M2a's ingestion API, plus a thin Serilog sink adapter on top of the same client.

**Architecture:** One project, four files of real logic: wire DTOs (`EventPayload`), the batching engine (`LogsPlatformClient`, a lock-protected buffer + `System.Threading.Timer`), and a Serilog sink (`LogsPlatformSink`) that translates `LogEvent` → `EventPayload` and calls the same client. No dependency on `LogsPlatform.Domain`/`Infrastructure`/`Web` — only on the wire JSON shape those projects already expose via the (already-merged) M2a ingestion endpoint.

**Tech Stack:** .NET 10 (net10.0), `Serilog` 4.4.0 (core package only). Tests: xUnit, real SQL Server LocalDB via the existing `TestWebApplicationFactory`/`TestDatabase` infrastructure — no mocking.

## Global Constraints

- Target framework `net10.0`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>` — matches every other `.csproj` in the solution.
- `LogsPlatform.Client` has **zero** `ProjectReference`s to `LogsPlatform.Domain`, `LogsPlatform.Infrastructure`, or `LogsPlatform.Web`. It is a standalone library a real external consumer could install without pulling in EF Core or SQL Server.
- Only NuGet dependency: `Serilog` version `4.4.0` (verified current latest-stable on nuget.org, per the design doc). **No** `Serilog.Sinks.PeriodicBatching` — the batching engine lives in `LogsPlatformClient` itself and is shared by both usage modes, not duplicated per-sink.
- Wire JSON must match `src/LogsPlatform.Web/Contracts/IngestionContracts.cs`'s `IngestEventRequest` field-for-field (same 16 top-level fields, same nested `Hierarchy`/`Exception` shape). Server route: `POST api/v1/ingest/events` (relative path), auth header `X-Api-Key`. ASP.NET Core's `[FromBody]` binding is case-insensitive (confirmed in M2a), so client DTOs use plain PascalCase property names — no `JsonNamingPolicy` needed.
- Severity wire values are the case-sensitive strings `Trace|Debug|Info|Warn|Error|Fatal` (`IngestionProcessor`'s fixed severity map on the server: `Trace=1,Debug=5,Info=9,Warn=13,Error=17,Fatal=21`). The Serilog sink's `LogEventLevel` → severity mapping MUST use an explicit lookup table — never `LogEventLevel.ToString()` (only 3 of 6 names match textually).
- Exception stack traces sent over the wire MUST be the raw frame text (`Exception.StackTrace`), **never** `Exception.ToString()` — `ToString()` prepends the exception type and message, which corrupts server-side fingerprint-based exception grouping (`ExceptionFingerprinter` reads the top 3 lines of whatever string arrives).
- `FlushAsync()` — both the public method and the internal timer/size-triggered path — must never let an exception escape to the caller, for any failure (network error, non-success HTTP status, timeout). Failures are written to `Console.Error` only; the batch is dropped, never retried or re-queued. This is the client's core reliability guarantee: an ingestion outage must never crash the host application.
- Tests that read back data written through a real hosted server MUST construct the verification `DbContext` via `new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options` directly (see `tests/LogsPlatform.Tests/Infrastructure/LogsPlatformDbContextTests.cs` for the established pattern) — **never** via `TestDatabase.CreateContext()`, which calls `EnsureDeleted()`+`Migrate()` on every call and would wipe out the very rows the test is trying to verify. This exact mistake caused a real bug earlier in the project.
- No mocking, no fakes, no HTTP stubs. Every test exercising HTTP behavior does so against either a real hosted `TestWebApplicationFactory` server or a genuinely unreachable address (`http://127.0.0.1:1/`).
- Keep test volume modest, per the project's standing instruction: one test per distinct behavior, not exhaustive permutations.

---

### Task 1: Scaffold the `LogsPlatform.Client` project

**Suggested model tier:** cheapest (pure mechanical scaffolding).

**Files:**
- Create: `src/LogsPlatform.Client/LogsPlatform.Client.csproj`
- Modify: `LogsPlatform.sln` (add project)
- Modify: `tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj` (add `ProjectReference` to the new project)

**Interfaces:**
- Consumes: nothing (first task).
- Produces: a buildable, empty `LogsPlatform.Client` class library, referenceable by `LogsPlatform.Tests`.

- [ ] **Step 1: Create the project file**

`src/LogsPlatform.Client/LogsPlatform.Client.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Serilog" Version="4.4.0" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add the project to the solution**

Run: `dotnet sln LogsPlatform.sln add src/LogsPlatform.Client/LogsPlatform.Client.csproj`
Expected: `Project(s) added to the solution.`

- [ ] **Step 3: Reference it from the test project**

Run: `dotnet add tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj reference src/LogsPlatform.Client/LogsPlatform.Client.csproj`
Expected: `Reference ... added to the project.`

Note: `LogsPlatform.Tests` will transitively see the `Serilog` package through this `ProjectReference` (SDK-style projects expose `PackageReference`s transitively unless `PrivateAssets` is set, which we don't set) — Task 4's Serilog-based tests need no separate `Serilog` package reference in the test project.

- [ ] **Step 4: Verify the solution builds**

Run: `dotnet build LogsPlatform.sln`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Client/LogsPlatform.Client.csproj LogsPlatform.sln tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj
git commit -m "Scaffold LogsPlatform.Client project (M2b)"
```

---

### Task 2: Wire DTOs (`EventPayload`)

**Suggested model tier:** cheapest (mechanical transcription, mirrors an existing server-side record family field-for-field).

**Files:**
- Create: `src/LogsPlatform.Client/EventPayload.cs`
- Test: `tests/LogsPlatform.Tests/Client/EventPayloadSerializationTests.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces (used by Tasks 3 and 4): `public record IngestHierarchyPayload(string? Module, string? ScreenService, string? Process, string? Operation)`; `public record IngestExceptionPayload(string Type, string? StackTrace)`; `public record EventPayload(string? EventKey, DateTime Timestamp, string Severity, string Environment, string? Version, IngestHierarchyPayload? Hierarchy, string? CorrelationId, string? TraceId, string? SpanId, string? ParentSpanId, double? DurationMs, string? CustomerId, string? UserId, string Message, string? MessageTemplate, IngestExceptionPayload? Exception, Dictionary<string, object>? Metadata)`.

Note on nullability: the server's `IngestEventRequest` makes every field nullable (it must defensively parse untrusted external input and reject at runtime). This client SDK fully controls how `EventPayload` gets constructed, so the four fields the server requires — `Timestamp`, `Severity`, `Environment`, `Message` — are non-nullable C# types here, catching a missing required field at compile time instead of via a runtime rejection. The wire JSON shape (field names, nesting) still matches the server's contract exactly.

- [ ] **Step 1: Write the failing tests**

`tests/LogsPlatform.Tests/Client/EventPayloadSerializationTests.cs`:

```csharp
using System.Text.Json;
using LogsPlatform.Client;

namespace LogsPlatform.Tests.Client;

public class EventPayloadSerializationTests
{
    [Fact]
    public void EventPayload_FullyPopulated_SerializesAllFieldsWithPascalCaseNames()
    {
        var payload = new EventPayload(
            EventKey: "key-1", Timestamp: new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc),
            Severity: "Error", Environment: "Production", Version: "1.2.3",
            Hierarchy: new IngestHierarchyPayload("Billing", "InvoiceService", "ChargeCard", "Charge"),
            CorrelationId: "corr-1", TraceId: "trace-1", SpanId: "span-1", ParentSpanId: "parent-1",
            DurationMs: 12.5, CustomerId: "cust-1", UserId: "user-1",
            Message: "Something failed", MessageTemplate: "Something {What}",
            Exception: new IngestExceptionPayload("System.TimeoutException", "at Foo.Bar() line 1"),
            Metadata: new Dictionary<string, object> { ["Key"] = "Value" });

        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"EventKey\":\"key-1\"", json);
        Assert.Contains("\"Severity\":\"Error\"", json);
        Assert.Contains("\"Environment\":\"Production\"", json);
        Assert.Contains("\"Hierarchy\":{\"Module\":\"Billing\"", json);
        Assert.Contains("\"Exception\":{\"Type\":\"System.TimeoutException\"", json);
        Assert.Contains("\"Message\":\"Something failed\"", json);
    }

    [Fact]
    public void EventPayload_OnlyRequiredFields_SerializesWithoutThrowing()
    {
        var payload = new EventPayload(
            EventKey: null, Timestamp: DateTime.UtcNow, Severity: "Info", Environment: "Production",
            Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null,
            ParentSpanId: null, DurationMs: null, CustomerId: null, UserId: null,
            Message: "hello", MessageTemplate: null, Exception: null, Metadata: null);

        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"Message\":\"hello\"", json);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter EventPayloadSerializationTests`
Expected: FAIL — compile error, `EventPayload`/`IngestHierarchyPayload`/`IngestExceptionPayload` do not exist.

- [ ] **Step 3: Implement `EventPayload.cs`**

`src/LogsPlatform.Client/EventPayload.cs`:

```csharp
namespace LogsPlatform.Client;

public record IngestHierarchyPayload(string? Module, string? ScreenService, string? Process, string? Operation);

public record IngestExceptionPayload(string Type, string? StackTrace);

public record EventPayload(
    string? EventKey,
    DateTime Timestamp,
    string Severity,
    string Environment,
    string? Version,
    IngestHierarchyPayload? Hierarchy,
    string? CorrelationId,
    string? TraceId,
    string? SpanId,
    string? ParentSpanId,
    double? DurationMs,
    string? CustomerId,
    string? UserId,
    string Message,
    string? MessageTemplate,
    IngestExceptionPayload? Exception,
    Dictionary<string, object>? Metadata);
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter EventPayloadSerializationTests`
Expected: PASS — 2/2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Client/EventPayload.cs tests/LogsPlatform.Tests/Client/EventPayloadSerializationTests.cs
git commit -m "Add EventPayload wire DTOs for LogsPlatform.Client"
```

---

### Task 3: Core `ILogsPlatformClient` / `LogsPlatformClient` batching engine

**Suggested model tier:** standard (concurrency reasoning, integration with a real hosted test server).

**Files:**
- Create: `src/LogsPlatform.Client/ILogsPlatformClient.cs`
- Create: `src/LogsPlatform.Client/LogsPlatformClient.cs`
- Create: `tests/LogsPlatform.Tests/Client/TestPolling.cs`
- Test: `tests/LogsPlatform.Tests/Client/LogsPlatformClientTests.cs`

**Interfaces:**
- Consumes: `EventPayload` (Task 2). Existing test infrastructure: `TestWebApplicationFactory` (`tests/LogsPlatform.Tests/Web/TestWebApplicationFactory.cs`), `TestDatabase.ConnectionString` (`tests/LogsPlatform.Tests/Infrastructure/TestDatabase.cs`), `LogsPlatformDbContext` (`LogsPlatform.Infrastructure`), `Event` (`LogsPlatform.Domain.Entities`), and the admin HTTP contracts `CreateApplicationRequest`/`ApplicationResponse`/`CreateEnvironmentRequest`/`CreateApiKeyRequest`/`CreateApiKeyResponse` (`LogsPlatform.Web.Contracts`).
- Produces (used by Task 4): `public interface ILogsPlatformClient : IAsyncDisposable { Task SendEventAsync(EventPayload evt); Task FlushAsync(); }`; `public sealed class LogsPlatformClient : ILogsPlatformClient` with constructor `LogsPlatformClient(string baseUrl, string apiKey, HttpClient? httpClient = null, int batchSize = 100, TimeSpan? period = null, int queueLimit = 10_000)`.

Behavior to implement (all covered by the tests below):
- `SendEventAsync` appends to a `lock`-protected buffer and returns a completed `Task` immediately (no network I/O on the calling path). If the append pushes the buffer to `queueLimit` entries or beyond, the oldest entry is dropped (`RemoveAt(0)`) before the size check — this only engages in practice when `batchSize` is configured larger than `queueLimit` (the default `batchSize=100 <= queueLimit=10_000` makes it dead code in normal operation; it exists as a safety net and is exercised deliberately in the overflow test below by configuring `batchSize` larger than `queueLimit`). If the buffer then reaches `batchSize`, it is swapped out (snapshotted and cleared under the same lock) and flushed via fire-and-forget (`_ = FlushBatchAsync(toFlush)`).
- A `System.Threading.Timer` fires every `period` (default 2 seconds) and calls the public `FlushAsync()`, which drains whatever is currently buffered (even a partial batch) the same way.
- `FlushAsync()` never throws: the actual `HttpClient.PostAsJsonAsync` call is wrapped in try/catch; a non-success status code or a thrown exception is written to `Console.Error` and the batch is simply dropped.
- If `httpClient` is not provided, the client creates and owns one (`BaseAddress = new Uri(baseUrl)`), disposed alongside the client in `DisposeAsync()`. If `httpClient` IS provided, it is used as-is — `baseUrl` is not applied to it (the caller's `HttpClient`, e.g. one obtained from `TestWebApplicationFactory`, is assumed to already be usably configured, including its own `BaseAddress`). Either way, the constructor adds an `X-Api-Key` header to the client's `DefaultRequestHeaders` with the given `apiKey`.
- `DisposeAsync()` stops the timer, awaits `FlushAsync()` to drain any remaining buffered events, then disposes the owned `HttpClient` if the client created its own.

- [ ] **Step 1: Add the test polling helper**

`tests/LogsPlatform.Tests/Client/TestPolling.cs`:

```csharp
namespace LogsPlatform.Tests.Client;

internal static class TestPolling
{
    public static async Task<int> WaitForCountAsync(Func<Task<int>> countQuery, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var count = await countQuery();
        while (count < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            count = await countQuery();
        }
        return count;
    }
}
```

This exists because flushes triggered by the size/timer paths are fire-and-forget from the caller's perspective — tests need to poll briefly rather than assume the write has landed the instant `SendEventAsync` returns. Reused by Task 4's sink tests.

- [ ] **Step 2: Write the failing tests**

`tests/LogsPlatform.Tests/Client/LogsPlatformClientTests.cs`:

```csharp
using System.Net.Http.Json;
using LogsPlatform.Client;
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Tests.Web;
using LogsPlatform.Web.Contracts;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Client;

[Collection("Database")]
public class LogsPlatformClientTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public LogsPlatformClientTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(int ApplicationId, string ApiKey)> CreateAppWithApiKeyAsync(string appName)
    {
        var setupClient = _factory.CreateClient();
        var appResponse = await setupClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        await setupClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));

        var keyResponse = await setupClient.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("Client test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        return (app.Id, key!.ApiKey);
    }

    private static async Task<int> CountEventsAsync(int applicationId)
    {
        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString)
            .Options;
        await using var context = new LogsPlatformDbContext(options);
        return await context.Events.CountAsync(e => e.ApplicationId == applicationId);
    }

    private static EventPayload BuildEvent(string eventKey) => new(
        EventKey: eventKey, Timestamp: DateTime.UtcNow, Severity: "Error", Environment: "Production",
        Version: null, Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null,
        DurationMs: null, CustomerId: null, UserId: null, Message: "client test event", MessageTemplate: null,
        Exception: null, Metadata: null);

    [Fact]
    public async Task SendEventAsync_ReachesBatchSize_FlushesAndPersistsEvents()
    {
        var (appId, apiKey) = await CreateAppWithApiKeyAsync("ClientBatchSizeTestApp");
        await using var client = new LogsPlatformClient(
            baseUrl: "http://localhost/", apiKey: apiKey, httpClient: _factory.CreateClient(),
            batchSize: 2, period: TimeSpan.FromMinutes(10));

        await client.SendEventAsync(BuildEvent("batch-1"));
        await client.SendEventAsync(BuildEvent("batch-2"));

        var count = await TestPolling.WaitForCountAsync(() => CountEventsAsync(appId), expected: 2, timeout: TimeSpan.FromSeconds(3));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task SendEventAsync_TimerElapses_FlushesPartialBatchAndPersists()
    {
        var (appId, apiKey) = await CreateAppWithApiKeyAsync("ClientTimerTestApp");
        await using var client = new LogsPlatformClient(
            baseUrl: "http://localhost/", apiKey: apiKey, httpClient: _factory.CreateClient(),
            batchSize: 100, period: TimeSpan.FromMilliseconds(200));

        await client.SendEventAsync(BuildEvent("timer-1"));

        var count = await TestPolling.WaitForCountAsync(() => CountEventsAsync(appId), expected: 1, timeout: TimeSpan.FromSeconds(2));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SendEventAsync_ExceedsQueueLimit_DropsOldestBeforeFlush()
    {
        var (appId, apiKey) = await CreateAppWithApiKeyAsync("ClientOverflowTestApp");
        await using var client = new LogsPlatformClient(
            baseUrl: "http://localhost/", apiKey: apiKey, httpClient: _factory.CreateClient(),
            batchSize: 1000, period: TimeSpan.FromMinutes(10), queueLimit: 5);

        for (var i = 0; i < 8; i++)
        {
            await client.SendEventAsync(BuildEvent($"k{i}"));
        }
        await client.FlushAsync();

        var count = await TestPolling.WaitForCountAsync(() => CountEventsAsync(appId), expected: 5, timeout: TimeSpan.FromSeconds(3));
        Assert.Equal(5, count);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var context = new LogsPlatformDbContext(options);
        var keys = await context.Events.Where(e => e.ApplicationId == appId).Select(e => e.EventKey).ToListAsync();
        Assert.Equal(new[] { "k3", "k4", "k5", "k6", "k7" }, keys.OrderBy(k => k));
    }

    [Fact]
    public async Task FlushAsync_UnreachableServer_DoesNotThrow()
    {
        await using var client = new LogsPlatformClient(
            baseUrl: "http://127.0.0.1:1/", apiKey: "irrelevant",
            batchSize: 100, period: TimeSpan.FromMinutes(10));

        await client.SendEventAsync(BuildEvent("unreachable-1"));

        var exception = await Record.ExceptionAsync(() => client.FlushAsync());
        Assert.Null(exception);
    }

    [Fact]
    public async Task DisposeAsync_WithPendingEvents_FlushesBeforeDisposing()
    {
        var (appId, apiKey) = await CreateAppWithApiKeyAsync("ClientDisposeTestApp");
        var client = new LogsPlatformClient(
            baseUrl: "http://localhost/", apiKey: apiKey, httpClient: _factory.CreateClient(),
            batchSize: 100, period: TimeSpan.FromMinutes(10));

        await client.SendEventAsync(BuildEvent("dispose-1"));
        await client.DisposeAsync();

        var count = await CountEventsAsync(appId);
        Assert.Equal(1, count);
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter LogsPlatformClientTests`
Expected: FAIL — compile error, `ILogsPlatformClient`/`LogsPlatformClient` do not exist.

- [ ] **Step 4: Implement `ILogsPlatformClient.cs`**

`src/LogsPlatform.Client/ILogsPlatformClient.cs`:

```csharp
namespace LogsPlatform.Client;

public interface ILogsPlatformClient : IAsyncDisposable
{
    Task SendEventAsync(EventPayload evt);
    Task FlushAsync();
}
```

- [ ] **Step 5: Implement `LogsPlatformClient.cs`**

`src/LogsPlatform.Client/LogsPlatformClient.cs`:

```csharp
using System.Net.Http.Json;

namespace LogsPlatform.Client;

public sealed class LogsPlatformClient : ILogsPlatformClient
{
    private const string IngestPath = "api/v1/ingest/events";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly int _batchSize;
    private readonly int _queueLimit;
    private readonly Timer _timer;
    private readonly object _bufferLock = new();
    private readonly List<EventPayload> _buffer = new();
    private bool _disposed;

    public LogsPlatformClient(
        string baseUrl,
        string apiKey,
        HttpClient? httpClient = null,
        int batchSize = 100,
        TimeSpan? period = null,
        int queueLimit = 10_000)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(apiKey);

        _batchSize = batchSize;
        _queueLimit = queueLimit;

        if (httpClient is null)
        {
            _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }

        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        var actualPeriod = period ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(OnTimerTick, null, actualPeriod, actualPeriod);
    }

    public Task SendEventAsync(EventPayload evt)
    {
        List<EventPayload>? toFlush = null;
        lock (_bufferLock)
        {
            _buffer.Add(evt);
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
            _ = FlushBatchAsync(toFlush);
        }

        return Task.CompletedTask;
    }

    public Task FlushAsync()
    {
        List<EventPayload>? toFlush = null;
        lock (_bufferLock)
        {
            if (_buffer.Count > 0)
            {
                toFlush = new List<EventPayload>(_buffer);
                _buffer.Clear();
            }
        }

        return toFlush is null ? Task.CompletedTask : FlushBatchAsync(toFlush);
    }

    private void OnTimerTick(object? state)
    {
        _ = FlushAsync();
    }

    private async Task FlushBatchAsync(List<EventPayload> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            var response = await _httpClient.PostAsJsonAsync(IngestPath, batch);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[LogsPlatform.Client] Ingestion request failed with status {(int)response.StatusCode}.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LogsPlatform.Client] Ingestion request failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _timer.Dispose();
        await FlushAsync();

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
```

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter LogsPlatformClientTests`
Expected: PASS — 5/5 tests. (Note: LocalDB writes mean this may take a few seconds per test; that's expected given the real-server, real-DB testing convention.)

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Client/ILogsPlatformClient.cs src/LogsPlatform.Client/LogsPlatformClient.cs tests/LogsPlatform.Tests/Client/TestPolling.cs tests/LogsPlatform.Tests/Client/LogsPlatformClientTests.cs
git commit -m "Add LogsPlatformClient batching engine (M2b core client)"
```

---

### Task 4: Serilog sink

**Suggested model tier:** standard (correct property/exception extraction is a real correctness risk — see Global Constraints on severity mapping and stack traces).

**Files:**
- Create: `src/LogsPlatform.Client/Serilog/LogsPlatformSink.cs`
- Create: `src/LogsPlatform.Client/Serilog/LogsPlatformSinkExtensions.cs`
- Test: `tests/LogsPlatform.Tests/Client/LogsPlatformSinkTests.cs`

**Interfaces:**
- Consumes: `EventPayload`/`IngestHierarchyPayload`/`IngestExceptionPayload` (Task 2), `ILogsPlatformClient`/`LogsPlatformClient` (Task 3), `TestPolling` (Task 3's test helper).
- Produces: `public static class LogsPlatformSinkExtensions { public static LoggerConfiguration LogsPlatform(this LoggerSinkConfiguration sinkConfiguration, string apiKey, string baseUrl, string environment, LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose) }` — the only public entry point external consumers of the Serilog integration use.

Namespace note: both new files live in `namespace LogsPlatform.Client.Serilog;` — a namespace nested inside `LogsPlatform.Client`. C# namespace member lookup searches enclosing namespaces before `using` directives, so `EventPayload`, `IngestHierarchyPayload`, `IngestExceptionPayload`, `ILogsPlatformClient`, and `LogsPlatformClient` (all declared directly in `LogsPlatform.Client`) are visible in these files with no extra `using` needed. `using Serilog;`/`using Serilog.Core;`/`using Serilog.Configuration;`/`using Serilog.Events;` refer unambiguously to the NuGet package's top-level `Serilog` namespace (using directives always resolve from the global namespace, so the shared trailing name `Serilog` is not ambiguous).

- [ ] **Step 1: Write the failing tests**

`tests/LogsPlatform.Tests/Client/LogsPlatformSinkTests.cs`:

```csharp
using System.Net.Http.Json;
using LogsPlatform.Client.Serilog;
using LogsPlatform.Infrastructure;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Tests.Web;
using LogsPlatform.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Context;

namespace LogsPlatform.Tests.Client;

[Collection("Database")]
public class LogsPlatformSinkTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public LogsPlatformSinkTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(int ApplicationId, string ApiKey)> CreateAppWithApiKeyAsync(string appName)
    {
        var setupClient = _factory.CreateClient();
        var appResponse = await setupClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        await setupClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));

        var keyResponse = await setupClient.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("Sink test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        return (app.Id, key!.ApiKey);
    }

    private static async Task<T> QueryAsync<T>(Func<LogsPlatformDbContext, Task<T>> query)
    {
        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString)
            .Options;
        await using var context = new LogsPlatformDbContext(options);
        return await query(context);
    }

    [Fact]
    public async Task LogsPlatform_InformationLevelLog_StoresSeverityAsInfo()
    {
        var (appId, apiKey) = await CreateAppWithApiKeyAsync("SinkSeverityTestApp");
        var baseAddress = _factory.CreateClient().BaseAddress!.ToString();
        using var logger = new LoggerConfiguration()
            .WriteTo.LogsPlatform(apiKey, baseAddress, "Production")
            .CreateLogger();

        logger.Information("sink severity test message");
        logger.Dispose();

        await TestPolling.WaitForCountAsync(
            () => QueryAsync(context => context.Events.CountAsync(e => e.ApplicationId == appId)),
            expected: 1, timeout: TimeSpan.FromSeconds(3));

        var stored = await QueryAsync(context => context.Events.SingleOrDefaultAsync(e => e.ApplicationId == appId));
        Assert.Equal(9, stored!.Severity);
    }

    [Fact]
    public async Task LogsPlatform_LogContextCorrelationId_IsSentThrough()
    {
        var (appId, apiKey) = await CreateAppWithApiKeyAsync("SinkPropertyTestApp");
        var baseAddress = _factory.CreateClient().BaseAddress!.ToString();
        using var logger = new LoggerConfiguration()
            .WriteTo.LogsPlatform(apiKey, baseAddress, "Production")
            .CreateLogger();

        using (LogContext.PushProperty("CorrelationId", "corr-abc-123"))
        {
            logger.Error("correlated failure");
        }
        logger.Dispose();

        await TestPolling.WaitForCountAsync(
            () => QueryAsync(context => context.Events.CountAsync(e => e.ApplicationId == appId)),
            expected: 1, timeout: TimeSpan.FromSeconds(3));

        var stored = await QueryAsync(context => context.Events.SingleOrDefaultAsync(e => e.ApplicationId == appId));
        Assert.Equal("corr-abc-123", stored!.CorrelationId);
    }

    [Fact]
    public async Task LogsPlatform_LoggedException_StoresRawStackTraceNotToString()
    {
        var (appId, apiKey) = await CreateAppWithApiKeyAsync("SinkExceptionTestApp");
        var baseAddress = _factory.CreateClient().BaseAddress!.ToString();
        using var logger = new LoggerConfiguration()
            .WriteTo.LogsPlatform(apiKey, baseAddress, "Production")
            .CreateLogger();

        Exception caught;
        try
        {
            throw new TimeoutException("operation timed out");
        }
        catch (Exception ex)
        {
            caught = ex;
        }
        logger.Error(caught, "operation failed");
        logger.Dispose();

        await TestPolling.WaitForCountAsync(
            () => QueryAsync(context => context.Events.CountAsync(e => e.ApplicationId == appId)),
            expected: 1, timeout: TimeSpan.FromSeconds(3));

        var stored = await QueryAsync(context => context.Events.SingleOrDefaultAsync(e => e.ApplicationId == appId));
        Assert.Equal(caught.StackTrace, stored!.StackTrace);
        Assert.DoesNotContain("operation timed out", stored.StackTrace);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LogsPlatform.Tests --filter LogsPlatformSinkTests`
Expected: FAIL — compile error, `LogsPlatformSinkExtensions`/`.LogsPlatform(...)` do not exist.

- [ ] **Step 3: Implement `LogsPlatformSink.cs`**

`src/LogsPlatform.Client/Serilog/LogsPlatformSink.cs`:

```csharp
using Serilog.Core;
using Serilog.Events;

namespace LogsPlatform.Client.Serilog;

public sealed class LogsPlatformSink : ILogEventSink, IDisposable
{
    private static readonly Dictionary<LogEventLevel, string> SeverityMap = new()
    {
        [LogEventLevel.Verbose] = "Trace",
        [LogEventLevel.Debug] = "Debug",
        [LogEventLevel.Information] = "Info",
        [LogEventLevel.Warning] = "Warn",
        [LogEventLevel.Error] = "Error",
        [LogEventLevel.Fatal] = "Fatal",
    };

    private readonly ILogsPlatformClient _client;
    private readonly string _environment;

    public LogsPlatformSink(ILogsPlatformClient client, string environment)
    {
        _client = client;
        _environment = environment;
    }

    public void Emit(LogEvent logEvent)
    {
        var payload = new EventPayload(
            EventKey: null,
            Timestamp: logEvent.Timestamp.UtcDateTime,
            Severity: SeverityMap[logEvent.Level],
            Environment: _environment,
            Version: null,
            Hierarchy: BuildHierarchy(logEvent),
            CorrelationId: GetProperty(logEvent, "CorrelationId"),
            TraceId: null,
            SpanId: null,
            ParentSpanId: null,
            DurationMs: null,
            CustomerId: GetProperty(logEvent, "CustomerId"),
            UserId: null,
            Message: logEvent.RenderMessage(),
            MessageTemplate: logEvent.MessageTemplate.Text,
            Exception: BuildException(logEvent),
            Metadata: null);

        _ = _client.SendEventAsync(payload);
    }

    public void Dispose()
    {
        _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static string? GetProperty(LogEvent logEvent, string name)
    {
        if (logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar)
        {
            return scalar.Value?.ToString();
        }
        return null;
    }

    private static IngestHierarchyPayload? BuildHierarchy(LogEvent logEvent)
    {
        var module = GetProperty(logEvent, "Module");
        var screenService = GetProperty(logEvent, "ScreenService");
        var process = GetProperty(logEvent, "Process");
        var operation = GetProperty(logEvent, "Operation");

        if (module is null && screenService is null && process is null && operation is null)
        {
            return null;
        }

        return new IngestHierarchyPayload(module, screenService, process, operation);
    }

    private static IngestExceptionPayload? BuildException(LogEvent logEvent)
    {
        if (logEvent.Exception is null)
        {
            return null;
        }

        return new IngestExceptionPayload(
            logEvent.Exception.GetType().FullName ?? logEvent.Exception.GetType().Name,
            logEvent.Exception.StackTrace);
    }
}
```

- [ ] **Step 4: Implement `LogsPlatformSinkExtensions.cs`**

`src/LogsPlatform.Client/Serilog/LogsPlatformSinkExtensions.cs`:

```csharp
using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace LogsPlatform.Client.Serilog;

public static class LogsPlatformSinkExtensions
{
    public static LoggerConfiguration LogsPlatform(
        this LoggerSinkConfiguration sinkConfiguration,
        string apiKey,
        string baseUrl,
        string environment,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(environment);

        var client = new LogsPlatformClient(baseUrl, apiKey);
        var sink = new LogsPlatformSink(client, environment);
        return sinkConfiguration.Sink(sink, restrictedToMinimumLevel);
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/LogsPlatform.Tests --filter LogsPlatformSinkTests`
Expected: PASS — 3/3 tests.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: all tests pass (209 pre-existing from before M2b, plus the 2 + 5 + 3 = 10 new tests from Tasks 2-4).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Client/Serilog/LogsPlatformSink.cs src/LogsPlatform.Client/Serilog/LogsPlatformSinkExtensions.cs tests/LogsPlatform.Tests/Client/LogsPlatformSinkTests.cs
git commit -m "Add Serilog sink for LogsPlatform.Client"
```

---

## Self-Review Notes

**Spec coverage:** Standalone project with no Domain/Infrastructure/Web references (Task 1). Shared batching engine serving both usage modes (Task 3, consumed directly and by Task 4's sink — one implementation, not two). `ILogsPlatformClient`/`FlushAsync`/`IAsyncDisposable` (Task 3). Serilog sink as thin translator, explicit severity mapping, `LogContext` property extraction, raw-`StackTrace`-not-`ToString()` (Task 4). Real-DB-persistence testing posture throughout, applying M2a's own final-review lesson. All design doc sections are covered; no gaps found.

**Placeholder scan:** No TBD/TODO markers; every step has complete, runnable code.

**Type consistency:** `EventPayload`'s field names and types (Task 2) are used identically in `LogsPlatformClient.FlushBatchAsync`'s `PostAsJsonAsync<List<EventPayload>>` call (Task 3) and in `LogsPlatformSink.Emit`'s payload construction (Task 4) — checked by re-reading each usage against the Task 2 declaration.
