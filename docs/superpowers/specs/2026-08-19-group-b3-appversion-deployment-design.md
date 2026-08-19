# B3: AppVersion + Deployment — Design

**Status:** Approved by user, ready for implementation planning
**Part of:** M1 (Application Model) — "Group B" (flat, non-hierarchical entities directly under `Application`), the third and final of three planned sub-groups (B1/B2/B3). Group A (hierarchy), Group B1 (`Customer`/`AppUser`/`LogSource`), and Group B2 (`ApiKey`) are all fully merged to `main`. B3 completes Group B and, with it, M1's own acceptance criterion.

## Goal

Admin management (Create/List/Rename/Deactivate) for `AppVersion` and `Deployment` — a linked pair of release-record entities. `AppVersion` represents a released build of an application; `Deployment` represents the fact that a specific version was deployed to a specific environment at a specific time. Backend + UI in one plan, same posture as B1/B2.

## Why These Two Are a Linked Pair, Not Two Independent Entities

Per `05-מודל-נתונים.md` and `06-מודל-אפליקציה.md`: `Deployment` references both `AppEnvironment` and `AppVersion` — it is the join record between "when/where" (`AppEnvironment` + `DeployedAt`) and "what" (`AppVersion`). Both are explicitly **not** part of the internal Module→ScreenService→ProcessNode→Operation hierarchy — `06-מודל-אפליקציה.md` §2 states this directly: "a version is a snapshot of the whole application, not of one module." Per-module independent deployment is called out as an explicit future extension, not supported in V1. This is also the pairing the Analysis Engine's Deployment→Error-Spike correlation type will eventually key off of (`05` §2: correlating `(EnvironmentId, VersionId, Timestamp)` on `Event` against `(EnvironmentId, VersionId, DeployedAt)` on `Deployment`) — reason enough to design and ship them together rather than splitting further.

## Entities

```
AppVersion (Id, ApplicationId→Application, VersionNumber, ReleaseNotes, CreatedAt, IsActive)
Deployment (Id, ApplicationId→Application, EnvironmentId→AppEnvironment, VersionId→AppVersion, DeployedAt, Notes, IsActive)
```

