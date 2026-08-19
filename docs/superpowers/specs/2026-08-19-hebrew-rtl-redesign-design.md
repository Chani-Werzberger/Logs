# Hebrew RTL UI Redesign — Design

**Status:** Approved by user, ready for implementation planning
**Part of:** cross-cutting design/localization pass over the Admin UI shipped in M0+M1 (Groups A, B1, B2, B3 — all merged to `main`). Not part of M2 (Ingestion); this changes no entities, endpoints, or C# logic — markup, styling, and text only.

## Current State

The Admin UI (`ApplicationsAdmin.razor`, `ModulesAdmin.razor`, `ScreenServicesAdmin.razor`, `ProcessesAdmin.razor`, `OperationsAdmin.razor`, and six `Shared/*Section.razor` components — `ApiKeysSection`, `CustomersSection`, `DeploymentsSection`, `LogSourcesSection`, `UsersSection`, `VersionsSection`) is completely unstyled: no `wwwroot`, no CSS, no `MainLayout`/`NavMenu` — `Routes.razor` renders bare `RouteView`s with raw `<table>`/`<EditForm>` HTML. All text is English. Pages are reachable only via direct URL; there is no persistent navigation. This matches the project's own `09-UI-Design.md` §9 ("desktop-first, information density over visual polish") but the doc's own IA (§2: What's Unusual / Search / Exceptions / Admin, with a persistent Application+Environment header) was never built — only the raw Admin CRUD screens exist so far.

All 11 CRUD files follow one identical pattern (confirmed by inspection): an HTML table of existing rows with inline per-row Edit/Deactivate, and a create form below it, with `<p style="color:red">` for errors. This is a single cross-cutting pass, not several independent pieces — no decomposition needed.

## Goal

Make the existing screens look like a real tool, in Hebrew, RTL — without changing behavior. Every field, validation rule, and repository/controller call stays exactly as-is; only markup, CSS classes, and displayed text change.

## Terminology Decision

Confirmed with the user: **technical/entity nouns stay in English** inside the Hebrew UI, matching the convention already used throughout `מסמכי-אפיון/*.md` (e.g. "בחירת Application → טאבים: Environments, Versions"). Only UI chrome (actions, labels, messages) is translated.

| Stays English | Hebrew |
|---|---|
| Application, Module, ScreenService, Process, Operation, Environment, Version, Deployment, Customer, User, API Key, Log Source | — |
| Create | צור |
| Add [X] | הוסף [X] |
| Save / Cancel | שמור / בטל |
| Edit | ערוך |
| Deactivate | השבת |
| Revoke | בטל תוקף |
| Name / Description | שם / תיאור |
| Created At / Deployed At | נוצר בתאריך / תאריך פריסה |
| Label / Notes | תווית / הערות |
| Is Production | סביבת ייצור |
| Version Number / Release Notes | מספר גרסה / הערות גרסה |
| External Customer/User Id / Display Name | מזהה לקוח/משתמש חיצוני / שם תצוגה |
| "already exists" / "not found" errors | translated per-message, same wording pattern as today (e.g. `An application named 'X' already exists.` → `Application בשם 'X' כבר קיים.`) |

## Foundation (new)

- **Vendor Bootstrap 5.3, RTL build**, self-hosted (no CDN): `wwwroot/lib/bootstrap/bootstrap.rtl.min.css` + `wwwroot/lib/bootstrap/bootstrap.bundle.min.js`, downloaded once and committed. `app.UseStaticFiles()` is already wired in `Program.cs`; `wwwroot` itself doesn't exist yet and gets created by this change.
- `wwwroot/css/site.css` — small app-specific tweaks only (e.g. spacing for the nested row-expansion sections in `ApplicationsAdmin.razor`).
- `App.razor` — `<html lang="he" dir="rtl">`, links the two vendored assets, `<title>` becomes `LogsPlatform` (kept as the product name, unchanged).
- **New `Components/Layout/MainLayout.razor`** — Bootstrap navbar header + `@Body` in a container. Applied as the default layout so all five existing pages stop being orphaned/bare (wired via `_Imports.razor`'s `@layout`, no per-page changes needed).
- **New `Components/Layout/NavMenu.razor`** — full IA from `09-UI-Design.md` §2: **מה חריג** (What's Unusual), **חיפוש** (Search), **חריגות** (Exceptions) rendered as disabled nav links with a "בקרוב" badge (not yet built — M2+), and **ניהול** (Admin) active, linking to `/admin/applications`.

## Page/Component Restyling Pattern

Applied identically across all 11 CRUD files:

- Tables → `table table-striped table-hover align-middle`
- Create forms → wrapped in a `card` titled "הוספת [X]", `form-label`/`form-control` on inputs
- Buttons → `btn btn-primary` (create/save), `btn btn-outline-secondary` (cancel/edit), `btn btn-outline-danger` (deactivate/revoke)
- Error messages → Bootstrap `alert alert-danger` (replaces `<p style="color:red">`)
- `ModulesAdmin.razor`'s manual breadcrumb (hand-built `&gt;` separator loop) → Bootstrap `<nav><ol class="breadcrumb">`, which renders the separator correctly under `dir="rtl"` automatically. **No change to `BreadcrumbBuilder.cs`** — it already returns plain `(Label, Url)` segments; only the markup consuming them changes.

**Deliberately no new shared/generic component** (e.g. a generic `AdminCrudTable<T>`) — despite the 11 files sharing a near-identical shape, extracting one now would be a behavioral refactor across every CRUD screen at once, which is a different (and riskier) kind of change than the pure markup/text swap this pass is scoped to. Each file keeps its existing structure; only classes and strings change in place.

## Files Touched

**New:**
- `wwwroot/lib/bootstrap/bootstrap.rtl.min.css`, `wwwroot/lib/bootstrap/bootstrap.bundle.min.js`
- `wwwroot/css/site.css`
- `Components/Layout/MainLayout.razor`
- `Components/Layout/NavMenu.razor`

**Modified (markup/CSS classes/text only, no `@code` behavior changes):**
- `App.razor`, `_Imports.razor`
- `Components/Pages/ApplicationsAdmin.razor`, `ModulesAdmin.razor`, `ScreenServicesAdmin.razor`, `ProcessesAdmin.razor`, `OperationsAdmin.razor`
- `Components/Shared/ApiKeysSection.razor`, `CustomersSection.razor`, `DeploymentsSection.razor`, `LogSourcesSection.razor`, `UsersSection.razor`, `VersionsSection.razor`

## Testing

No new automated tests. This is a pure markup/CSS/text change with zero behavior change — every existing Controller-level integration test (API-only, no Razor rendering involved) is unaffected. Verification is manual: run `LogsPlatform.Web` (`dotnet run`), walk through all five admin pages and six shared sections, confirm RTL layout renders correctly, Hebrew text displays properly, and every create/edit/deactivate/revoke flow still works exactly as before.

## Non-Goals (explicit)

- No new entities, endpoints, or repository/controller logic.
- No change to any validation rule, error condition, or data flow.
- No i18n infrastructure (resource files, culture switching, language toggle) — Hebrew is hardcoded directly in markup; there's no requirement for a bilingual UI, so a resource-based localization layer would be unused complexity.
- No generic/shared CRUD component extraction (see above).
- No work on What's Unusual / Search / Exceptions / Timeline — those nav entries are placeholders pointing at nothing, matching where the project actually is (M2 not started).
