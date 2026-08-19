# B2: ApiKey — Design

**Status:** Approved by user, ready for implementation planning
**Part of:** M1 (Application Model) — "Group B" (flat, non-hierarchical entities directly under `Application`), the second of three planned sub-groups (B1/B2/B3). Group A (hierarchy) and Group B1 (`Customer`/`AppUser`/`LogSource`) are fully merged to `main`.

## Goal

Admin management (Create/List/Revoke) for `ApiKey` — the credential an application's ingestion client uses to authenticate to the (future, M2) ingestion API. Architecturally different from every entity built so far: creation shows a raw secret exactly once, only its hash is ever stored, and removal is `Revoke` (idempotent, sets `RevokedAt`) rather than `Rename`/`Deactivate`.

## Why This Isn't Shaped Like B1

`Customer`/`AppUser`/`LogSource` (B1) are descriptive records an admin edits over time. `ApiKey` is a credential:

- **No `Rename`.** `Label` is set once at creation and never edited through this UI — there is no reachable update path at all, so this plan has **no `PUT` endpoint**, unlike every prior entity's uniform 5-endpoint shape.
- **No `IsActive`.** `RevokedAt` (nullable `DateTime`) is both the soft-delete marker and an audit fact ("when was this key turned off") — this is already the shape `05-מודל-נתונים.md` specifies (`ApiKey(Id, ApplicationId, KeyHash, Label, CreatedAt, RevokedAt NULL)`), so unlike B1's `Customer`/`AppVersion`/`Deployment`, there is no spec gap to fill here.
- **No duplicate-conflict case, anywhere.** Every prior entity's `AddAsync` could hit a real unique-constraint violation from legitimate user input (two customers with the same external id, two log sources with the same name). `ApiKey`'s only near-unique value is `KeyHash`, derived from a 256-bit random secret the admin never types — a collision is cryptographically unreachable, not just unlikely-in-practice. `ApiKey.AddAsync` is therefore the first `AddAsync` in this project with no `IsUniqueViolation()` catch anywhere in its call chain (repository, controller, or UI).

## Entity

```
ApiKey (Id, ApplicationId→Application, KeyHash, Label, CreatedAt, RevokedAt NULL)
```

- **`KeyHash`**: SHA-256 hash of the raw key, stored as hex (64 chars). Plain `SHA256`, not PBKDF2/BCrypt — `10-Security-Design.md` §2 only mandates PBKDF2/BCrypt for UI login passwords (low-entropy, brute-forceable, need deliberately slow verification). A 256-bit random API key has no brute-force surface a fast hash weakens, and this hash sits on the (future, M2) ingestion request's hot path — using a slow password hash there would add needless latency to every ingested event.
- **`RevokedAt`**: `NULL` = active. Set once, on first `Revoke`; a second `Revoke` call is a no-op that leaves the original timestamp untouched (re-revoking must not overwrite the historical fact of *when* the key actually stopped being valid).
- No unique index on `KeyHash` — a plain (non-unique) index only, present for M2's future ingestion-auth lookup performance, not for any constraint this plan needs enforced.
- **`Label` has no uniqueness constraint** — an admin can create two keys with the same label for one `Application` (e.g. rotating a key: create the replacement labeled the same as the one about to be revoked). Nothing in the spec requires otherwise.

## Raw Key Format

`RandomNumberGenerator.GetBytes(32)` → base64url-encode → prefix `lgp_` (e.g. `lgp_xK9dQm...`). Recognizable/greppable in code and logs, matching the convention used by GitHub/Stripe-style API keys. Generated server-side in the repository's `AddAsync`, returned to the caller exactly once in the `Create` response, never persisted or logged in raw form.

## Repository Interface

```csharp
public interface IApiKeyRepository
{
    Task<ApiKey?> GetByIdAsync(int id);
    Task<IReadOnlyList<ApiKey>> GetByApplicationIdAsync(int applicationId, bool includeRevoked = false);
    Task<(ApiKey Entity, string RawKey)> AddAsync(int applicationId, string label);
    Task RevokeAsync(int id);
}
```

