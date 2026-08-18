# Blazor Application Admin UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the first UI in the product — a single Blazor Server page at `/admin/applications` in the existing `LogsPlatform.Web` project, letting a user view/create `Application`s and view/create `AppEnvironment`s for any of them, using the same repositories the JSON API already uses.

**Architecture:** Blazor Server components inject `IApplicationRepository`/`IAppEnvironmentRepository` directly via DI (same process as the API, per the Modular Monolith architecture) — no HTTP calls to the app's own API. Full design rationale: `docs/superpowers/specs/2026-08-17-blazor-application-admin-ui-design.md`.

**Tech Stack:** ASP.NET Core Razor Components (Blazor Server / "Interactive Server" render mode), .NET 10, added to the existing `LogsPlatform.Web` project — no new packages required (Razor Components support ships in the ASP.NET Core shared framework already referenced).

## Global Constraints

- No authentication — matches the API's current state (deferred per Security Design). Do not add a login screen or any auth check.
- No update/delete for Applications or Environments — the API doesn't support them; don't add UI for operations that don't exist yet.
- No custom CSS — plain HTML tables/forms only. Native HTML5 `required` attribute is the only client-side validation (no `DataAnnotationsValidator`, no custom validation layer).
- Duplicate-`Application.Name` handling in the Blazor page is a **deliberate, small duplication** of `ApplicationsController.Create`'s `catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })` logic (Task 8 of the prior plan) — not a bug, not something to "fix" by extracting a shared service in this plan. Documented as a deferred cost in the design doc.
- `AppEnvironment`'s own duplicate-name-per-application constraint is **not** specifically caught anywhere in this plan (matches `EnvironmentsController`'s own current, accepted gap — an unhandled exception surfaces as Blazor's default error UI). Do not add handling for it.
- No new automated UI-test framework (no bUnit, no Playwright) — verification per task is `dotnet build` + `dotnet test` (full existing suite, to catch any regression from the shared `Program.cs` change) + `curl` against the running app to confirm the expected HTML structure is present. Full interactive verification (clicking, submitting forms) happens after all tasks are done, in an actual browser — noted explicitly at the end of Task 3, not something any task's implementer needs to fake or skip silently.
- Namespace convention: files under `src/LogsPlatform.Web/Components/` get namespace `LogsPlatform.Web.Components` by default (root namespace `LogsPlatform.Web` + folder path) — no explicit `@namespace` directive needed in any `.razor` file below.

---

### Task 1: Blazor Server hosting infrastructure + read-only Applications list

**Files:**
- Modify: `src/LogsPlatform.Web/Program.cs`
- Create: `src/LogsPlatform.Web/Components/_Imports.razor`
- Create: `src/LogsPlatform.Web/Components/App.razor`
- Create: `src/LogsPlatform.Web/Components/Routes.razor`
- Create: `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor`

