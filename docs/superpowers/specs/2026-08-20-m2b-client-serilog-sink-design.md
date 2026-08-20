# M2b: LogsPlatform.Client + Serilog Sink — Design

**Status:** Approved by user, ready for implementation planning
**Part of:** M2 (Ingestion), the second of two plans — M2a (Ingestion API core, fully merged) built the server side; M2b builds the consumer-facing client library that talks to it. Together they complete M2's full milestone scope per `07-Ingestion-ו-API.md` §7 and `12-תוכנית-עבודה-ואבני-דרך.md`'s M2 row.

## Goal

A standalone, NuGet-able `LogsPlatform.Client` project with two usage modes sharing one batching engine:
1. **Direct** — `ILogsPlatformClient.SendEventAsync(...)` for full control.
2. **Serilog sink** — `.WriteTo.LogsPlatform(apiKey, baseUrl, environment: "Production")`, mapping `LogContext` properties to the hierarchy/correlation fields M2a's ingestion API expects.

Both batch events client-side (every 2 seconds or 100 events, whichever first) rather than one HTTP call per log line, and neither can ever let an ingestion failure propagate into the consuming application — this is the single governing principle of the whole plan, stated explicitly in `07` §7: "תקלה ב-ingestion לעולם לא מפילה את אפליקציית הלקוח" (an ingestion failure never crashes the client application).

## Why a Separate, Standalone Project

`LogsPlatform.Client` does not exist yet — only `Domain`/`Infrastructure`/`Web`/`Tests` were scaffolded in M0. This plan creates it. It must NOT reference `LogsPlatform.Domain` or `LogsPlatform.Infrastructure` (no `ProjectReference` to either) — a real external consumer installing this as a NuGet package should not be forced to pull in EF Core, SQL Server, or this project's internal domain model. `LogsPlatform.Web`'s ingestion contract (`IngestEventRequest` etc., in `LogsPlatform.Web.Contracts`) is likewise not referenced — `LogsPlatform.Client` defines its own wire-format DTOs, serialized to the exact same JSON shape M2a's controller expects, independently. This is the standard shape for a client SDK: it depends only on the wire contract (a JSON shape, documented in `07` §2), never on the server's internal C# types.

## One Batching Engine, Not Two

`07` §7 describes the CLIENT's behavior as a whole ("the client accumulates events and sends in batch") — not a Serilog-sink-specific behavior. Both usage modes route through the same core `LogsPlatformClient`; the Serilog sink is a thin translator on top of it (`LogEvent` → wire DTO → `client.SendEventAsync(...)`), not a second, independent batching implementation.