`AddAsync` takes `(applicationId, label)` rather than a constructed entity — unlike every prior `AddAsync(TEntity)`, the raw key and its hash are generated *inside* the repository (the only place both the raw value and its persistence need to exist together), and the raw value must travel back out to the caller alongside the persisted entity, so the return shape is a tuple rather than just the entity.

Both `AddAsync` and `RevokeAsync` wrap `SaveChangesAsync` in `try`/`catch`/`_context.Entry(entity).State = EntityState.Detached`/`throw`, present from this plan's first draft — the detach-on-failure lesson every one of A1/A2/B1's final reviews had to re-catch because it wasn't propagated into the next plan's Global Constraints in time. This design doc explicitly bakes it in up front, per the concrete process fix from B1's final review.

## API Shape

| Method | Route | Behavior |
|---|---|---|
| `POST` | `/api/v1/admin/applications/{appId}/api-keys` | Create. Body: `{ label }`. Response: `{ id, applicationId, label, createdAt, apiKey }` — `apiKey` (the raw secret) appears **only** in this one response. `404` if `appId` doesn't exist (parent-existence guard, same as every prior entity). |
| `GET` | `/api/v1/admin/applications/{appId}/api-keys` | List. Active-only (`RevokedAt IS NULL`) by default, `?includeRevoked=true` for all. Items: `{ id, applicationId, label, createdAt, revokedAt }` — **never** `apiKey` or `KeyHash`. |
| `GET` | `/api/v1/admin/applications/{appId}/api-keys/{id}` | Single. `404` if not found or belongs to a different application (IDOR guard, same pattern as every prior entity). Same response shape as the list item (no `apiKey`/`KeyHash`). |
| `DELETE` | `/api/v1/admin/applications/{appId}/api-keys/{id}` | Revoke. Idempotent — `204` whether this call actually revoked the key or it was already revoked. `404` on not-found/cross-application, same IDOR guard. |

No `PUT` — see "Why This Isn't Shaped Like B1" above.

## UI: Fourth Subsection on `ApplicationsAdmin.razor`'s Row Expansion

Same self-contained-component pattern as B1's `CustomersSection`/`UsersSection`/`LogSourcesSection`: a new `ApiKeysSection.razor` (`[Parameter] public int ApplicationId`), instantiated once per expanded row, added directly after `LogSourcesSection` in the markup.

- **Table:** `Label`, `Created At` — revoked keys simply drop out of the default list (matching B1's established "no visible status column" convention), no `?includeRevoked=true` toggle in this UI.
- **Create form:** `Label` only (`maxlength="200"`, matching every prior string field). No external-id-style second field.
- **On successful create:** the raw key renders once, in a `<pre>`/`<code>` block directly above the table, with explicit copy-it-now wording ("This is the only time you will see this key — copy it now."). Manually selectable text; no clipboard JS interop, keeping this plan free of any new client-side dependency. The banner clears on the next create or on navigating away (component-local state only, nothing persisted).
- **Revoke button** per row, no confirmation prompt — matches the existing no-confirm convention for Deactivate everywhere else in this project.
- No inline-edit/Rename toggle anywhere in this component — there is nothing to rename.

## Testing

Same posture as every prior plan: real integration tests (LocalDB) for the repository and controller. The UI task's "manual smoke check" step uses the corrected code-inspection instructions from B1's fixed plan doc (`curl` cannot reach content behind `ApplicationsAdmin.razor`'s row-expansion toggle — this plan copies that already-corrected guidance rather than reintroducing the mistake).

One repository test worth calling out explicitly since it has no B1 analog: verify `RevokeAsync` is idempotent — revoke twice, assert `RevokedAt` is unchanged between the two calls (not just non-null).

## Non-Goals (explicit)

- **Ingestion-side authentication** (`X-Api-Key` header → hash lookup → `Application`) — that middleware belongs to M2 (Ingestion), which doesn't exist yet. This plan only makes `ApiKey` records manageable and stores `KeyHash` with a lookup-friendly index ready for M2 to query against.
- `AppVersion`/`Deployment` (B3) — separate, later plan.
- No change to Group A or B1's already-shipped code/UI.
