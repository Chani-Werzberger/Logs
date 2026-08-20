# M2a: Ingestion API Core — Design

**Status:** Approved by user, ready for implementation planning
**Part of:** M2 (Ingestion), split into M2a (this plan — the ingestion API itself) and M2b (`LogsPlatform.Client` + Serilog sink, later). M1 (Application Model: Group A hierarchy + Group B1/B2/B3) is fully merged. M2a alone satisfies M2's stated milestone acceptance criterion: *"a test console sends a batch of events and they land in the DB, including one with a typo that doesn't drop the whole batch."*

## Goal

The one real ingestion endpoint, `POST /api/v1/ingest/events`: authenticates via API key, resolves each event's hierarchy path against already-admin-managed nodes (never auto-creating), groups exceptions by fingerprint, deduplicates via an idempotency key, rate-limits per key, and returns OTLP-style partial-success. No Query API, no UI, no Analysis Engine — those are M3/M4.

## Entities

Both already fully specified in `05-מודל-נתונים.md` — copied here verbatim, plus one resolved gap:

```
Event (
  Id bigint IDENTITY PK,
  Timestamp datetime2,
  Severity int,
  ApplicationId int → Application,
  EnvironmentId int → AppEnvironment,
  VersionId int NULL → AppVersion,
  ModuleId int NULL → AppModule,
  ScreenServiceId int NULL → ScreenService,
  ProcessId int NULL → ProcessNode,
  OperationId int NULL → Operation,
  CustomerId int NULL → Customer,
  AppUserId int NULL → AppUser,
  EventKey nvarchar(100) NULL,        -- NEW, see "Idempotency gap" below
  CorrelationId nvarchar(100) NULL,
  TraceId nvarchar(100) NULL,
  SpanId nvarchar(100) NULL,
  ParentSpanId nvarchar(100) NULL,
  DurationMs float NULL,
  Message nvarchar(max),
  MessageTemplate nvarchar(1000) NULL,
  ExceptionGroupId bigint NULL → ExceptionGroup,
  StackTrace nvarchar(max) NULL,
  MetadataJson nvarchar(max) NULL
)

ExceptionGroup (
  Id bigint IDENTITY PK,
  ApplicationId int → Application,
  Fingerprint nvarchar(200),
  ExceptionType nvarchar(500),
  MessageTemplate nvarchar(1000),
  RepresentativeStackTrace nvarchar(max),
  FirstSeenAt datetime2,
  LastSeenAt datetime2,
  OccurrenceCount int
)
```

