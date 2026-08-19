# B1: Customer + AppUser + LogSource — Design

**Status:** Approved by user, ready for implementation planning
**Part of:** M1 (Application Model) — "Group B" (flat, non-hierarchical entities directly under `Application`), the first of three planned sub-groups (B1/B2/B3). "Group A" (the `Application → AppModule → ScreenService → ProcessNode → Operation` hierarchy, backend + UI) is fully merged to `main`.

## Goal

Admin CRUD (Create/List/Rename/Deactivate) for `Customer`, `AppUser`, and `LogSource` — three flat, per-`Application` entities that, unlike Group A's hierarchy, have no parent-child chain among themselves and no drill-down navigation. Backend + UI in one plan, since the reduced complexity (no nesting, no breadcrumbs) doesn't justify the A1/A2/A3-style split.

## Why These Three, Not All of Group B

`מסמכי-אפיון\05-מודל-נתונים.md` and `07-Ingestion-ו-API.md` define six "Group B" entities: `Customer`, `AppUser`, `LogSource`, `ApiKey`, `Version` (referred to as `AppVersion` in code, matching the `AppModule`/`AppEnvironment` BCL-collision-avoidance convention), and `Deployment`. These are architecturally three different shapes, not one uniform pattern:

1. **`Customer`/`AppUser`/`LogSource`** — simple descriptive entities, the closest in spirit to Group A's CRUD pattern (this plan, B1).
2. **`ApiKey`** — a genuinely different lifecycle: creation shows a raw key exactly once before only its hash is ever stored again, there's no "Rename" (only a `Label`), and removal is `Revoke` (setting `RevokedAt`), not deactivate/delete. Deferred to a later plan (B2).
3. **`AppVersion` + `Deployment`** — a linked pair of what are really *release records*, not manageable nodes in the same sense — `Deployment` references both `Environment` and `AppVersion`. Deferred to a later plan (B3).

## Spec Gaps Found and Resolved (both confirmed with the user before this design)

- **`IsActive` gap:** `05-מודל-נתונים.md`'s schema gives `IsActive` only to the four hierarchy tables, but `07-Ingestion-ו-API.md` §5 says the uniform CRUD pattern (soft-delete + `?includeInactive=true`) applies to `customers` (and `versions`/`deployments`/`api-keys`) too. **Resolved: add `IsActive` to `Customer` (and, for B3, `AppVersion`/`Deployment` — `ApiKey` already has its own soft-delete-equivalent field, `RevokedAt`).**
- **`AppUser`/`LogSource` missing from the Admin API endpoint list:** `07-Ingestion-ו-API.md` §5 enumerates `customers, versions, deployments, api-keys` but never mentions `AppUser` or `LogSource`, even though `06-מודל-אפליקציה.md`'s ERD lists both as first-class children of `Application`. **Resolved: give both the same full CRUD scope as `Customer`, including `IsActive`** — for `LogSource` this is defensive consistency more than a strict necessity (it's the one entity of these three not referenced by `Event`, per `05`'s Event schema, so "delete without history" would always be safe — but keeping the shape uniform across all three of this plan's entities is simpler than special-casing one).

## Entities

```
Customer (Id, ApplicationId→Application, ExternalCustomerId, Name, IsActive)
AppUser  (Id, ApplicationId→Application, ExternalUserId, DisplayName, IsActive)
LogSource(Id, ApplicationId→Application, Name, Description, IsActive)
```

