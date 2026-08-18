# Application Hierarchy Spine (Module → ScreenService → ProcessNode → Operation) — Design

**Status:** Approved by user, ready for implementation planning
**Part of:** M1 (Application Model) — replicates the `Application`/`AppEnvironment` pattern (backend + UI, already merged) across the rest of the logical-structure hierarchy defined in `06-מודל-אפליקציה.md`. This is "Group A" of the two remaining hierarchy groups noted in the prior plan's "Next Plans" section — "Group B" (`Customer`/`AppUser`/`LogSource`/`ApiKey`/`AppVersion`/`Deployment`, flat entities with no parent-child chain among themselves) is separate, later work.

## Goal

Full CRUD-minus-real-delete management of the `Module → ScreenService → ProcessNode → Operation` chain — the same shape as `Application`/`AppEnvironment`, but with two capabilities that slice didn't need: **Rename** and **Deactivate** (soft-delete), both required by `06-מודל-אפליקציה.md` §3 for every level of this hierarchy. Includes a UI extension (drill-down navigation with breadcrumbs) so the structure is manageable without code changes, per the original spec's §15.

## Why one design doc for four entities

`Module`, `ScreenService`, `ProcessNode`, and `Operation` are architecturally identical: each is a child of exactly one parent in the chain, each needs Create/List(-by-parent)/GetById/Rename/Deactivate, each enforces name-uniqueness within its immediate parent's scope. `ScreenService` has one extra field (`Type`: Screen|Service) and nothing else differs. Documenting the pattern once and naming which entity plays which role avoids writing the same design four times — the schema for all four is already fully specified in `05-מודל-נתונים.md` §2-3 and `06-מודל-אפליקציה.md` §1,3; this doc covers behavior (API/UI), not schema.

## Non-Goals (explicit)

- No hard-delete anywhere in this slice (see "Deactivate Semantics" below — this is a deliberate simplification, not an oversight).
- No `Customer`/`AppUser`/`LogSource`/`ApiKey`/`AppVersion`/`Deployment` ("Group B") — separate later work.
- No authentication (still matches the API's overall current state).
- No custom CSS (same as the prior UI slice).
- No change to the existing `Application`/`AppEnvironment` API or UI beyond adding one new link/section per Application row pointing into the new drill-down.

## Naming Correction: `AppModule`, not `Module`

Following this project's established BCL-collision-avoidance rule (`AppEnvironment` not `Environment`, `ProcessNode` not `Process` — see the prior plan's Global Constraints), the C# entity class for the "Module" hierarchy level must be named **`AppModule`**, not `Module` — `System.Reflection.Module` is a real BCL type, and this project has consistently avoided this exact class of ambiguity risk everywhere else. The hierarchy level is still conceptually/prosaically called "Module" throughout this doc and in routes (`/modules`) and UI text — only the C# type name changes.

## API Shape (one pattern, four entities)

Using `AppModule` as the canonical example — `ScreenService`, `ProcessNode`, `Operation` follow identically, each nested under its own parent's route:

| Method | Route | Behavior |
|---|---|---|
| `POST` | `/api/v1/admin/applications/{appId}/modules` | Create. Body: `{ name, description }`. `201` with created `Module`. `409` on duplicate name within the same `ApplicationId` (same `SqlException { Number: 2601 or 2627 }` pattern already used for `Application`). |
| `GET` | `/api/v1/admin/applications/{appId}/modules` | List, scoped to `appId`. Default: active only. `?includeInactive=true` returns all. |
| `GET` | `/api/v1/admin/applications/{appId}/modules/{id}` | Single `Module` — needed so a drill-down page can render "you are inside Module X" (breadcrumb label) without re-fetching the whole list. `404` if not found. |
| `PUT` | `/api/v1/admin/applications/{appId}/modules/{id}` | Rename. Body: `{ name, description }`. `200` with updated `Module`. Same `409`-on-duplicate handling as Create (renaming into a name a sibling already has hits the same unique index). |
| `DELETE` | `/api/v1/admin/applications/{appId}/modules/{id}` | Deactivate (see below — never a real delete in this slice). `204 No Content`. |

Chain routes for the other three levels: `/modules/{moduleId}/screen-services`, `/screen-services/{screenServiceId}/processes`, `/processes/{processId}/operations` — each with the identical 5-endpoint shape, scoped to its own parent id instead of `appId`.

