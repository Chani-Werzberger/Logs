# M6: Security Hardening — Design

**חלק מ:** R&D Logs Platform — Milestone M6
**תאריך:** 2026-08-23
**מבוסס על:** [10-Security-Design.md](../../../מסמכי-אפיון/10-Security-Design.md) (primary source), [04-ארכיטקטורה.md](../../../מסמכי-אפיון/04-ארכיטקטורה.md) §9, [11-Test-Strategy.md](../../../מסמכי-אפיון/11-Test-Strategy.md) §6, [07-Ingestion-ו-API.md](../../../מסמכי-אפיון/07-Ingestion-ו-API.md) §7 (redaction hook)
**Follows:** M0–M5, all merged to `main`, including the go/no-go Scenario Test (M5), passed.

## 1. Goal

Every Admin/Query/Findings HTTP endpoint and every Blazor page is currently wide open — no login exists anywhere in the app. M6 closes that: real username/password authentication, an `IsAdmin`/non-admin authorization split, an authenticated audit trail for the one action the spec calls out as epistemically sensitive (Hypothesis→Conclusion promotion), a redaction extension point in the Client library, and a pre-commit secrets scan.

## 2. Scope decisions locked before this design started

- **Password hashing:** custom PBKDF2 via `Rfc2898DeriveBytes.Pbkdf2` — no new NuGet dependency, mirrors `ApiKeyHasher`'s existing fully-custom precedent exactly.
- **First-admin bootstrap:** seeded automatically on startup if no `PlatformUser` rows exist (fixed username, a generated password printed to the console once) — no manual DB step.
- **Entity name: `PlatformUser`**, not `AppUser` — a person who logs into LogsPlatform's own UI, unrelated to and unambiguous against the existing `AppUser` (an end-user of a *connected* application, e.g. one of RetailPulse's customers, scoped per-Application). `PlatformUser` is top-level, not scoped to any Application, matching the spec's explicit "no per-Application permissions in V1 — every authenticated user sees every Application."
- **Promote-to-Conclusion's `ApprovedBy`** is no longer free text — it's read from the authenticated `PlatformUser`'s `Username` automatically. Locked in specifically because `10`'s §7 names this the one action that always gets audited; a free-text field an approver could mistype or fake undercuts that guarantee now that real identity exists.
- **Scope includes the HTTP API surface, not just the Blazor UI.** Cookie auth gates `EventsController`, `TimelineController`, `ExceptionGroupsController`, `FindingsController`, and every Admin controller — closing the real gap `10`'s §1 threat model names explicitly ("an outsider/employee who shouldn't see data leaking into another app/environment via the API"). `IngestionController` keeps its own untouched `X-Api-Key` scheme.
- **Real ripple effect, accepted knowingly:** every existing `WebApplicationFactory`-based test that calls these controllers directly (dozens of files across M1–M5) needs an authenticated `HttpClient` or it starts getting 401s. Handled by one shared "log in and get a client" helper, not a test-only auth bypass — the whole point of gating these endpoints is to actually test that the gate works, so tests can't just skip it.

## 3. Architecture

**`PlatformUser` entity** (`src/LogsPlatform.Domain/Entities/PlatformUser.cs`): `Id`, `Username` (unique), `PasswordHash`, `IsAdmin`, `IsActive`, `CreatedAt`. No `ApplicationId` — top-level.