**Naming:** no BCL-collision-avoidance prefix needed for either — `Event`/`ExceptionGroup` don't collide with anything commonly `using`'d in this codebase (C#'s `event` keyword is lowercase and contextual; it doesn't collide with a type named `Event`).

**Idempotency gap (confirmed, resolved the same way B1 resolved its `IsActive` gap):** `07-Ingestion-ו-API.md` §6 requires an optional client-supplied `eventKey` for retry-safe dedup, but `05`'s `Event` schema has no such column. **Resolved: add nullable `EventKey`, unique filtered index on `(ApplicationId, EventKey) WHERE EventKey IS NOT NULL`** — scoped per-application rather than globally: `eventKey` is client-supplied and nothing at the API contract level actually enforces it's a GUID (the spec recommends one, but a non-conforming client sending e.g. sequential ids could otherwise collide across two unrelated applications and silently drop a legitimate event from one of them). Per-application scoping costs nothing and removes that cross-tenant risk entirely, consistent with this project's existing Environment/Application isolation posture.

**Indexes** (per `05` §3's exact list): `(ApplicationId, EnvironmentId, Timestamp)`, `(ApplicationId, OperationId, Timestamp)`, `(CorrelationId)`, `(TraceId)`, `(ExceptionGroupId)`. Full-text index on `Message` is explicitly **out of scope for this plan** — full-text search is a Query API (M3) concern, not an ingestion concern; adding the index now with nothing to exercise it is premature.

## API Shape

```
POST /api/v1/ingest/events
X-Api-Key: <raw key>
Content-Type: application/json

[ { "eventKey": "...", "timestamp": "...", "severity": "Error", "environment": "Production",
    "version": "2.3.1", "hierarchy": { "module": "...", "screenService": "...", "process": "...", "operation": "..." },
    "correlationId": "...", "traceId": "...", "spanId": "...", "parentSpanId": null,
    "durationMs": 812.4, "customerId": "...", "userId": "...",
    "message": "...", "messageTemplate": "...",
    "exception": { "type": "...", "stackTrace": "..." },
    "metadata": { ... } } ]
```

Response, `202 Accepted` (per `07` §2, OTLP-inspired):
```json
{ "accepted": 9, "rejected": 1,
  "errors": [ { "index": 3, "reason": "severity: invalid value 'Critical'" } ],
  "hierarchyWarnings": [ { "index": 0, "field": "operation", "reason": "not found, event stored without operation reference" } ] }
```

The endpoint accepts a JSON array only, even for a single event (`[{...}]`, not a bare `{...}`) — the real `LogsPlatform.Client` (M2b) always batches internally per `07` §7, so single-object convenience-binding has no real consumer in V1; scoped out to avoid custom model-binding complexity for a case nothing needs yet.

## Auth: `X-Api-Key` → `Application`

New `ApiKeyAuthenticationHandler` (a `AuthenticationHandler<AuthenticationSchemeOptions>`, ASP.NET Core's standard extension point) reads `X-Api-Key`, hashes it with the already-merged `ApiKeyRepository`'s hash routine, and looks up the `ApiKey` by hash. **This requires one new repository method that B2's own final review flagged as necessary before M2 starts:**

```csharp
// added to IApiKeyRepository
Task<ApiKey?> GetByKeyHashAsync(string keyHash);
```

The hash function itself (currently `private static` inside `ApiKeyRepository`) is promoted to a shared, public static location (`ApiKeyRepository.HashKey(string rawKey)` or a small static helper class) so the auth handler and the repository compute byte-identical hashes from one source of truth — exactly the risk B2's review called out ("the three ways to get it wrong are all silent: hashing without the prefix, a different encoding, or lowercase hex").

A revoked key (`RevokedAt` non-null) or unrecognized key → `401 Unauthorized`, RFC 7807 error body, entire request rejected (no partial-success at the auth layer — you don't get to submit *some* events without valid credentials). A valid key resolves the `Application` for every event in the batch; **no `applicationId` field exists in the request body** (per `07` §2 — the whole point of the API key is that the client doesn't get to assert which application it's writing to).

## Hierarchy Resolution

New `HierarchyResolver` service (`src/LogsPlatform.Web/Services/`, same layer as the existing `BreadcrumbBuilder`), one method:

```csharp
public record HierarchyResolutionResult(int? ModuleId, int? ScreenServiceId, int? ProcessId, int? OperationId);

public async Task<(HierarchyResolutionResult Result, string? WarningField)> ResolveAsync(
    int applicationId, string? module, string? screenService, string? process, string? operation)
```

Resolves the chain module→screenService→process→operation by **name**, one layer at a time, each scoped to its parent's already-resolved id. Uses the existing repositories' `GetByApplicationIdAsync`/`GetByModuleIdAsync`/`GetByScreenServiceIdAsync`/`GetByProcessIdAsync` (all already `includeInactive: false` by default — a typo that happens to match a *deactivated* node correctly resolves as not-found, not as a live reference) and filters by name in memory. No new lookup-by-name repository methods needed — hierarchy tables are low-volume admin metadata, not `Event`-scale, so fetching the parent-scoped list and filtering client-side is the right cost/complexity trade here, and it touches zero already-shipped repository interfaces.

**First unresolvable layer stops the chain**: if `module` doesn't resolve, `screenService`/`process`/`operation` are never even looked up (they're meaningless without a resolved parent) — result is `ModuleId: null` and everything past it `null`, one `hierarchyWarning` naming the first failing field. If `module` resolves but `screenService` doesn't, `ModuleId` is set, everything from `ScreenServiceId` down is `null`, one warning naming `screenService`. Never auto-creates a node under any circumstance — this is the literal point of `07` §3's design decision, restated for M2a's own implementer since it's easy to "helpfully" add auto-create when handling a not-found case.

## Validation & Partial-Success

Per event, in order:
1. **Required-field check** (`timestamp`, `severity`, `message`) — missing any of these rejects the *event*, adds one `errors[]` entry, does not touch the DB for that event, and does not fail the batch.
2. **Severity parse** — must be one of `Trace|Debug|Info|Warn|Error|Fatal` (case-sensitive per the wire format in `07`'s example); unrecognized value rejects the event the same way. Maps to `05`'s numeric ranges via the first value of each range (`Trace=1, Debug=5, Info=9, Warn=13, Error=17, Fatal=21`) — the exact mid-range value doesn't matter for V1 since nothing yet buckets *within* a severity band, only *by* band.
3. **Hierarchy resolution** (see above) — never rejects the event, only adds `hierarchyWarnings[]` entries.
4. **Exception grouping** (see below) — never rejects the event.
5. **Idempotency check** (see below) — a duplicate `eventKey` is **not** an error; per `07`'s stated reasoning ("a network retry is a realistic scenario"), it's silently treated as already-accepted (counted in `accepted`, no DB write, no error entry) — this is what makes it retry-safe rather than retry-*visible*.

## Exception Grouping

If the event carries an `exception` object: compute `Fingerprint = Convert.ToHexString(SHA256.HashData(UTF8(ExceptionType + "|" + NormalizedStackSignature + "|" + MessageTemplate)))`, where `NormalizedStackSignature` is the exception type + method names of the top 3 stack frames, with line numbers stripped (line numbers shift across builds of the same logical bug; type+method name across the top few frames is a stable-enough signature for V1's stated non-ML fingerprinting approach). Look up `ExceptionGroup` by `(ApplicationId, Fingerprint)`; if none exists, create one (`FirstSeenAt = LastSeenAt = event timestamp, OccurrenceCount = 1`); if one exists, **do not touch it** — `OccurrenceCount`/`LastSeenAt` are explicitly reserved for M4's Analysis Engine batch job to reconcile (`05` §4: "not updated on every insert, avoids row contention at high write volume"). Set `Event.ExceptionGroupId` to the (found-or-created) group's id either way.

## Idempotency

`AddEventsAsync` (see Repository below) checks, in one query per batch, which of the batch's non-null `eventKey`s already exist for this `ApplicationId` before inserting; matches are silently counted as accepted and skipped, not re-inserted.

## Rate Limiting

Simple in-memory, per-API-key fixed-window counter (`IMemoryCache`, already part of the ASP.NET Core stack, no new package) — V1 runs single-instance and local-only (`rnd_logs_platform_v1_design` memory: "runs locally only in V1"), so there's no multi-instance consistency problem a distributed store would be solving. Default: 1000 events/minute per key (`07` §6's own example number), configurable via `appsettings`. Breach → `429 Too Many Requests`, `Retry-After` header, RFC 7807 body, whole request rejected (not partial — rate limiting is a batch-level concern, not a per-event one).

## Repository

```csharp
public interface IEventRepository
{
    Task<IngestResult> AddEventsAsync(IReadOnlyList<Event> events);
}

public record IngestResult(int Accepted, int DuplicateEventKeysSkipped);
```

Deliberately **not** shaped like every prior CRUD repository — `Event` has no `GetById`/`Rename`/`Deactivate` in this plan's scope. Events are immutable, append-only log records; nothing in M2a reads them back (that's M3's Query API). The repository's only job is "insert what's genuinely new, tell the caller how many that was."

`IExceptionGroupRepository.GetOrCreateAsync(int applicationId, string fingerprint, string exceptionType, string messageTemplate, string representativeStackTrace, DateTime seenAt)` — the one non-trivial method, returning the existing or newly-created group's id.

Both wrap `SaveChangesAsync` in the established detach-on-failure pattern for their write paths, from the first draft (per this project's now-consistent Global Constraints practice).

## Testing

Same real-integration-test posture as every prior plan (LocalDB, `TestWebApplicationFactory`), but the shape of the tests is different from any prior plan's: this is the first plan whose primary surface is a single bulk-write endpoint with conditional per-item outcomes, not per-entity CRUD. Coverage needed: happy-path batch (all valid), partial-success (one bad required field among several good events, verify `accepted`/`rejected` counts and the DB only has the valid ones), hierarchy-typo-doesn't-drop-event (verify the event lands with a null FK + a warning, not rejected), idempotent-retry (send the same `eventKey` twice, verify only one row, second call still returns `202` with it counted in `accepted`), auth-rejection (missing/wrong/revoked key → `401`, verify zero DB writes), exception-grouping (two events with the same fingerprint → one `ExceptionGroup` row, `OccurrenceCount` still `1` after the second — proving M2a correctly does NOT increment it), rate-limit breach (`429` after exceeding the configured threshold in a test-scoped low limit).

## Non-Goals (explicit)

- **Query API** (`GET /api/v1/events`, `/timeline`, `/exception-groups`) — M3.
- **Analysis Engine** (`OccurrenceCount`/`LastSeenAt` reconciliation, Baseline, Finding creation) — M4.
- **`LogsPlatform.Client` + Serilog sink** — M2b, a separate plan; M2a's own test coverage uses a raw `HttpClient` against the API directly, exactly like every controller test in this project already does.
- **Full-text index on `Message`** — deferred to whenever M3's Query API actually needs it.
- No change to any already-shipped M1 code (Group A, B1, B2, B3) beyond the one new `IApiKeyRepository.GetByKeyHashAsync` method and hash-function promotion described above.