## Deactivate Semantics — Simplified From the Spec, Deliberately

`06-מודל-אפליקציה.md` §3 says: soft-delete (`IsActive=false`) if the node has historical `Event`s, hard-delete if it doesn't. **The `Event` table doesn't exist yet** (it's part of M2's Ingestion work) — there is currently no way to check "does this node have history," so the has-history/no-history branch can't be implemented correctly today. Implementing hard-delete now, before it's possible to check the one condition that's supposed to gate it, risks building the less-safe path first.

**Decision for this slice: `DELETE` always deactivates (`IsActive=false`), never hard-deletes.** Hard-delete-when-genuinely-history-free is deferred until the `Event` table exists and the check becomes meaningful — noted here explicitly so it isn't mistaken for the final behavior.

## Repository Interfaces (pattern, `AppModule` example)

```csharp
public interface IAppModuleRepository
{
    Task<AppModule?> GetByIdAsync(int id);
    Task<IReadOnlyList<AppModule>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<AppModule> AddAsync(AppModule module);
    Task<AppModule> RenameAsync(int id, string name, string? description);
    Task DeactivateAsync(int id);
}
```

Interface names mirror their entity name exactly, matching the existing `Application`→`IApplicationRepository`/`AppEnvironment`→`IAppEnvironmentRepository` convention (routes and controllers still use the short "Module"/"ScreenService" form, per the `EnvironmentsController` precedent — only repository interfaces/classes take the full entity name). Same shape for `IScreenServiceRepository` (scoped by `moduleId`), `IProcessNodeRepository` (scoped by `screenServiceId`), `IOperationRepository` (scoped by `processId`) — each following the "always scope child-entity queries by parent" Global Constraint already established in the prior plan.

## UI: Drill-Down with Breadcrumbs

**Entry point:** the existing `/admin/applications` page gets one addition per row — a "Modules" link (alongside the existing Environments expand-toggle), navigating to:

```
/admin/applications/{appId}/modules
  → click a Module → /admin/applications/{appId}/modules/{moduleId}/screen-services
    → click a ScreenService → .../screen-services/{screenServiceId}/processes
      → click a ProcessNode → .../processes/{processId}/operations
```

Each page: a breadcrumb trail back to the root (`Application Name > Module Name > ...`), a Create form, a table (Name, Description/Type-for-ScreenService, Active status), a Rename action per row (inline form or small edit toggle — implementation detail for the plan, not fixed here), and a Deactivate button per row. Each page is a separate `.razor` file — **not** a generic/reusable component — matching the prior plan's explicit precedent ("a generic `Repository<T>` base now would be premature... appropriate at this scale") and the fact that `ScreenService` has one field (`Type`) the others don't, which a fully generic component would need to special-case anyway.

## Execution Split (three implementation plans, not one)

The design above is one pattern; the *implementation* is large enough (4 entities × 5 endpoints × repository × UI) that it should not be one plan:

1. **A1 — Module + ScreenService backend** (entities, `DbContext` mapping + migration, repositories, controllers). Proves the Rename/Deactivate shape for the first time.
2. **A2 — ProcessNode + Operation backend** (same shape, should move faster — pattern is proven by A1).
3. **A3 — UI drill-down**, built on top of A1+A2's completed APIs, covering all four levels at once (a UI plan split by level would be artificial — the breadcrumb navigation only makes sense once the whole chain exists).

Each of A1/A2/A3 gets dispatched through `subagent-driven-development` the same way the prior two plans were, in that order.

## Testing

Same posture as the prior two plans: real integration tests (LocalDB) for A1/A2's repositories and controllers, following the exact patterns already established (`TestDatabase.CreateContext()`, `[Collection("Database")]`, `TestWebApplicationFactory`). A3 (UI) gets the same "no new UI-test framework, curl-based structural checks + explicit manual-browser-verification handoff" posture as the first UI slice — including, based on what the last slice's final review found, applying the **already-known fix pattern preemptively**: repositories' write methods (`AddAsync`, `RenameAsync`) must detach the entity on `SaveChangesAsync` failure from the start (the circuit-scoped-`DbContext` lesson from the `Application`/`AppEnvironment` slice applies identically here — no need to rediscover it).