**Batching engine design — deliberately simple, not a hand-rolled `Channel`/async-loop:** a `lock`-protected `List<EventPayload>` buffer plus a `System.Threading.Timer`. `SendEventAsync` appends under the lock; if the buffer just reached the batch-size limit (default 100), it's swapped out and flushed. The timer fires every `Period` (default 2 seconds) and flushes whatever's accumulated, even a partial batch. Buffer overflow (producer faster than the flush cadence) drops the OLDEST entry, per `07`'s explicit "buffer מוגבל בגודל, drop-oldest בגלישה." This was considered against `System.Threading.Channels.Channel<T>` (which has a built-in `BoundedChannelFullMode.DropOldest`) and against `Serilog.Sinks.PeriodicBatching`'s `PeriodicBatchingSink` (a well-tested library primitive, but tightly coupled to Serilog's own `LogEvent` type, making it awkward to share between the direct-client and Serilog-sink paths) — the lock+`List`+`Timer` approach was chosen because it's the easiest to verify correct by direct code reading for a solo V1 project at synthetic-data scale, and it naturally serves both usage modes through one implementation. `FlushAsync()`'s actual HTTP send is wrapped in a try/catch that can never propagate — any failure is written to `Console.Error` (not fed back into the same logging pipeline, which would risk an infinite loop if the Serilog sink itself is what failed) and the batch is simply dropped, not retried or re-queued.

**Package dependency:** only `Serilog` (core, `4.4.0`) is added — for `ILogEventSink`, `LogEvent`, `LoggerSinkConfiguration`. No `Serilog.Sinks.PeriodicBatching` needed, since the batching engine lives in the core client, not the sink.

## Core Client

```csharp
public interface ILogsPlatformClient : IAsyncDisposable
{
    Task SendEventAsync(EventPayload evt);
    Task FlushAsync();
}
```

`SendEventAsync` enqueues under the lock and returns once queued — it does not wait on network I/O, matching "not one HTTP call per log line." `FlushAsync()` is a public, explicit, *awaited* drain-and-send (used both internally by the timer/size triggers via fire-and-forget, and externally by a caller wanting a deterministic flush before shutdown — e.g. `IAsyncDisposable.DisposeAsync()` calls it). Constructor: `LogsPlatformClient(string baseUrl, string apiKey, HttpClient? httpClient = null, int batchSize = 100, TimeSpan? period = null /* default 2s */, int queueLimit = 10_000)` — an optional injected `HttpClient` for testability/connection-pool reuse; if none is provided, the client creates and owns one internally (disposed alongside the client).

`EventPayload` is the client-facing event shape — a plain record mirroring every field M2a's `IngestEventRequest` accepts (`eventKey`, `timestamp`, `severity`, `environment`, `version`, `hierarchy` (module/screenService/process/operation), `correlationId`, `traceId`, `spanId`, `parentSpanId`, `durationMs`, `customerId`, `userId`, `message`, `messageTemplate`, `exception` (type/stackTrace), `metadata`) — this is `LogsPlatform.Client`'s OWN type, not shared with `LogsPlatform.Web.Contracts`.

## Serilog Sink

```csharp
public static class LogsPlatformSinkExtensions
{
    public static LoggerConfiguration LogsPlatform(
        this LoggerSinkConfiguration sinkConfiguration,
        string apiKey, string baseUrl, string environment,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose)
}
```

`LogsPlatformSink : ILogEventSink` wraps an internal `LogsPlatformClient` and, per `Emit(LogEvent)`: maps `LogEventLevel` → the wire severity string via an explicit lookup table (`Verbose→Trace, Debug→Debug, Information→Info, Warning→Warn, Error→Error, Fatal→Fatal`). This mapping is NOT a pass-through of `LogEventLevel.ToString()` — only 3 of the 6 Serilog names (`Debug`, `Error`, `Fatal`) match M2a's severity vocabulary textually; `Verbose`, `Information`, and `Warning` do not, and `IngestionProcessor` rejects any event whose `severity` doesn't parse against its fixed map (`Trace|Debug|Info|Warn|Error|Fatal`). Without the explicit table, most real-world Information/Warning-level logs would be silently rejected by the ingestion endpoint's required-severity check — this is the exact mismatch M2a's own final review flagged when it reviewed the ingestion side and anticipated the client side. It pulls `Module`/`ScreenService`/`Process`/`Operation`/`CorrelationId`/`CustomerId` from `LogEvent.Properties` (set via Serilog's `LogContext.PushProperty(...)`) into the `hierarchy`/`correlationId`/`customerId` fields, sets `message`/`messageTemplate` from the `LogEvent`'s rendered message and raw template, and — critically, per M2a's own final-review finding — sets `exception.stackTrace` from `LogEvent.Exception.StackTrace` (the raw stack trace), **never** `LogEvent.Exception.ToString()` (which prepends the exception type and message, corrupting `ExceptionFingerprinter`'s top-3-frame signature and defeating grouping). Then calls `_client.SendEventAsync(payload)`.

## Testing

Same real-integration posture as every prior plan, but the specific lesson from M2a's own final review applies directly here: assert that events actually reach the database via a real hosted server, not just that the client call didn't throw. Tests host the real `LogsPlatform.Web` app via `TestWebApplicationFactory` (already established), point `LogsPlatformClient` at it with a real `HttpClient` obtained from the factory, send events through both the direct client and the Serilog sink, and read the database directly afterward (a fresh `LogsPlatformDbContext`, matching M2a's `LogsPlatformDbContextTests` pattern) to confirm the events landed with the right fields. Also test: batch-size-triggered flush (send 100+ events, confirm they arrive without waiting for the timer), timer-triggered flush (send fewer than 100, wait past the period, confirm they still arrive), buffer overflow drop-oldest (fill past `queueLimit`, confirm only the newest `queueLimit` events survive), and the non-crashing-on-failure guarantee (point the client at an unreachable URL, confirm `SendEventAsync`/the Serilog sink never throws).

## Non-Goals (explicit)

- No change to `LogsPlatform.Web`'s ingestion endpoint (M2a) — this plan is a pure consumer of that already-shipped API.
- No retry-with-backoff on failed HTTP sends — per `07`'s explicit design, a failed batch is dropped with a console warning, not retried (retrying risks compounding a real outage into a client-side backlog).
- No configuration-file-based setup (`appsettings.json` binding, etc.) — this is a library used programmatically (`new LogsPlatformClient(...)` or `.WriteTo.LogsPlatform(...)`), matching how every other Serilog sink is configured in code.
- No multi-language SDKs — explicitly out of V1 scope per the project's own characterization docs.