- **`Customer.ExternalCustomerId`** and **`AppUser.ExternalUserId`** are set at creation and **not** part of Rename — they're the external-system correlation keys events will eventually be matched against during ingestion (M2); changing them after the fact would be equivalent to changing an entity's identity, not renaming it. Only `Name`/`DisplayName` (and `LogSource.Description`) are renameable — this mirrors the already-established precedent of `ScreenService.Type` being set-once-at-creation, immutable via Rename.
- **`LogSource`** has no external-id concept (it's `Name`+`Description` only, both renameable) — per `05-מודל-נתונים.md`'s note, it's "a purely descriptive entity, not an auth mechanism."
- **Uniqueness scope**, all within the immediate `Application`, matching Group A's composite-index pattern:
  - `Customer`: unique `(ApplicationId, ExternalCustomerId)` — not `Name`, since `Name` is just a display label and two customers could legitimately share one; the external id is the real identity anchor.
  - `AppUser`: unique `(ApplicationId, ExternalUserId)`, same reasoning.
  - `LogSource`: unique `(ApplicationId, Name)` — no external id, so `Name` is the natural anchor here, matching the hierarchy's own convention.
- **Soft-delete only, same reasoning as Group A:** the `Event` table doesn't exist until M2, so "hard-delete if no history" can't be evaluated yet. `DELETE` always sets `IsActive = false`.

## API Shape (three parallel resources, each the same 5-endpoint pattern already proven in Group A)

| Method | Route | Behavior |
|---|---|---|
| `POST` | `/api/v1/admin/applications/{appId}/customers` | Create. Body: `{ externalCustomerId, name }`. `409` on duplicate `(ApplicationId, ExternalCustomerId)`. |
| `GET` | `/api/v1/admin/applications/{appId}/customers` | List, active-only by default, `?includeInactive=true` for all. |
| `GET` | `/api/v1/admin/applications/{appId}/customers/{id}` | Single `Customer`. `404` if not found or belongs to a different application (IDOR guard, matching Group A's established pattern). |
| `PUT` | `/api/v1/admin/applications/{appId}/customers/{id}` | Rename. Body: `{ name }` only — `externalCustomerId` is immutable. |
| `DELETE` | `/api/v1/admin/applications/{appId}/customers/{id}` | Deactivate (soft-delete only, never hard). |

`AppUser` follows the identical shape under `/api/v1/admin/applications/{appId}/users` (prosaic route form, matching the established `AppEnvironment`→`/environments` / `AppModule`→`/modules` convention — the C# entity type stays BCL-safe as `AppUser`, but the URL and UI text just say "users"), with `{ externalUserId, displayName }` on Create and `{ displayName }` on Rename.

`LogSource` follows the identical shape under `/api/v1/admin/applications/{appId}/log-sources`, with `{ name, description }` on both Create and Rename (no immutable field, since there's no external id here).

All three reuse the already-merged `DbUpdateExceptionExtensions.IsUniqueViolation()` helper for `409` handling — no new inline `SqlException` pattern.

## Repository Interfaces (pattern, `Customer` example)

```csharp
public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(int id);
    Task<IReadOnlyList<Customer>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<Customer> AddAsync(Customer customer);
    Task<Customer> RenameAsync(int id, string name);
    Task DeactivateAsync(int id);
}
```

Same shape for `IAppUserRepository`/`ILogSourceRepository` — note `RenameAsync` here takes only `name` (or `displayName`), not `(name, description)` like Group A's hierarchy repositories, since `Customer`/`AppUser` have no `Description` field. `LogSource.RenameAsync` does take `(name, description)`, matching Group A's exact shape, since it has both fields.

Every `AddAsync`/`RenameAsync` wraps `SaveChangesAsync` in `try`/`catch`/`_context.Entry(entity).State = EntityState.Detached`/`throw` from the first draft — the detach-on-failure lesson from Group A, not rediscovered here.

## UI: Extend `ApplicationsAdmin.razor`'s Existing Per-Row Expansion

No new pages, no breadcrumbs — `Customer`/`AppUser`/`LogSource` are flat, so the existing "expand-in-place" pattern already used for `AppEnvironment` on `ApplicationsAdmin.razor` is the right fit, not the drill-down-with-breadcrumbs pattern Group A needed for its 4-level nesting.

Each Application row's existing expanded section gains three more subsections (alongside the existing Environments one): **Customers**, **Users**, **Log Sources** — each with its own small table (relevant columns + Active status), inline "Add" form, per-row inline-edit Rename toggle (the same single-`_editingId`-per-section mechanism established in Group A's UI plan), and a Deactivate button per row. Unlike Group A's UI, there's no drill-down link on any row — these are leaf-level, flat lists.

## Testing

Same posture as every prior plan: real integration tests (LocalDB) for the repositories and controllers, `TestDatabase.CreateContext()`/`TestWebApplicationFactory`/`[Collection("Database")]`. The UI portion follows Group A's established posture — no bUnit, a `curl`-based structural smoke check per task, and a required full interactive browser walkthrough once the whole plan is merged.

## Non-Goals (explicit)

- `ApiKey`, `AppVersion`, `Deployment` — separate, later plans (B2, B3).
- No auto-creation of `Customer`/`AppUser` from ingestion — that's an M2 concern; this plan is Admin-managed CRUD only, matching how Group A's hierarchy is also never auto-created from ingested data (`07-Ingestion-ו-API.md` §3's "never auto-create nodes" policy, which this plan treats as applying equally to `Customer`/`AppUser` even though `07` doesn't say so explicitly for these two — being conservative/consistent rather than assuming a different, unstated policy).
- No change to the already-shipped Group A hierarchy code or UI.
