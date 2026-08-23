# M4b: Findings API + Lifecycle Actions + "What's Unusual" UI — Design

**חלק מ:** R&D Logs Platform — Milestone M4 (Analysis Engine), second half
**תאריך:** 2026-08-23
**מבוסס על:** [07-Ingestion-ו-API.md](../../../מסמכי-אפיון/07-Ingestion-ו-API.md) §4 (Findings API), [09-UI-Design.md](../../../מסמכי-אפיון/09-UI-Design.md) §3 (What's Unusual), [08-Analysis-ו-Anomaly-Detection.md](../../../מסמכי-אפיון/08-Analysis-ו-Anomaly-Detection.md) §1 (Conclusion-writing principle)
**Follows:** M4a (Analysis Engine core), merged to `main` 2026-08-23, 267/267 tests passing.

## 1. Goal

M4a produces `Finding` rows with `Fact`/`Observation`/`Hypothesis` statements and `Evidence`, but nothing outside the engine can read or act on them yet. M4b closes that loop: a Findings API to read and mutate Findings, and the "What's Unusual" screen the project's whole value proposition is built around — the system leads with an answer instead of the user starting from a search.

## 2. Scope decisions locked before this design started

- **Promote-to-Conclusion approval identity:** free-text "approved by" note, not a real user-identity system (no auth/login exists yet — confirmed absent from the codebase). `FindingStatement.ApprovedBy`/`ApprovedAt` already exist on the entity from M4a for exactly this.
- **Home page:** "What's Unusual" (the Findings list) replaces `Home.razor`'s current nav-hub content at `/`, matching the original UI spec's intent ("the system starts with an answer"). `NavMenu.razor` already has a disabled "מה חריג" nav item and a working "בית" item pointing at `/` — these collapse into one active "מה חריג" item once this ships.
- **`DownstreamFailureCorrelator` wiring:** called inline by the detector that found the anomaly (not driven uniformly from `AnalysisEngineTickRunner` the way `DeploymentCorrelator` is), since only the detector has the triggering event's `CorrelationId`/`OperationId`/`Timestamp` in scope.
- **ErrorSpike trigger event:** `RateAnomalyDetector` has no single triggering event (it works off aggregate hourly counts) — on an ErrorSpike write, it queries one representative Event (most recent with non-null `CorrelationId`) from the spike hour to use as the correlator's trigger.
- **Status transitions:** free — `PATCH .../status` accepts any of the 4 `FindingStatus` values with no state-machine validation. No stated requirement for stricter workflow; simplest option matching the rest of this admin tool.
- **Lifecycle auth:** unauthenticated, consistent with the rest of the Admin UI (no login system exists in this project yet).

## 3. Repository layer

`IFindingRepository` (already has `FindOpenAsync`, `AddAsync`, `AddStatementAsync`, `AddEvidenceAsync`, `GetByIdAsync`, `GetDetectedSinceAsync`) gains:

```csharp
Task<IReadOnlyList<Finding>> QueryAsync(FindingQueryParameters parameters);
Task<Finding?> UpdateStatusAsync(long findingId, FindingStatus status);
Task<FindingStatement?> PromoteToConclusionAsync(long findingId, long statementId, string approvedBy);
```

`FindingQueryParameters(int ApplicationId, int EnvironmentId, FindingStatus? Status, FindingSeverity? Severity, FindingType? Type, DateTime? From, DateTime? To)` — `ApplicationId`+`EnvironmentId` required, matching the established convention from `EventQueryParameters`/`ExceptionGroupQueryParameters`. Results ordered by `Severity` descending then `DetectedAt` descending, per the UI spec's default sort.

`UpdateStatusAsync` returns `null` if the Finding doesn't exist (controller maps to 404); otherwise sets `Status` and returns the updated row.

`PromoteToConclusionAsync` returns `null` if the Finding or the statement (scoped to that Finding) doesn't exist, or if the statement's current `Kind` isn't `Hypothesis` (a statement can only be promoted once — attempting it again on an already-`Conclusion` statement is rejected, not silently re-approved). On success: sets `Kind = FindingStatementKind.Conclusion`, `ApprovedBy = approvedBy`, `ApprovedAt = DateTime.UtcNow`, returns the updated statement.

## 4. Findings API (`FindingsController`)

Mirrors `ExceptionGroupsController`'s shape:

| Endpoint | Behavior |
|---|---|
| `GET /api/v1/findings` | Query params: `applicationId`, `environmentId` (required), `status`, `severity`, `type`, `from`, `to`. Returns `List<FindingSummary>`. |
| `GET /api/v1/findings/{id}` | Returns `FindingDetail` (Finding + ordered Statements with Kind + Evidence). 404 if not found. |
| `PATCH /api/v1/findings/{id}/status` | Body `{"status": "Acknowledged"}`. 404 if not found, 400 on an unparseable status string. |
| `POST /api/v1/findings/{id}/statements/{statementId}/promote` | Body `{"approvedBy": "..."}`. 404 if Finding/statement not found or statement doesn't belong to that Finding, 400 if `approvedBy` is blank or the statement isn't currently `Hypothesis`. |

Contracts (new records in `QueryContracts.cs`, following the existing `EventSummary`/`ExceptionGroupSummary` naming pattern):

```csharp
public record FindingSummary(long Id, string Type, string Title, string Severity, string ConfidenceLevel, string Status, DateTime DetectedAt, string ApplicationName, string? OperationName);
public record FindingStatementDto(long Id, string Kind, string Text, int OrderIndex, string? ApprovedBy, DateTime? ApprovedAt);
public record EvidenceDto(long Id, string EvidenceType, long ReferenceId, string Description);
public record FindingDetail(long Id, string Type, string Title, string Severity, string ConfidenceLevel, string Status, DateTime DetectedAt, string ApplicationName, string EnvironmentName, IReadOnlyList<FindingStatementDto> Statements, IReadOnlyList<EvidenceDto> Evidence);
```

`FindingSummary.OperationName`/`FindingDetail`'s scope display resolve `ScopeType`/`ScopeId` to a human name where `ScopeType == Operation` (via `IOperationRepository`); for `ScopeType == ExceptionGroup` the Finding's own `Title` already names the exception type, so no extra lookup is needed there.

## 5. Detector wiring for `DownstreamFailureCorrelator`

Both detectors below take a constructor-injected `DownstreamFailureCorrelator` (a new 4th/5th constructor parameter — both are already `Scoped`, and `DownstreamFailureCorrelator` is `Scoped` too, so no DI-lifetime concern like M4a's `BackgroundService` bug).

- **`NewExceptionDetector`**: already iterates individual `Event` rows per newly-first-seen `ExceptionGroup`. After `_writer.WriteAsync(draft)` returns the `Finding`, if the triggering event has a non-null `CorrelationId`, call `_downstreamCorrelator.RunAsync(finding, correlationId, operationId, timestamp)` using that event's own `CorrelationId`/`OperationId`/`Timestamp`. If `OperationId` is null (event has no hierarchy resolution), skip the correlator call — nothing to correlate downstream operations against.
- **`RateAnomalyDetector`**: only on an `ErrorSpike` write (not `MissingActivity`/`PerformanceDegradation` — a spike is the one case implying "something failed and may have cascaded"), query the DB directly (`LogsPlatformDbContext`, injected alongside the existing repositories — matches `NewExceptionDetector`'s own precedent of taking the context directly for a query shape not worth adding to `IMetricsRepository`) for the most recent `Event` in that Operation's spike hour with a non-null `CorrelationId`. If found, call the correlator the same way `NewExceptionDetector` does.
- `AnalysisEngineTickRunner` is unchanged — `DownstreamFailureCorrelator` is no longer its concern; only `DeploymentCorrelator` remains driven uniformly per-tick over `GetDetectedSinceAsync`. `Program.cs`'s existing `AddScoped<DownstreamFailureCorrelator>()` registration (from M4a) is reused as-is; only the two detectors' registrations are unaffected since DI resolves the new constructor parameter automatically.

## 6. UI

**"What's Unusual" (`Home.razor`, still routed at `/`):** Replaces the nav-hub cards with the Findings list. Uses the existing `AppEnvironmentSelector` component for the always-visible Application+Environment picker (same pattern as `Search.razor`/`Exceptions.razor`). Filters: Severity, Confidence, Status, Type, date range — mapped directly to `GET /api/v1/findings`. Each row: type icon (text label is fine — no icon library established yet in this project, defer bespoke icons), Severity badge, Confidence badge, title, Application/Operation, DetectedAt, Status. Default sort Severity-then-DetectedAt-desc (server-side, from the API). Empty state: "לא נמצאו חריגות בטווח הזמן/הסינון הנוכחי" (not an error state).

**Finding detail (`/findings/{id}`, new page):**
1. Title + Severity/Confidence/Status badges.
2. Statements rendered by `Kind`, styled per the spec's fixed scheme: `Fact` neutral gray/dark-blue, `Observation` blue with a chart icon, `Hypothesis` amber with a question icon and a fixed "טרם אושר" (not yet approved) label — never rendered in a way that reads as a final conclusion, `Conclusion` green with "אושר ע"י {ApprovedBy} ב-{ApprovedAt:g}".
3. Evidence list: each item is a description plus a navigation link where one exists — `Event` → `/search` filtered to that event's context (best-effort: filter by the event's CorrelationId if the description surfaces it, else link to `/search` unfiltered — M4a's `Evidence.Description` doesn't currently carry enough structured data for a precise filter; noted as a known limitation, not solved by this plan), `ExceptionGroup` → `/exceptions/{referenceId}`, `Deployment` → `/admin/applications` (deployments live under an application's Admin tab, not a standalone route — links to the app's admin page since no deployment-specific deep link exists). `Baseline`/`Operation` evidence render as plain text with no link (no dedicated detail page exists for either).
4. Actions: Acknowledge / Resolve / Dismiss buttons (each a `PATCH .../status` call, page refreshes the Finding after). A "קדם ל-Conclusion" button appears next to each `Hypothesis` statement specifically (not as a page-level action) — opens a small inline form requiring a non-empty approval note before submitting `POST .../promote`.
5. Drill-down buttons matching the Finding's `ScopeType`: "צפה ב-Timeline" and "צפה באירועים המקוריים" for `Operation`-scoped Findings (link to `/search`/`/timeline` filtered by the scope's Operation), "צפה בקבוצת ה-Exception" for `ExceptionGroup`-scoped Findings (link to `/exceptions/{ScopeId}`).

**`NavMenu.razor`**: the disabled "מה חריג" item becomes a live `NavLink` to `/` (`NavLinkMatch.All`), and the separate "בית" item is removed — they were always going to be the same destination once this shipped.

## 7. Testing approach

Same TDD/real-SQL-Server pattern as every prior plan: `IFindingRepository`'s 3 new methods tested against a real seeded DB (no mocking); `FindingsController` tested via `WebApplicationFactory`, including a real read-back through a fresh `DbContext` per this project's established "verify the write actually happened, not just the response shape" convention (from M2a's/M3's lessons); `RateAnomalyDetector`/`NewExceptionDetector`'s new correlator-calling branches tested by asserting a Hypothesis statement + Event evidence appear on the resulting Finding, matching `DownstreamFailureCorrelatorTests`' own assertion shape from M4a. UI pages are not covered by automated tests (this project's established convention — Blazor pages are verified via live manual browser/curl checks, not component tests), but this plan's finish should include a live walkthrough: trigger a real ErrorSpike/NewException via the ingestion API, confirm it appears on `/`, open its detail page, Acknowledge it, and promote a Hypothesis to Conclusion end-to-end.

## 8. Out of scope for M4b

RBAC / real user identity for approvals (deferred to a future Security milestone per `09-UI-Design.md` §10). A dedicated Deployment detail page (evidence links to the Admin page instead). Precise Event-evidence deep-linking into Search (falls back to an unfiltered Search link — `Evidence.Description` doesn't carry structured filter data yet).
