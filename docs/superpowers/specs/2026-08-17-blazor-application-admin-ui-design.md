# Blazor Application Admin UI — Design

**Status:** Approved by user, ready for implementation planning
**Part of:** M1 (Application Model) — completes the "minimal Blazor Server admin UI" tail-end item noted in the Application Model Foundation plan's "Next Plans" section.

## Goal

A single Blazor Server page, added to the existing `LogsPlatform.Web` project, that lets a user view all `Application`s, create a new one, and view/create `AppEnvironment`s for any application — using the exact same data (and data rules) as the existing JSON API, with no new backend capability. This is the first UI in the product; everything before this has been API/Swagger-only.

## Non-Goals (explicit, to prevent scope creep)

- No authentication/authorization — the underlying API has none yet (Security Design defers it), and adding UI-only auth ahead of the API would be backwards.
- No update/delete for Applications or Environments — the API doesn't support them yet either.
- No custom visual design/CSS — plain HTML tables and forms, matching "basic" as explicitly requested.
- No coverage of any entity beyond `Application`/`AppEnvironment` — the rest of the hierarchy (Module/ScreenService/ProcessNode/Operation/etc.) doesn't exist in the backend yet.

## Architecture

### Data access: direct repository injection, not HTTP

Blazor Server components render server-side, in the same process as the API controllers (per the Modular Monolith architecture, `04-ארכיטקטורה.md`). The page injects `IApplicationRepository` and `IAppEnvironmentRepository` directly via `@inject`, calling them exactly as `ApplicationsController`/`EnvironmentsController` do. It does **not** make HTTP calls to its own API — that would add serialization overhead and self-referencing-URL configuration for zero benefit when everything is already in-process.

**Trade-off this creates, accepted deliberately:** `ApplicationsController.Create`'s duplicate-`Name` → `409 Conflict` handling (Task 8) lives in the controller, not the repository. Calling the repository directly bypasses it. The Blazor page therefore needs its **own**, small, duplicated `try/catch` around `AddAsync` for the same `SqlException { Number: 2601 or 2627 }` case, showing an inline error message instead of an HTTP status code. This is a second occurrence of logic the final whole-branch review already flagged as worth centralizing eventually (a shared service or exception-mapping layer) — not centralizing it now, so it's noted here explicitly as a deferred cost, not a hidden one.

### New ASP.NET Core hosting pieces

`LogsPlatform.Web` currently only hosts API controllers (`AddControllers()`/`MapControllers()` in `Program.cs`) — no Razor Components infrastructure exists yet. This task adds:
- `builder.Services.AddRazorComponents().AddInteractiveServerComponents();`
- `app.MapRazorComponents<App>().AddInteractiveServerRenderMode();`
- Root components: `Components/App.razor` (HTML shell), `Components/Routes.razor` (router outlet), `Components/_Imports.razor` (shared usings)

This is standard, current (.NET 8+) "Blazor Web App" hosting added to an existing API project — a well-supported combination, not a workaround.

## The Page: `/admin/applications`

**File:** `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor`

**Layout, top to bottom:**

1. **Create Application form** — two inputs (`Name` required, `Description` optional) + submit button. On submit: calls `IApplicationRepository.AddAsync`, wrapped in the try/catch described above. Success clears the form and refreshes the list below. Failure (duplicate name) shows an inline message near the form, form values preserved so the user can edit and retry.

2. **Applications table** — one row per `Application` (`Name`, `Description`, `CreatedAt`, an expand/collapse toggle). Loaded once on page init via `GetAllAsync()`.

3. **Per-row expansion (lazy)** — clicking a row's toggle calls `IAppEnvironmentRepository.GetByApplicationIdAsync(appId)` **only the first time that row is expanded** (result cached in component state after that), then renders:
   - A nested table of that application's `AppEnvironment`s (`Name`, `IsProduction`).
   - A small inline "Add Environment" form (`Name` required, `IsProduction` checkbox) scoped to that row. Submit calls `IAppEnvironmentRepository.AddAsync` with the row's `appId`, then refreshes just that row's environment list.

**State model:** a `Dictionary<int, List<EnvironmentViewModel>?>`-shaped per-row cache (or equivalent), keyed by `ApplicationId`, `null`/absent = not yet loaded, matching the "lazy on first expand" rule above.

## Data Flow

```
Page init
  → IApplicationRepository.GetAllAsync() → render Applications table

User submits "Create Application"
  → IApplicationRepository.AddAsync(...) [try/catch for 2601/2627 → inline error]
  → on success: re-run GetAllAsync(), clear form

User clicks a row's expand toggle
  → if not cached: IAppEnvironmentRepository.GetByApplicationIdAsync(appId) → cache + render
  → if cached: just toggle visibility, no new query

User submits "Add Environment" for an expanded row
  → IAppEnvironmentRepository.AddAsync(...)
  → on success: re-run GetByApplicationIdAsync(appId) for that row only, clear that row's form
```

## Error Handling

- Duplicate `Application.Name` → inline message, form preserved (see Architecture section above).
- Any other exception (e.g., a future validation rule, a transient DB error) is **not** specifically caught — it propagates to Blazor Server's default error UI (a generic reconnect/error banner). This matches the API's own current behavior (Task 8 only narrowly handles the two known cases; everything else still surfaces as a real error, not silently swallowed) and keeps this task's scope to what's already a known, named case.
- No client-side validation beyond HTML's native `required` attribute on the `Name` fields — matches the API's own current validation posture (implicit `[Required]` via non-nullable record parameters, no explicit validation layer either).

## Testing Strategy for This Slice

**What's already covered:** all data-layer behavior (repository CRUD, scoping, duplicate-name/unknown-parent handling) is covered by the existing 9 tests — the Blazor page is a thin rendering/interaction layer over already-tested logic, not new business logic.

**What's new and needs verification:** the Razor component's rendering and interaction logic itself (expand/collapse state, form submission wiring, the duplicate-try/catch's UI-side behavior). This project has no Blazor component-testing framework (e.g. bUnit) set up yet, and introducing one is a meaningfully bigger lift than this "basic UI" task justifies on its own.

**Decision:** no new automated UI-test framework for this task. Verification is manual, in an actual running browser, for each interaction path (create application, create duplicate application → see inline error, expand a row, add an environment) — treated as a required acceptance step per implementation task, the same way Task 6's `curl` smoke test was required, not optional. Adding bUnit (or a Playwright-based E2E layer) is noted as a future improvement once there's enough UI surface to justify the setup cost, not before.

## Open Question Resolved During Brainstorming

- Navigation: single page with per-row expansion (not two separate pages) — user's explicit choice.
- Auth: deferred, matching the API's current state — user's explicit choice.