**`PasswordHasher`** (`src/LogsPlatform.Infrastructure/PasswordHasher.cs`, mirroring `ApiKeyHasher`'s location/shape): `Hash(string password) -> string` produces `{iterations}.{saltBase64}.{hashBase64}` via `Rfc2898DeriveBytes.Pbkdf2` (SHA-256, 100,000 iterations, 16-byte salt, 32-byte output); `Verify(string password, string hash) -> bool` re-derives with the stored salt/iteration count and compares in constant time (`CryptographicOperations.FixedTimeEquals`).

**Cookie authentication.** `Program.cs` adds `AddAuthentication(CookieAuthenticationOptions.DefaultScheme).AddCookie(...)` as the **default** scheme (unlike the API-key scheme, which stays explicitly opt-in via `[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]` on `IngestionController` only, untouched). A global `[Authorize]` fallback policy applies to every controller and every Blazor page by default; a second policy (`RequireAdmin`, checking an `IsAdmin` claim) gates the Admin section and its write endpoints specifically.

**Login flow.** New `AuthController` (`POST /api/v1/auth/login` — `{ username, password }` → `204` + sets the auth cookie via `HttpContext.SignInAsync`, or `401` on bad credentials; `POST /api/v1/auth/logout` → `HttpContext.SignOutAsync`). A new `/login` Blazor page (unauthenticated, the one page excluded from the global `[Authorize]` fallback) posts to it and redirects to `/` on success.

**Startup seeding.** In `Program.cs`, after `app.Build()` and before `app.Run()`: if `PlatformUsers` is empty, generate a random password, hash it, insert one `PlatformUser { Username = "admin", IsAdmin = true }`, and `Console.WriteLine` the plaintext password once (never persisted anywhere else, never logged to a file).

**Admin UI for `PlatformUser` management** — a new Admin tab (`PlatformUsersSection.razor`, clearly distinct from the existing `UsersSection.razor` which manages `AppUser`): list/create/deactivate `PlatformUser` rows, admin-only. No self-service signup, no password-reset flow (out of scope for V1 — an admin resets by deactivating and recreating, a known, documented limitation).

**Promote-to-Conclusion change.** `FindingsController.Promote` and `FindingDetail.razor`'s promote action both drop the `approvedBy` request field entirely; the controller reads `User.FindFirstValue(ClaimTypes.Name)` (the cookie's own username claim) and passes that to `IFindingRepository.PromoteToConclusionAsync` instead.

**Redaction hook** (`src/LogsPlatform.Client/LogsPlatformClient.cs`): a new optional constructor parameter `Func<string, string>? redactMessage = null`. When supplied, `SendEventAsync` applies it to `EventPayload.Message` before buffering, and to every `string`-typed value in `Metadata` (non-string values pass through unchanged) — matching `07`'s exact scope ("Message/Metadata... before sending from the client"). No default redaction logic ships; the mechanism exists, the policy is the connecting developer's call, per the spec's explicit "not automatic PII detection" boundary.

**Secrets scan.** `.githooks/pre-commit` (bash, executable) — `git diff --cached --name-only` piped through a small grep pattern set (`Password=`, `pwd=`, AWS-key-shaped strings, `-----BEGIN...PRIVATE KEY-----`, a bare SQL Server connection string outside `appsettings.Development.json`/User Secrets) against staged file contents; blocks the commit with a clear message on a hit. `README.md` gains one line: `git config core.hooksPath .githooks` (git doesn't auto-install hooks from a cloned repo — this is a one-time local setup step, documented, not enforced).

## 4. Data flow — Query/Admin/Findings request lifecycle

Unauthenticated request → cookie missing/invalid → ASP.NET Core's cookie middleware redirects a browser page request to `/login` (Blazor) or returns `401` for an API call (`FindingsController` etc., since these aren't page navigations). Authenticated-but-non-admin request to an Admin-gated resource → `403`. Everything else proceeds exactly as today — no repository/query-layer changes; the `ApplicationId`/`EnvironmentId` scoping `10`'s §4 already enforces at the repository level is unaffected and untouched by this milestone.

## 5. Testing approach

`11`'s §6 checklist, directly: missing/wrong/revoked API key → `401` (extends the existing `ApiKeyAuthenticationHandlerTests`-equivalent coverage — currently missing a revoked-key case, will confirm and add if absent); `PlatformUser` with `IsAdmin=false` hitting an Admin-only endpoint → `403`; a fresh `PlatformUser` row's `PasswordHash` is never the plaintext password (a direct DB read in a test, per this project's established "verify the actual write, not just the response" convention); `LogsPlatformClient`'s redaction hook test confirms a message is actually transformed before the HTTP call fires (using the existing `HttpClient?`-injection seam from M2b, no new test infra needed there). A shared `AuthenticatedTestClientHelper` (seeds a `PlatformUser` directly via `DbContext` — fast, no HTTP round-trip needed to establish it — then performs one real `POST /api/v1/auth/login` call and returns the resulting cookie-bearing `HttpClient`) is added early and used by every existing test file this milestone's auth gating breaks.

## 6. Out of scope for M6

Per-Application permissions/granular RBAC (explicitly deferred to V2 per `10`'s §3). General admin-action audit logging beyond Promote-to-Conclusion (explicitly deferred per `10`'s §7). Automatic PII detection (explicitly out of scope per `10`'s §5 — the redaction hook is a mechanism, not a policy). Password reset / self-service signup. TLS enforcement (not relevant to local-only V1 per `10`'s §9, a documented V2 blocker). Retention/archival policy (`10`'s §8, unrelated to this milestone).