- **`AppVersion`** avoids the `System.Version` BCL collision the same way `AppModule`/`AppEnvironment`/`AppUser` already do in this codebase — C# type stays `AppVersion`, but the route/controller/UI text use the prosaic short form `versions`/`VersionsController`, matching the established convention.
- **`IsActive` on both**, confirmed as a spec gap back when B1 was designed (`05-מודל-נתונים.md`'s schema originally omitted it from these two tables, but `07-Ingestion-ו-API.md` §5's uniform `?includeInactive=true` CRUD pattern was always meant to apply here too — B1's design doc flagged this and deferred the fix to this plan).
- **Editability (this plan's defining design decision, confirmed with the user):** `AppVersion.VersionNumber` is set at creation and never renamed — it's the identity anchor deployments reference, exactly like B1's `Customer.ExternalCustomerId`/`AppUser.ExternalUserId`. Only `ReleaseNotes` is editable via Rename. `Deployment.EnvironmentId`/`VersionId`/`DeployedAt` are ALL immutable after creation — a `Deployment` record's job is to state a historical fact ("this version went to this environment at this time"), and the future Analysis Engine will correlate against exactly that fact; editing it after the fact would be rewriting history rather than correcting a typo. Only `Notes` is editable via Rename. Correcting a genuinely wrong `Deployment` record (wrong environment, wrong version, wrong timestamp) means deactivating that record and creating a correct one — the same pattern this project already uses everywhere soft-delete exists.
- **Uniqueness**: `AppVersion` gets a unique `(ApplicationId, VersionNumber)` index — same identity-field pattern as every prior entity. `Deployment` gets **no** uniqueness constraint at all: redeploying the same version to the same environment (a hotfix redeploy, a rollback-then-redeploy) is legitimate and expected, not a duplicate-input error. This means `Deployment`, like `ApiKey` in B2, has no `IsUniqueViolation()`/409 path anywhere in its repository, controller, or UI — a deliberate difference from `AppVersion` and every hierarchy/B1 entity, not an omission.
- **Soft-delete only, same reasoning as every prior entity**: the `Event` table doesn't exist until M2, so "hard-delete if no history" can't be evaluated yet. `DELETE` always sets `IsActive = false`, never hard-deletes.
- **Field shapes**: `VersionNumber` is a free-text string, not validated against semver or any other format (an org might use build numbers, dates, or any other scheme) — `HasMaxLength(200)`, matching every prior identity field (`ExternalCustomerId`, `ExternalUserId`, `Name`). `ReleaseNotes` and `Deployment.Notes` are both nullable free-text fields with **no** `HasMaxLength` constraint, matching `LogSource.Description`'s precedent (`nvarchar(max)` by EF's default) — release notes and deployment notes can run long. Since neither has `HasMaxLength`, neither gets a `maxlength` attribute on its UI `InputText`, consistent with this project's existing rule that only `HasMaxLength`-bound fields get one.

## API Shape

Both follow the standard 5-endpoint pattern already proven in Group A and B1:

| Method | Route | Behavior |
|---|---|---|
| `POST` | `/api/v1/admin/applications/{appId}/versions` | Create. Body: `{ versionNumber, releaseNotes }`. `409` on duplicate `(ApplicationId, VersionNumber)`. `404` if `appId` doesn't exist. |
| `GET` | `/api/v1/admin/applications/{appId}/versions` | List, active-only by default, `?includeInactive=true` for all. |
| `GET` | `/api/v1/admin/applications/{appId}/versions/{id}` | Single. `404` if not found or belongs to a different application (IDOR guard). |
| `PUT` | `/api/v1/admin/applications/{appId}/versions/{id}` | Rename. Body: `{ releaseNotes }` only — `versionNumber` is immutable. |
| `DELETE` | `/api/v1/admin/applications/{appId}/versions/{id}` | Deactivate (soft-delete only). |

`Deployment` follows the identical shape under `/api/v1/admin/applications/{appId}/deployments`, with three differences:
- **Create body**: `{ environmentId, versionId, deployedAt, notes }`.
- **Create validation has THREE guards, not one**: `appId` must exist (standard parent-existence guard); the given `environmentId` must belong to that `appId` (`404` if it doesn't — reuses `IAppEnvironmentRepository.GetByIdAsync`); the given `versionId` must belong to that `appId` (`404` if it doesn't — reuses `IAppVersionRepository.GetByIdAsync`). Same IDOR-style ownership check this project already applies everywhere an id is looked up under a parent route, just applied proactively at creation time against two additional foreign keys instead of only at read/write-by-own-id time.
- **Rename body**: `{ notes }` only — `environmentId`/`versionId`/`deployedAt` are immutable.

## Repository Interfaces

```csharp
public interface IAppVersionRepository
{
    Task<AppVersion?> GetByIdAsync(int id);
    Task<IReadOnlyList<AppVersion>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<AppVersion> AddAsync(AppVersion version);
    Task<AppVersion> RenameAsync(int id, string releaseNotes);
    Task DeactivateAsync(int id);
}

public interface IDeploymentRepository
{
    Task<Deployment?> GetByIdAsync(int id);
    Task<IReadOnlyList<Deployment>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<Deployment> AddAsync(Deployment deployment);
    Task<Deployment> RenameAsync(int id, string? notes);
    Task DeactivateAsync(int id);
}
```

Back to the standard `AddAsync(TEntity)` shape used by Group A and B1 — B2's `ApiKey.AddAsync(int, string)` tuple-returning shape was specific to needing to generate and return a transient raw secret, which doesn't apply here.

`AddAsync` and `RenameAsync` wrap `SaveChangesAsync` in `try`/`catch`/`_context.Entry(entity).State = EntityState.Detached`/`throw` from the first draft. `DeactivateAsync` gets the same detach-on-failure treatment from the first draft too — correctly inheriting the lesson B1's plan initially missed (and had to retrofit after its final review) rather than repeating that gap a third time.

**A lesson from B2's final review, worth stating explicitly even though it doesn't end up applying here:** B2's `RevokeAsync` had a bug where an idempotency guard read a change-tracker-cached entity via `FindAsync`, which is safe under a short-lived per-HTTP-request `DbContext` but not under Blazor Server's long-lived circuit-scoped one. Neither `AppVersion.DeactivateAsync` nor `Deployment.DeactivateAsync` has an idempotency guard that reads current state before writing (unlike `ApiKey.RevokeAsync`, they unconditionally set `IsActive = false`, matching every Group A/B1 entity's `DeactivateAsync`) — so this specific pitfall does not apply to either entity in this plan. Noted here so the implementer and reviewers don't need to re-derive that this plan is unaffected.

## UI: Two More Subsections on `ApplicationsAdmin.razor`'s Existing Row Expansion

Same self-contained, sibling-independent pattern as every prior Group B plan: `VersionsSection.razor` and `DeploymentsSection.razor`, each `[Parameter] public int ApplicationId`, added directly after the existing `<ApiKeysSection>` line.

- **`VersionsSection`**: table (Version Number, Release Notes), inline "Add" form (Version Number + Release Notes), per-row inline-edit Rename toggle (Release Notes only — Version Number is read-only, not present in the edit form), Deactivate button per row, no confirmation prompt.
- **`DeploymentsSection`**: table (Environment name, Version number, Deployed At, Notes), inline "Add" form with two `<select>` dropdowns — one populated from `IAppEnvironmentRepository.GetByApplicationIdAsync(ApplicationId)` (Environment), one from `IAppVersionRepository.GetByApplicationIdAsync(ApplicationId)` (Version) — plus a `DeployedAt` date/time input and a `Notes` field. Per-row inline-edit Rename toggle (Notes only). Deactivate button per row, no confirmation prompt.
- **No live cross-component refresh**: if an admin creates a new `AppVersion` in `VersionsSection` and then immediately opens `DeploymentsSection`'s create form in the same expanded row, the new version won't appear in the dropdown until the row collapses and re-expands (each section fetches its own data independently in `OnInitializedAsync`, with no shared/observable state between sibling sections). This matches the existing precedent — none of B1's or B2's sections coordinate with their siblings either — and is not something this plan introduces or needs to solve.
- `maxlength="200"` on every string `InputText` bound to a `HasMaxLength(200)` property, matching every prior string field in this project.

## Testing

Same posture as every prior plan: real integration tests (LocalDB) for the repositories and controllers, `TestDatabase.CreateContext()`/`TestWebApplicationFactory`/`[Collection("Database")]`. The UI portion follows the established posture — no bUnit, and the manual "smoke check" step uses the already-corrected code-inspection guidance from B1's fixed plan doc (`curl` cannot reach content behind `ApplicationsAdmin.razor`'s row-expansion toggle) rather than a curl-based check.

`DeploymentRepositoryTests`/`DeploymentsControllerTests` need explicit coverage for the three-guard `Create` validation: `environmentId` belonging to a different application, `versionId` belonging to a different application, and both belonging to the correct application (happy path) — this is new territory none of the prior plans' entities needed, since none of them referenced two sibling FKs at once.

## Non-Goals (explicit)

- No Analysis Engine correlation logic — that's M4's concern; this plan is Admin-managed CRUD only, exactly like every prior Group A/B entity.
- No per-module independent deployment — explicitly called out in `06-מודל-אפליקציה.md` as a future extension, not V1.
- No change to any already-shipped Group A, B1, or B2 code or UI.
