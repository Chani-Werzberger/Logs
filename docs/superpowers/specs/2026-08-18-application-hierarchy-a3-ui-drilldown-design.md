# A3: Hierarchy UI Drill-Down — Design

**Status:** Approved by user, ready for implementation planning
**Part of:** M1 (Application Model) — the third and final plan in the "Group A" hierarchy work (`docs/superpowers/specs/2026-08-18-application-hierarchy-spine-design.md`'s Execution Split). A1 (`AppModule`/`ScreenService` backend) and A2 (`ProcessNode`/`Operation` backend) are both merged to `main`; this plan builds the UI on top of the now-complete 4-level backend.

## Goal

A Blazor Server UI for the full `Application → AppModule → ScreenService → ProcessNode → Operation` hierarchy: drill-down navigation with breadcrumbs, Create/Rename/Deactivate for each of the four levels, matching the shape already specified in the hierarchy-spine design doc's "UI: Drill-Down with Breadcrumbs" section — this plan resolves the two items that doc explicitly left open (Rename UI mechanism, breadcrumb data-fetching approach) and fills in the remaining implementation detail.

## Non-Goals (explicit)

- No "show inactive" toggle in the list views — deactivated items simply drop out of the (default active-only) list after the action. The API already supports `includeInactive` if this is wanted later; not building it now (YAGNI).
- No change to `ApplicationsAdmin.razor`'s existing duplicate-name handling (still its original inline `SqlException` check) — not retroactively migrating already-shipped code to the newer `IsUniqueViolation()` helper as part of this plan.
- No authentication (matches the API's and the rest of the UI's current state).
- No custom CSS (plain HTML tables/forms, matching the established precedent).
- No bUnit or other new automated UI-test framework (see Testing section).

## Pages

Four new pages, one per hierarchy level, plus one small addition to the existing `ApplicationsAdmin.razor`:

| Page | Route | Parent context needed |
|---|---|---|
| `ModulesAdmin.razor` | `/admin/applications/{appId}/modules` | `appId` |
| `ScreenServicesAdmin.razor` | `/admin/applications/{appId}/modules/{moduleId}/screen-services` | `appId`, `moduleId` |
| `ProcessesAdmin.razor` | `/admin/applications/{appId}/modules/{moduleId}/screen-services/{screenServiceId}/processes` | `appId`, `moduleId`, `screenServiceId` |
| `OperationsAdmin.razor` | `/admin/applications/{appId}/modules/{moduleId}/screen-services/{screenServiceId}/processes/{processId}/operations` | `appId`, `moduleId`, `screenServiceId`, `processId` (leaf level — no further drill-down) |

Routes are the **full nested path**, carrying every ancestor ID — this is a deliberate difference from the Admin API's own routes (which are parent-scoped only, e.g. `/api/v1/admin/modules/{moduleId}/screen-services`). The nested UI path makes every page's URL self-describing and deep-linkable without extra lookups just to recover ancestor IDs — only ancestor *names* (for the breadcrumb) need fetching.

`ApplicationsAdmin.razor` gets one addition per row: a "Modules" link to `/admin/applications/{appId}/modules`, alongside the existing Environments expand-toggle.

Each of the four new pages is a separate `.razor` file, not a generic/reusable component — matching the established precedent from the hierarchy-spine design doc ("a generic `Repository<T>` base now would be premature... appropriate at this scale") and the fact that `ScreenService` has a `Type` field the others don't, which a fully generic page would need to special-case anyway.

## Each Page's Layout

1. **Breadcrumb trail** at the top (see below).
2. **Create form** — `Name` (required) + `Description` (optional) for `AppModule`/`ProcessNode`/`Operation`; `ScreenServicesAdmin.razor` additionally has a `Type` dropdown (Screen/Service). Same inline duplicate-name error handling as `ApplicationsAdmin.razor`'s existing Create form, but using `DbUpdateExceptionExtensions.IsUniqueViolation()` (new code, so it uses the current helper — not the older inline pattern the pre-existing page still has).
3. **Table** — one row per active child, columns: Name (a link into the next level down, except on `OperationsAdmin.razor` which has no further drill-down), Description (+ Type on `ScreenServicesAdmin.razor`), and per-row Rename/Deactivate controls.

## Rename: Inline Edit Toggle

Each page tracks a single `int? _editingId` (only one row editable at a time, matching typical inline-edit UX). A row's "Edit" button sets `_editingId` to that row's id, swapping the Name/Description cells for an `EditForm` with `Save`/`Cancel` buttons — the same toggle vocabulary already used for `ApplicationsAdmin.razor`'s expand/collapse, applied to edit-mode instead of expanded-mode. `Save` calls the repository's `RenameAsync`, wrapped in the same `IsUniqueViolation()` try/catch as Create, and resets `_editingId = null` on success (leaving it set, with an inline error, on a duplicate-name conflict — same UX as Create's error handling). `Cancel` resets `_editingId = null` without saving.

## Deactivate

A plain "Deactivate" button per row, calling the repository's `DeactivateAsync` then refreshing that page's list (same active-only-by-default list the page already loads). No confirmation dialog (matches the project's overall "no custom UI chrome" posture so far) — deactivation is already non-destructive (soft-delete, reversible by design, per the hierarchy-spine design doc's Deactivate Semantics).

## Breadcrumb: Shared `BreadcrumbBuilder` Service

**File:** `src/LogsPlatform.Web/Services/BreadcrumbBuilder.cs`

A plain C# service (not a Razor component), registered `Scoped` in DI (it depends on the already-`Scoped` repositories). One method, called with whichever IDs the calling page actually has:

```csharp
public record BreadcrumbSegment(string Label, string Url);

public class BreadcrumbBuilder
{
    // constructor injects IApplicationRepository, IAppModuleRepository,
    // IScreenServiceRepository, IProcessNodeRepository

    public async Task<List<BreadcrumbSegment>> BuildAsync(
        int appId, int? moduleId = null, int? screenServiceId = null, int? processId = null)
    {
        // Always fetches the Application's name.
        // Fetches AppModule's name only if moduleId is given, and so on down the chain —
        // each fetch is a direct GetByIdAsync call on that level's own repository
        // (no FK-walking needed, since every ancestor ID is already known from the route).
        // Returns segments in root-to-leaf order, each with a Url pointing to that
        // level's own list page (so every breadcrumb segment is a working link back up).
    }
}
```

Each page injects `BreadcrumbBuilder` and calls it in `OnInitializedAsync` with its own known route IDs. This is genuinely identical, repeated logic across all four pages (unlike the per-level CRUD forms, which the hierarchy-spine design doc explicitly rejected genericizing because of `ScreenService`'s extra field) — extracting it avoids four copies of the same ancestor-name-fetching sequence without fighting that same objection, since breadcrumb-building has no per-level special-casing.

## Data Flow (one example, `ProcessesAdmin.razor`)

```
Page init (route gives appId, moduleId, screenServiceId)
  → BreadcrumbBuilder.BuildAsync(appId, moduleId, screenServiceId) → render breadcrumb
  → IProcessNodeRepository.GetByScreenServiceIdAsync(screenServiceId) → render table

User submits "Create Process"
  → IProcessNodeRepository.AddAsync(...) [try/catch IsUniqueViolation() → inline error]
  → on success: re-run GetByScreenServiceIdAsync(screenServiceId), clear form

User clicks "Edit" on a row
  → _editingId = row.Id → row swaps to EditForm

User submits the edit form ("Save")
  → IProcessNodeRepository.RenameAsync(...) [try/catch IsUniqueViolation() → inline error, _editingId stays set]
  → on success: _editingId = null, re-run GetByScreenServiceIdAsync(screenServiceId)

User clicks "Deactivate" on a row
  → IProcessNodeRepository.DeactivateAsync(row.Id)
  → re-run GetByScreenServiceIdAsync(screenServiceId) [row drops out, since it's now inactive]

User clicks a row's Name link
  → navigates to /admin/applications/{appId}/modules/{moduleId}/screen-services/{screenServiceId}/processes/{row.Id}/operations
```

## Testing Strategy

Same posture as the first Blazor UI slice (`ApplicationsAdmin.razor`): all data-layer behavior (repository CRUD, scoping, duplicate-name/detach-on-failure/IDOR) is already covered by A1/A2's 72 tests — these four pages are a thin rendering/interaction layer over already-tested logic, not new business logic. `BreadcrumbBuilder` is the one piece of genuinely new logic in this plan; it's plain C# (not a Razor component) and can be covered by ordinary xUnit tests against a real LocalDB, following the exact same `TestDatabase.CreateContext()` pattern already used throughout the project — no new test infrastructure needed for it specifically.

No bUnit or other automated Razor-component-testing framework is being introduced (same reasoning as before: the lift isn't justified yet, and the project's manual-verification posture has caught real bugs in both prior Blazor rounds). Manual, in-a-real-browser verification of each interaction path (create at each level, rename via the inline toggle, deactivate, breadcrumb links, full drill-down navigation from Applications down to Operations and back) is a required acceptance step per implementation task, not optional — matching both prior Blazor slices' precedent.

## Known Limitation Carried Forward (documented, not fixed here)

`ApplicationsAdmin.razor`'s and this plan's pages all resolve repositories through a `Scoped`-lifetime `DbContext`, which in Blazor Server means one instance per browser circuit, not per operation — the same circuit-scoped-`DbContext` situation already documented in the first Blazor slice's design doc, including its noted-but-deferred concurrency risk (`IDbContextFactory` would be the fix, not urgent at this interaction-surface size). Not re-litigated here; carried forward as-is.