**Interfaces:**
- Consumes: `IApplicationRepository.GetAllAsync()` (already exists, from the prior plan's Task 2/4).
- Produces: a working `/admin/applications` route rendering a read-only table — the page Task 2 and Task 3 will modify further.

**Regression risk this task specifically carries:** `Program.cs` is the file `TestWebApplicationFactory` boots via `WebApplicationFactory<Program>` for all 9 existing tests. Adding Razor Components hosting to it must not break any of them — Step 3 below is not optional.

> **Correction (post-Task 1):** the original code blocks below were missing two standard, necessary pieces of ASP.NET Core's Blazor Web App hosting model, both already folded into the code shown now: (1) `_Imports.razor` needs `@using static Microsoft.AspNetCore.Components.Web.RenderMode` — without it, `@rendermode="InteractiveServer"` fails to compile with `CS0103` (`InteractiveServer` is a static member of `RenderMode`, not a bare type); this line is present in the official .NET Blazor Web App template for exactly this reason. (2) `Program.cs` needs `app.UseAntiforgery();` between `UseStaticFiles()` and `MapControllers()`/`MapRazorComponents()` — without it, every component request throws `InvalidOperationException: Endpoint ... contains anti-forgery metadata, but a middleware was not found that supports anti-forgery`, because `MapRazorComponents` attaches anti-forgery metadata to its endpoints by default in this hosting model. Both were discovered and fixed by Task 1's implementer, re-verified against the full 9-test suite before and after.

- [ ] **Step 1: Write the root Razor Components files**

```razor
@* src/LogsPlatform.Web/Components/_Imports.razor *@
@using System.Net.Http
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.JSInterop
@using LogsPlatform.Web.Components
@using static Microsoft.AspNetCore.Components.Web.RenderMode
```

```razor
@* src/LogsPlatform.Web/Components/App.razor *@
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="/" />
    <title>LogsPlatform</title>
    <HeadOutlet @rendermode="InteractiveServer" />
</head>
<body>
    <Routes @rendermode="InteractiveServer" />
    <script src="_framework/blazor.web.js"></script>
</body>
</html>
```

```razor
@* src/LogsPlatform.Web/Components/Routes.razor *@
<Router AppAssembly="typeof(Program).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="routeData" />
    </Found>
    <NotFound>
        <p>Page not found.</p>
    </NotFound>
</Router>
```

- [ ] **Step 2: Wire up Razor Components hosting in `Program.cs`**

Replace the full file content with:

```csharp
// src/LogsPlatform.Web/Program.cs
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Web.Components;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContext<LogsPlatformDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("LogsPlatformDb")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:LogsPlatformDb configuration.")));

builder.Services.AddScoped<IApplicationRepository, ApplicationRepository>();
builder.Services.AddScoped<IAppEnvironmentRepository, AppEnvironmentRepository>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program
{
} // exposes Program for WebApplicationFactory<Program> in tests
```

- [ ] **Step 3: Build, then run the full existing test suite to confirm no regression**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` (aside from the pre-existing NU1902/NU1901 NuGet advisory warnings already present before this task).

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 9, Skipped: 0, Total: 9` — the exact same 9 tests that passed before this task. If any fail, STOP — do not proceed to Step 4 until you understand why adding Razor Components hosting broke an existing test; this is exactly the regression risk called out above.

- [ ] **Step 4: Write the read-only Applications page**

```razor
@* src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor *@
@page "/admin/applications"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@inject IApplicationRepository ApplicationRepository
@rendermode InteractiveServer

<h1>Applications</h1>

<h2>All Applications</h2>
<table>
    <thead>
        <tr>
            <th>Name</th>
            <th>Description</th>
            <th>Created At</th>
        </tr>
    </thead>
    <tbody>
        @foreach (var application in _applications)
        {
            <tr>
                <td>@application.Name</td>
                <td>@application.Description</td>
                <td>@application.CreatedAt</td>
            </tr>
        }
    </tbody>
</table>

@code {
    private List<Application> _applications = new();

    protected override async Task OnInitializedAsync()
    {
        _applications = (await ApplicationRepository.GetAllAsync()).ToList();
    }
}
```

- [ ] **Step 5: Build again**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 6: Start the app and verify the page renders via curl**

```bash
dotnet run --project src/LogsPlatform.Web --launch-profile http &
sleep 5
curl -s http://localhost:5201/admin/applications
```

Expected: HTML output containing `<h1>Applications</h1>`, `<table>`, and `<th>Name</th>` — confirming the page route resolves and the component prerenders server-side (Blazor Server prerenders the initial HTTP response before the SignalR circuit attaches, so `curl` can see this even though it can't test interactivity). Stop the background process afterward — `kill %1` does not reliably stop it on this Windows/Git-Bash environment since `dotnet run` spawns a child process; use `taskkill //F //IM dotnet.exe` instead (verify with `netstat -ano | grep 5201` that nothing is left listening).

If curl returns a 404 or an error page instead: check that `app.MapRazorComponents<App>()` in `Program.cs` is actually being reached (not short-circuited by an earlier `return`/exception) and that `Components/App.razor`'s namespace resolves correctly (it must be `LogsPlatform.Web.Components` for the `using LogsPlatform.Web.Components;` in `Program.cs` to find it).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Program.cs src/LogsPlatform.Web/Components/
git commit -m "Add Blazor Server hosting + read-only Applications list page"
```

---

### Task 2: Create Application form + duplicate-name error handling

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor` (full replacement)

**Interfaces:**
- Consumes: `IApplicationRepository.AddAsync(Application)` (existing).
- Produces: a working create-application form on the same page — Task 3 adds environments below this.

- [ ] **Step 1: Replace the page with the create-form version**

```razor
@* src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor *@
@page "/admin/applications"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using Microsoft.EntityFrameworkCore
@using Microsoft.Data.SqlClient
@inject IApplicationRepository ApplicationRepository
@rendermode InteractiveServer

<h1>Applications</h1>

<h2>Create Application</h2>
<EditForm Model="_newApplication" OnValidSubmit="CreateApplicationAsync">
    <div>
        <label>
            Name:
            <InputText @bind-Value="_newApplication.Name" required />
        </label>
    </div>
    <div>
        <label>
            Description:
            <InputText @bind-Value="_newApplication.Description" />
        </label>
    </div>
    <button type="submit">Create</button>
</EditForm>
@if (_createError is not null)
{
    <p style="color:red">@_createError</p>
}

<h2>All Applications</h2>
<table>
    <thead>
        <tr>
            <th>Name</th>
            <th>Description</th>
            <th>Created At</th>
        </tr>
    </thead>
    <tbody>
        @foreach (var application in _applications)
        {
            <tr>
                <td>@application.Name</td>
                <td>@application.Description</td>
                <td>@application.CreatedAt</td>
            </tr>
        }
    </tbody>
</table>

@code {
    private List<Application> _applications = new();
    private readonly NewApplicationModel _newApplication = new();
    private string? _createError;

    protected override async Task OnInitializedAsync()
    {
        _applications = (await ApplicationRepository.GetAllAsync()).ToList();
    }

    private async Task CreateApplicationAsync()
    {
        _createError = null;
        try
        {
            await ApplicationRepository.AddAsync(new Application
            {
                Name = _newApplication.Name,
                Description = _newApplication.Description,
                CreatedAt = DateTime.UtcNow
            });

            _newApplication.Name = string.Empty;
            _newApplication.Description = null;
            _applications = (await ApplicationRepository.GetAllAsync()).ToList();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            _createError = $"An application named '{_newApplication.Name}' already exists.";
        }
    }

    private class NewApplicationModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 9, Skipped: 0, Total: 9` — unchanged from Task 1 (this task only touches the Razor page, not anything the existing tests exercise).

- [ ] **Step 4: Start the app and verify the form markup renders via curl**

```bash
dotnet run --project src/LogsPlatform.Web --launch-profile http &
sleep 5
curl -s http://localhost:5201/admin/applications
```

Expected: HTML output now also contains `Create Application`, an `<input` element, and `<button type="submit">Create</button>` alongside the table from Task 1. Stop the background process afterward with `taskkill //F //IM dotnet.exe` (see Task 1's note — `kill %1` doesn't reliably work on this environment).

**Note on what this does NOT verify:** curl cannot submit the form (that requires a real browser's SignalR/WebSocket client) — this step confirms the markup is present and correct, not that clicking Create actually works end-to-end. Full interactive verification happens after Task 3 (see Task 3's final step).

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor
git commit -m "Add Create Application form with duplicate-name error handling"
```

---

### Task 3: Environment expansion + Add Environment form

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor` (full replacement)

**Interfaces:**
- Consumes: `IAppEnvironmentRepository.GetByApplicationIdAsync(int)`, `IAppEnvironmentRepository.AddAsync(AppEnvironment)` (both existing).
- Produces: the complete page — nothing further modifies this file in this plan.

- [ ] **Step 1: Replace the page with the final version**

```razor
@* src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor *@
@page "/admin/applications"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using Microsoft.EntityFrameworkCore
@using Microsoft.Data.SqlClient
@inject IApplicationRepository ApplicationRepository
@inject IAppEnvironmentRepository EnvironmentRepository
@rendermode InteractiveServer

<h1>Applications</h1>

<h2>Create Application</h2>
<EditForm Model="_newApplication" OnValidSubmit="CreateApplicationAsync">
    <div>
        <label>
            Name:
            <InputText @bind-Value="_newApplication.Name" required />
        </label>
    </div>
    <div>
        <label>
            Description:
            <InputText @bind-Value="_newApplication.Description" />
        </label>
    </div>
    <button type="submit">Create</button>
</EditForm>
@if (_createError is not null)
{
    <p style="color:red">@_createError</p>
}

<h2>All Applications</h2>
<table>
    <thead>
        <tr>
            <th></th>
            <th>Name</th>
            <th>Description</th>
            <th>Created At</th>
        </tr>
    </thead>
    <tbody>
        @foreach (var application in _applications)
        {
            <tr>
                <td>
                    <button @onclick="() => ToggleExpandAsync(application.Id)">
                        @(_expandedAppIds.Contains(application.Id) ? "-" : "+")
                    </button>
                </td>
                <td>@application.Name</td>
                <td>@application.Description</td>
                <td>@application.CreatedAt</td>
            </tr>
            @if (_expandedAppIds.Contains(application.Id))
            {
                <tr>
                    <td colspan="4">
                        <h4>Environments</h4>
                        <table>
                            <thead>
                                <tr>
                                    <th>Name</th>
                                    <th>Is Production</th>
                                </tr>
                            </thead>
                            <tbody>
                                @if (_environmentsByAppId.TryGetValue(application.Id, out var environments))
                                {
                                    @foreach (var environment in environments)
                                    {
                                        <tr>
                                            <td>@environment.Name</td>
                                            <td>@environment.IsProduction</td>
                                        </tr>
                                    }
                                }
                            </tbody>
                        </table>

                        @if (_newEnvironmentModels.TryGetValue(application.Id, out var newEnvironment))
                        {
                            <EditForm Model="newEnvironment" OnValidSubmit="() => CreateEnvironmentAsync(application.Id)">
                                <label>
                                    Name:
                                    <InputText @bind-Value="newEnvironment.Name" required />
                                </label>
                                <label>
                                    Is Production:
                                    <InputCheckbox @bind-Value="newEnvironment.IsProduction" />
                                </label>
                                <button type="submit">Add Environment</button>
                            </EditForm>
                        }
                    </td>
                </tr>
            }
        }
    </tbody>
</table>

@code {
    private List<Application> _applications = new();
    private readonly NewApplicationModel _newApplication = new();
    private string? _createError;

    private readonly HashSet<int> _expandedAppIds = new();
    private readonly Dictionary<int, List<AppEnvironment>> _environmentsByAppId = new();
    private readonly Dictionary<int, NewEnvironmentModel> _newEnvironmentModels = new();

    protected override async Task OnInitializedAsync()
    {
        _applications = (await ApplicationRepository.GetAllAsync()).ToList();
    }

    private async Task CreateApplicationAsync()
    {
        _createError = null;
        try
        {
            await ApplicationRepository.AddAsync(new Application
            {
                Name = _newApplication.Name,
                Description = _newApplication.Description,
                CreatedAt = DateTime.UtcNow
            });

            _newApplication.Name = string.Empty;
            _newApplication.Description = null;
            _applications = (await ApplicationRepository.GetAllAsync()).ToList();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            _createError = $"An application named '{_newApplication.Name}' already exists.";
        }
    }

    private async Task ToggleExpandAsync(int applicationId)
    {
        if (_expandedAppIds.Contains(applicationId))
        {
            _expandedAppIds.Remove(applicationId);
            return;
        }

        _expandedAppIds.Add(applicationId);

        if (!_environmentsByAppId.ContainsKey(applicationId))
        {
            _environmentsByAppId[applicationId] =
                (await EnvironmentRepository.GetByApplicationIdAsync(applicationId)).ToList();
        }

        if (!_newEnvironmentModels.ContainsKey(applicationId))
        {
            _newEnvironmentModels[applicationId] = new NewEnvironmentModel();
        }
    }

    private async Task CreateEnvironmentAsync(int applicationId)
    {
        var model = _newEnvironmentModels[applicationId];

        await EnvironmentRepository.AddAsync(new AppEnvironment
        {
            ApplicationId = applicationId,
            Name = model.Name,
            IsProduction = model.IsProduction
        });

        _newEnvironmentModels[applicationId] = new NewEnvironmentModel();
        _environmentsByAppId[applicationId] =
            (await EnvironmentRepository.GetByApplicationIdAsync(applicationId)).ToList();
    }

    private class NewApplicationModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    private class NewEnvironmentModel
    {
        public string Name { get; set; } = string.Empty;
        public bool IsProduction { get; set; }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Run the full existing test suite**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 9, Skipped: 0, Total: 9` — unchanged.

- [ ] **Step 4: Start the app and verify the full markup renders via curl**

```bash
dotnet run --project src/LogsPlatform.Web --launch-profile http &
sleep 5
curl -s http://localhost:5201/admin/applications
```

Expected: HTML output containing everything from Task 2 plus at least one row with an expand-toggle button (`+`) in the first column of the Applications table (present as long as at least one `Application` already exists in the target database — if the table is genuinely empty, the toggle buttons simply won't appear yet, which is correct, not a bug: create one via the form first, per Task 2, to get a row to expand). Stop the background process afterward with `taskkill //F //IM dotnet.exe` (see Task 1's note — `kill %1` doesn't reliably work on this environment).

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor
git commit -m "Add Environment expansion and Add Environment form to Applications page"
```

- [ ] **Step 6: Flag remaining interactive verification for the controller (not this task's implementer to attempt)**

Everything curl-verifiable across all 3 tasks has been checked. What remains — actually clicking the expand toggle, submitting both forms, and confirming a duplicate-name submission shows the inline error — requires a real browser (SignalR/WebSocket client), which this implementer does not have. Report this explicitly as a note in your final report rather than silently treating curl's structural checks as full verification: state plainly that interactive behavior (click/submit paths) has not been exercised end-to-end and needs a human or browser-automation check before this is considered fully proven, not just "should work because the code looks right."

---

## Self-Review Notes

- **Spec coverage:** Every section of `docs/superpowers/specs/2026-08-17-blazor-application-admin-ui-design.md` maps to a task here — hosting setup + read-only list (Task 1), create form + duplicate handling (Task 2), environment expansion + add form (Task 3). The design's explicit Non-Goals (no auth, no update/delete, no custom CSS, no new test framework) are respected — none of the three tasks add any of them.
- **Type consistency:** `IApplicationRepository`/`IAppEnvironmentRepository` method signatures (`GetAllAsync()`, `AddAsync(Application)`, `GetByApplicationIdAsync(int)`, `AddAsync(AppEnvironment)`) are used identically across all three tasks and match the actual interfaces defined in the prior plan's Task 2 — verified by re-reading `src/LogsPlatform.Domain/Repositories/IApplicationRepository.cs` and `IAppEnvironmentRepository.cs` before writing this plan.
- **No placeholders:** every step has complete, runnable `.razor`/`.cs` code or an exact command with an expected result — including the explicit, honest acknowledgment (Task 3 Step 6) of what curl-based verification cannot prove, rather than glossing over it.
- **Regression risk called out explicitly:** Task 1's Step 3 (full test suite run after modifying the shared `Program.cs`) is the one place in this plan where an existing, working system could break — flagged as non-optional, not folded silently into "build succeeds."

## After This Plan

Interactive, in-browser verification of the full click/submit flow (by the controller or the user) — the natural finish for this plan, since no task's implementer can do it themselves. Beyond that: the "Next Plans" items from the prior plan remain open (replicate the entity pattern for the rest of the hierarchy; `LogsPlatform.Client` + Ingestion API for M2).
