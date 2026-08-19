# Hebrew RTL UI Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restyle the existing unstyled English Admin UI (5 pages + 6 shared sections) into a Bootstrap-based, RTL, Hebrew interface — markup, CSS classes, and displayed text only, zero behavior change.

**Architecture:** Vendor Bootstrap 5.3 (RTL build) into `wwwroot/lib/bootstrap/`, add a `MainLayout`/`NavMenu` shell (none exists today), then pass over each of the 11 existing CRUD files swapping raw HTML for Bootstrap classes and English text for Hebrew, leaving every `@code` block's logic untouched except the literal text inside existing error-message string interpolations.

**Tech Stack:** ASP.NET Core / Blazor Server (.NET 10, `net10.0`), Bootstrap 5.3.3 (RTL build, self-hosted).

## Global Constraints

- Blazor Server, `InteractiveServer` render mode, `net10.0`, nullable enabled — matches every existing file in `src/LogsPlatform.Web`.
- RTL: `<html lang="he" dir="rtl">`; Bootstrap's RTL build (`bootstrap.rtl.min.css`) handles mirroring — no manual `float`/`margin-left` overrides.
- Self-hosted only — Bootstrap CSS/JS are vendored into `wwwroot/lib/bootstrap/` and committed to git; no CDN `<link>`/`<script>` tags anywhere.
- Terminology: **Application, Module, ScreenService, Process, Operation, Environment, Version, Deployment, Customer, User, API Key, Log Source stay in English** everywhere they appear (headings, buttons, table headers, error messages). Every other piece of UI chrome (actions, generic field labels, messages) is in Hebrew. See the design doc's terminology table for the exact mapping.
- **No `@code` block behavior changes anywhere in this plan.** Every task is markup + CSS class + displayed-text only. The one exception is the literal Hebrew text replacing the English text *inside* an existing `$"..."` string interpolation for an already-existing error message — the C# logic producing that string (which `catch` clause, which condition) does not change.
- No new automated tests. Per the design doc's Testing section, this is a zero-behavior-change pass; every existing Controller-level integration test is API-only and unaffected. Each task's gate is `dotnet build` (catches Razor/C# errors); the final task is a manual browser walkthrough.
- Full design context: `docs/superpowers/specs/2026-08-19-hebrew-rtl-redesign-design.md`.

---

### Task 1: Foundation — vendor Bootstrap, RTL shell, layout, nav

**Files:**
- Create: `src/LogsPlatform.Web/wwwroot/lib/bootstrap/bootstrap.rtl.min.css`
- Create: `src/LogsPlatform.Web/wwwroot/lib/bootstrap/bootstrap.bundle.min.js`
- Create: `src/LogsPlatform.Web/wwwroot/css/site.css`
- Create: `src/LogsPlatform.Web/Components/Layout/MainLayout.razor`
- Create: `src/LogsPlatform.Web/Components/Layout/NavMenu.razor`
- Modify: `src/LogsPlatform.Web/Components/App.razor`
- Modify: `src/LogsPlatform.Web/Components/Routes.razor`

**Interfaces:**
- Consumes: nothing from other tasks (this is the first task).
- Produces: `LogsPlatform.Web.Components.Layout.MainLayout` (type name referenced by `Routes.razor`'s `DefaultLayout`), static asset paths `lib/bootstrap/bootstrap.rtl.min.css`, `lib/bootstrap/bootstrap.bundle.min.js`, `css/site.css`, and CSS class **`app-row-details`** (defined in `site.css`, consumed by Task 2/`ApplicationsAdmin.razor` to space out its nested row-expansion content).

- [ ] **Step 1: Create the Bootstrap vendor directory and download the RTL build**

```bash
mkdir -p src/LogsPlatform.Web/wwwroot/lib/bootstrap
curl -sSL -o src/LogsPlatform.Web/wwwroot/lib/bootstrap/bootstrap.rtl.min.css https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.rtl.min.css
curl -sSL -o src/LogsPlatform.Web/wwwroot/lib/bootstrap/bootstrap.bundle.min.js https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js
```

- [ ] **Step 2: Verify both files downloaded correctly**

Run: `ls -la src/LogsPlatform.Web/wwwroot/lib/bootstrap/`
Expected: two files, `bootstrap.rtl.min.css` (~230KB) and `bootstrap.bundle.min.js` (~80KB), both non-empty.

Run: `head -c 200 src/LogsPlatform.Web/wwwroot/lib/bootstrap/bootstrap.rtl.min.css`
Expected: starts with a `/*! * Bootstrap ... */` comment banner, not an HTML error page.

- [ ] **Step 3: Create `wwwroot/css/site.css`**

```css
/* src/LogsPlatform.Web/wwwroot/css/site.css */
body {
    background-color: #f8f9fa;
}

.nav-link.disabled .badge {
    margin-inline-start: 0.4rem;
    font-weight: normal;
    vertical-align: middle;
}

.app-row-details > * + * {
    margin-top: 1.5rem;
}
```

- [ ] **Step 4: Create `Components/Layout/MainLayout.razor`**

```razor
@* src/LogsPlatform.Web/Components/Layout/MainLayout.razor *@
@inherits LayoutComponentBase

<NavMenu />

<div class="container-fluid px-4 pb-4">
    @Body
</div>
```

- [ ] **Step 5: Create `Components/Layout/NavMenu.razor`**

```razor
@* src/LogsPlatform.Web/Components/Layout/NavMenu.razor *@
<nav class="navbar navbar-expand navbar-dark bg-dark mb-4">
    <div class="container-fluid">
        <span class="navbar-brand">LogsPlatform</span>
        <ul class="navbar-nav">
            <li class="nav-item">
                <span class="nav-link disabled">
                    מה חריג
                    <span class="badge text-bg-secondary">בקרוב</span>
                </span>
            </li>
            <li class="nav-item">
                <span class="nav-link disabled">
                    חיפוש
                    <span class="badge text-bg-secondary">בקרוב</span>
                </span>
            </li>
            <li class="nav-item">
                <span class="nav-link disabled">
                    חריגות
                    <span class="badge text-bg-secondary">בקרוב</span>
                </span>
            </li>
            <li class="nav-item">
                <NavLink class="nav-link" href="/admin/applications" Match="NavLinkMatch.Prefix">
                    ניהול
                </NavLink>
            </li>
        </ul>
    </div>
</nav>
```

- [ ] **Step 6: Update `Components/App.razor`** — RTL, Hebrew, link the vendored assets

```razor
<!DOCTYPE html>
<html lang="he" dir="rtl">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="/" />
    <link rel="stylesheet" href="lib/bootstrap/bootstrap.rtl.min.css" />
    <link rel="stylesheet" href="css/site.css" />
    <title>LogsPlatform</title>
    <HeadOutlet @rendermode="InteractiveServer" />
</head>
<body>
    <Routes @rendermode="InteractiveServer" />
    <div id="blazor-error-ui" style="display:none;position:fixed;bottom:0;left:0;right:0;padding:0.6rem;background:#ffffcc;box-shadow:0 -1px 2px rgba(0,0,0,.2)">
        אירעה שגיאה בלתי צפויה. <a href="" class="reload">רענון</a>
    </div>
    <script src="_framework/blazor.web.js"></script>
    <script src="lib/bootstrap/bootstrap.bundle.min.js"></script>
</body>
</html>
```

- [ ] **Step 7: Update `Components/Routes.razor`** — wire `MainLayout` as the default layout for every page

```razor
<Router AppAssembly="typeof(Program).Assembly">
    <Found Context="routeData">
        <ErrorBoundary>
            <ChildContent>
                <RouteView RouteData="routeData" DefaultLayout="typeof(LogsPlatform.Web.Components.Layout.MainLayout)" />
            </ChildContent>
            <ErrorContent>
                <p>אירעה שגיאה בטעינת הדף. ייתכן שהקישור שגוי או שהדף כבר לא קיים.</p>
            </ErrorContent>
        </ErrorBoundary>
    </Found>
    <NotFound>
        <LayoutView Layout="typeof(LogsPlatform.Web.Components.Layout.MainLayout)">
            <p>הדף לא נמצא.</p>
        </LayoutView>
    </NotFound>
</Router>
```

- [ ] **Step 8: Build to verify everything compiles**

Run: `dotnet build src/LogsPlatform.Web/LogsPlatform.Web.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 9: Commit**

```bash
git add src/LogsPlatform.Web/wwwroot src/LogsPlatform.Web/Components/Layout src/LogsPlatform.Web/Components/App.razor src/LogsPlatform.Web/Components/Routes.razor
git commit -m "Add Bootstrap RTL shell, MainLayout, and NavMenu"
```

---

### Task 2: `ApplicationsAdmin.razor`

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor`

**Interfaces:**
- Consumes: CSS class `app-row-details` (from Task 1's `site.css`), `IApplicationRepository`/`IAppEnvironmentRepository` (unchanged).
- Produces: n/a.

- [ ] **Step 1: Replace the full file content**

```razor
@* src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor *@
@page "/admin/applications"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web.Components.Shared
@using Microsoft.EntityFrameworkCore
@using Microsoft.Data.SqlClient
@inject IApplicationRepository ApplicationRepository
@inject IAppEnvironmentRepository EnvironmentRepository
@rendermode InteractiveServer

<h1>Applications</h1>

<div class="card mb-4">
    <div class="card-header">הוספת Application</div>
    <div class="card-body">
        <EditForm Model="_newApplication" OnValidSubmit="CreateApplicationAsync">
            <div class="row g-3 align-items-end">
                <div class="col-auto">
                    <label class="form-label">שם</label>
                    <InputText @bind-Value="_newApplication.Name" required class="form-control" />
                </div>
                <div class="col-auto">
                    <label class="form-label">תיאור</label>
                    <InputText @bind-Value="_newApplication.Description" class="form-control" />
                </div>
                <div class="col-auto">
                    <button type="submit" class="btn btn-primary">צור</button>
                </div>
            </div>
        </EditForm>
        @if (_createError is not null)
        {
            <div class="alert alert-danger mt-3 mb-0">@_createError</div>
        }
    </div>
</div>

<table class="table table-striped table-hover align-middle">
    <thead>
        <tr>
            <th></th>
            <th></th>
            <th>שם</th>
            <th>תיאור</th>
            <th>נוצר בתאריך</th>
        </tr>
    </thead>
    <tbody>
        @foreach (var application in _applications)
        {
            <tr @key="application.Id">
                <td>
                    <button class="btn btn-sm btn-outline-secondary" @onclick="() => ToggleExpandAsync(application.Id)">
                        @(_expandedAppIds.Contains(application.Id) ? "-" : "+")
                    </button>
                </td>
                <td><a href="/admin/applications/@application.Id/modules">Modules</a></td>
                <td>@application.Name</td>
                <td>@application.Description</td>
                <td>@application.CreatedAt</td>
            </tr>
            @if (_expandedAppIds.Contains(application.Id))
            {
                <tr>
                    <td colspan="5">
                        <div class="app-row-details">
                            <div>
                                <h4>Environments</h4>
                                <table class="table table-sm table-striped align-middle">
                                    <thead>
                                        <tr>
                                            <th>שם</th>
                                            <th>סביבת ייצור</th>
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
                                        <div class="row g-3 align-items-end">
                                            <div class="col-auto">
                                                <label class="form-label">שם</label>
                                                <InputText @bind-Value="newEnvironment.Name" required class="form-control" />
                                            </div>
                                            <div class="col-auto form-check mb-2">
                                                <InputCheckbox @bind-Value="newEnvironment.IsProduction" class="form-check-input" id="isProduction-@application.Id" />
                                                <label class="form-check-label" for="isProduction-@application.Id">סביבת ייצור</label>
                                            </div>
                                            <div class="col-auto">
                                                <button type="submit" class="btn btn-primary">הוסף Environment</button>
                                            </div>
                                        </div>
                                    </EditForm>
                                }
                            </div>

                            <CustomersSection ApplicationId="application.Id" />
                            <UsersSection ApplicationId="application.Id" />
                            <LogSourcesSection ApplicationId="application.Id" />
                            <ApiKeysSection ApplicationId="application.Id" />
                            <VersionsSection ApplicationId="application.Id" />
                            <DeploymentsSection ApplicationId="application.Id" />
                        </div>
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
            _createError = $"Application בשם '{_newApplication.Name}' כבר קיימת.";
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

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/LogsPlatform.Web/LogsPlatform.Web.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor
git commit -m "Restyle and translate ApplicationsAdmin to Hebrew/RTL"
```

---

### Task 3: `ModulesAdmin.razor`

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Pages/ModulesAdmin.razor`

**Interfaces:**
- Consumes: `BreadcrumbBuilder.BuildAsync` (unchanged, returns `List<BreadcrumbSegment>`), `IAppModuleRepository` (unchanged).
- Produces: n/a.

- [ ] **Step 1: Replace the full file content**

```razor
@* src/LogsPlatform.Web/Components/Pages/ModulesAdmin.razor *@
@page "/admin/applications/{AppId:int}/modules"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using LogsPlatform.Web.Services
@using Microsoft.EntityFrameworkCore
@inject IAppModuleRepository ModuleRepository
@inject BreadcrumbBuilder BreadcrumbBuilder
@rendermode InteractiveServer

<nav aria-label="breadcrumb">
    <ol class="breadcrumb">
        @for (var i = 0; i < _breadcrumb.Count; i++)
        {
            var segment = _breadcrumb[i];
            if (i == _breadcrumb.Count - 1)
            {
                <li class="breadcrumb-item active" aria-current="page">@segment.Label</li>
            }
            else
            {
                <li class="breadcrumb-item"><a href="@segment.Url">@segment.Label</a></li>
            }
        }
    </ol>
</nav>

<h1>Modules</h1>

<div class="card mb-4">
    <div class="card-header">הוספת Module</div>
    <div class="card-body">
        <EditForm Model="_newModule" OnValidSubmit="CreateModuleAsync">
            <div class="row g-3 align-items-end">
                <div class="col-auto">
                    <label class="form-label">שם</label>
                    <InputText @bind-Value="_newModule.Name" required maxlength="200" class="form-control" />
                </div>
                <div class="col-auto">
                    <label class="form-label">תיאור</label>
                    <InputText @bind-Value="_newModule.Description" class="form-control" />
                </div>
                <div class="col-auto">
                    <button type="submit" class="btn btn-primary">צור</button>
                </div>
            </div>
        </EditForm>
        @if (_createError is not null)
        {
            <div class="alert alert-danger mt-3 mb-0">@_createError</div>
        }
    </div>
</div>

<table class="table table-striped table-hover align-middle">
    <thead>
        <tr>
            <th>שם</th>
            <th>תיאור</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var module in _modules)
        {
            <tr>
                @if (_editingId == module.Id)
                {
                    <td colspan="2">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(module.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Name" required maxlength="200" class="form-control form-control-sm" />
                                </div>
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Description" class="form-control form-control-sm" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <div class="alert alert-danger mt-2 mb-0 py-1">@_editError</div>
                        }
                    </td>
                }
                else
                {
                    <td><a href="/admin/applications/@AppId/modules/@module.Id/screen-services">@module.Name</a></td>
                    <td>@module.Description</td>
                }
                <td>
                    @if (_editingId != module.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(module)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(module.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

@code {
    [Parameter] public int AppId { get; set; }

    private List<AppModule> _modules = new();
    private List<BreadcrumbSegment> _breadcrumb = new();
    private readonly NewModuleModel _newModule = new();
    private string? _createError;

    private int? _editingId;
    private EditModuleModel? _editModel;
    private string? _editError;

    protected override async Task OnInitializedAsync()
    {
        _breadcrumb = await BreadcrumbBuilder.BuildAsync(AppId);
        _modules = (await ModuleRepository.GetByApplicationIdAsync(AppId)).ToList();
    }

    private async Task CreateModuleAsync()
    {
        _createError = null;
        try
        {
            await ModuleRepository.AddAsync(new AppModule
            {
                ApplicationId = AppId,
                Name = _newModule.Name,
                Description = _newModule.Description
            });

            _newModule.Name = string.Empty;
            _newModule.Description = null;
            _modules = (await ModuleRepository.GetByApplicationIdAsync(AppId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"Module בשם '{_newModule.Name}' כבר קיים.";
        }
    }

    private void StartEdit(AppModule module)
    {
        _editingId = module.Id;
        _editModel = new EditModuleModel { Name = module.Name, Description = module.Description };
        _editError = null;
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
        _editError = null;
    }

    private async Task SaveRenameAsync(int moduleId)
    {
        _editError = null;
        try
        {
            await ModuleRepository.RenameAsync(moduleId, _editModel!.Name, _editModel!.Description);
            _editingId = null;
            _editModel = null;
            _modules = (await ModuleRepository.GetByApplicationIdAsync(AppId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _editError = $"Module בשם '{_editModel!.Name}' כבר קיים.";
        }
    }

    private async Task DeactivateAsync(int moduleId)
    {
        await ModuleRepository.DeactivateAsync(moduleId);
        _modules = (await ModuleRepository.GetByApplicationIdAsync(AppId)).ToList();
    }

    private class NewModuleModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    private class EditModuleModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/LogsPlatform.Web/LogsPlatform.Web.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/ModulesAdmin.razor
git commit -m "Restyle and translate ModulesAdmin to Hebrew/RTL"
```

---

### Task 4: `ScreenServicesAdmin.razor`

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Pages/ScreenServicesAdmin.razor`

**Interfaces:**
- Consumes: `BreadcrumbBuilder.BuildAsync`, `IScreenServiceRepository`, `ScreenServiceType` enum (all unchanged).
- Produces: n/a.

- [ ] **Step 1: Replace the full file content**

```razor
@* src/LogsPlatform.Web/Components/Pages/ScreenServicesAdmin.razor *@
@page "/admin/applications/{AppId:int}/modules/{ModuleId:int}/screen-services"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using LogsPlatform.Web.Services
@using Microsoft.EntityFrameworkCore
@inject IScreenServiceRepository ScreenServiceRepository
@inject BreadcrumbBuilder BreadcrumbBuilder
@rendermode InteractiveServer

<nav aria-label="breadcrumb">
    <ol class="breadcrumb">
        @for (var i = 0; i < _breadcrumb.Count; i++)
        {
            var segment = _breadcrumb[i];
            if (i == _breadcrumb.Count - 1)
            {
                <li class="breadcrumb-item active" aria-current="page">@segment.Label</li>
            }
            else
            {
                <li class="breadcrumb-item"><a href="@segment.Url">@segment.Label</a></li>
            }
        }
    </ol>
</nav>

<h1>Screen/Services</h1>

<div class="card mb-4">
    <div class="card-header">הוספת Screen/Service</div>
    <div class="card-body">
        <EditForm Model="_newScreenService" OnValidSubmit="CreateScreenServiceAsync">
            <div class="row g-3 align-items-end">
                <div class="col-auto">
                    <label class="form-label">שם</label>
                    <InputText @bind-Value="_newScreenService.Name" required maxlength="200" class="form-control" />
                </div>
                <div class="col-auto">
                    <label class="form-label">סוג</label>
                    <InputSelect @bind-Value="_newScreenService.Type" class="form-select">
                        <option value="@ScreenServiceType.Screen">Screen</option>
                        <option value="@ScreenServiceType.Service">Service</option>
                    </InputSelect>
                </div>
                <div class="col-auto">
                    <label class="form-label">תיאור</label>
                    <InputText @bind-Value="_newScreenService.Description" class="form-control" />
                </div>
                <div class="col-auto">
                    <button type="submit" class="btn btn-primary">צור</button>
                </div>
            </div>
        </EditForm>
        @if (_createError is not null)
        {
            <div class="alert alert-danger mt-3 mb-0">@_createError</div>
        }
    </div>
</div>

<table class="table table-striped table-hover align-middle">
    <thead>
        <tr>
            <th>שם</th>
            <th>סוג</th>
            <th>תיאור</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var screenService in _screenServices)
        {
            <tr>
                @if (_editingId == screenService.Id)
                {
                    <td colspan="3">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(screenService.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Name" required maxlength="200" class="form-control form-control-sm" />
                                </div>
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Description" class="form-control form-control-sm" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <div class="alert alert-danger mt-2 mb-0 py-1">@_editError</div>
                        }
                    </td>
                }
                else
                {
                    <td>
                        <a href="/admin/applications/@AppId/modules/@ModuleId/screen-services/@screenService.Id/processes">@screenService.Name</a>
                    </td>
                    <td>@screenService.Type</td>
                    <td>@screenService.Description</td>
                }
                <td>
                    @if (_editingId != screenService.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(screenService)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(screenService.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

@code {
    [Parameter] public int AppId { get; set; }
    [Parameter] public int ModuleId { get; set; }

    private List<ScreenService> _screenServices = new();
    private List<BreadcrumbSegment> _breadcrumb = new();
    private readonly NewScreenServiceModel _newScreenService = new();
    private string? _createError;

    private int? _editingId;
    private EditScreenServiceModel? _editModel;
    private string? _editError;

    protected override async Task OnInitializedAsync()
    {
        _breadcrumb = await BreadcrumbBuilder.BuildAsync(AppId, ModuleId);
        _screenServices = (await ScreenServiceRepository.GetByModuleIdAsync(ModuleId)).ToList();
    }

    private async Task CreateScreenServiceAsync()
    {
        _createError = null;
        try
        {
            await ScreenServiceRepository.AddAsync(new ScreenService
            {
                ModuleId = ModuleId,
                Name = _newScreenService.Name,
                Type = _newScreenService.Type,
                Description = _newScreenService.Description
            });

            _newScreenService.Name = string.Empty;
            _newScreenService.Type = ScreenServiceType.Screen;
            _newScreenService.Description = null;
            _screenServices = (await ScreenServiceRepository.GetByModuleIdAsync(ModuleId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"Screen/Service בשם '{_newScreenService.Name}' כבר קיים.";
        }
    }

    private void StartEdit(ScreenService screenService)
    {
        _editingId = screenService.Id;
        _editModel = new EditScreenServiceModel { Name = screenService.Name, Description = screenService.Description };
        _editError = null;
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
        _editError = null;
    }

    private async Task SaveRenameAsync(int screenServiceId)
    {
        _editError = null;
        try
        {
            await ScreenServiceRepository.RenameAsync(screenServiceId, _editModel!.Name, _editModel!.Description);
            _editingId = null;
            _editModel = null;
            _screenServices = (await ScreenServiceRepository.GetByModuleIdAsync(ModuleId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _editError = $"Screen/Service בשם '{_editModel!.Name}' כבר קיים.";
        }
    }

    private async Task DeactivateAsync(int screenServiceId)
    {
        await ScreenServiceRepository.DeactivateAsync(screenServiceId);
        _screenServices = (await ScreenServiceRepository.GetByModuleIdAsync(ModuleId)).ToList();
    }

    private class NewScreenServiceModel
    {
        public string Name { get; set; } = string.Empty;
        public ScreenServiceType Type { get; set; } = ScreenServiceType.Screen;
        public string? Description { get; set; }
    }

    private class EditScreenServiceModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/LogsPlatform.Web/LogsPlatform.Web.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/ScreenServicesAdmin.razor
git commit -m "Restyle and translate ScreenServicesAdmin to Hebrew/RTL"
```

---

### Task 5: `ProcessesAdmin.razor`

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Pages/ProcessesAdmin.razor`

**Interfaces:**
- Consumes: `BreadcrumbBuilder.BuildAsync`, `IProcessNodeRepository` (unchanged).
- Produces: n/a.

- [ ] **Step 1: Replace the full file content**

```razor
@* src/LogsPlatform.Web/Components/Pages/ProcessesAdmin.razor *@
@page "/admin/applications/{AppId:int}/modules/{ModuleId:int}/screen-services/{ScreenServiceId:int}/processes"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using LogsPlatform.Web.Services
@using Microsoft.EntityFrameworkCore
@inject IProcessNodeRepository ProcessRepository
@inject BreadcrumbBuilder BreadcrumbBuilder
@rendermode InteractiveServer

<nav aria-label="breadcrumb">
    <ol class="breadcrumb">
        @for (var i = 0; i < _breadcrumb.Count; i++)
        {
            var segment = _breadcrumb[i];
            if (i == _breadcrumb.Count - 1)
            {
                <li class="breadcrumb-item active" aria-current="page">@segment.Label</li>
            }
            else
            {
                <li class="breadcrumb-item"><a href="@segment.Url">@segment.Label</a></li>
            }
        }
    </ol>
</nav>

<h1>Processes</h1>

<div class="card mb-4">
    <div class="card-header">הוספת Process</div>
    <div class="card-body">
        <EditForm Model="_newProcess" OnValidSubmit="CreateProcessAsync">
            <div class="row g-3 align-items-end">
                <div class="col-auto">
                    <label class="form-label">שם</label>
                    <InputText @bind-Value="_newProcess.Name" required maxlength="200" class="form-control" />
                </div>
                <div class="col-auto">
                    <label class="form-label">תיאור</label>
                    <InputText @bind-Value="_newProcess.Description" class="form-control" />
                </div>
                <div class="col-auto">
                    <button type="submit" class="btn btn-primary">צור</button>
                </div>
            </div>
        </EditForm>
        @if (_createError is not null)
        {
            <div class="alert alert-danger mt-3 mb-0">@_createError</div>
        }
    </div>
</div>

<table class="table table-striped table-hover align-middle">
    <thead>
        <tr>
            <th>שם</th>
            <th>תיאור</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var process in _processes)
        {
            <tr>
                @if (_editingId == process.Id)
                {
                    <td colspan="2">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(process.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Name" required maxlength="200" class="form-control form-control-sm" />
                                </div>
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Description" class="form-control form-control-sm" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <div class="alert alert-danger mt-2 mb-0 py-1">@_editError</div>
                        }
                    </td>
                }
                else
                {
                    <td>
                        <a href="/admin/applications/@AppId/modules/@ModuleId/screen-services/@ScreenServiceId/processes/@process.Id/operations">@process.Name</a>
                    </td>
                    <td>@process.Description</td>
                }
                <td>
                    @if (_editingId != process.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(process)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(process.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

@code {
    [Parameter] public int AppId { get; set; }
    [Parameter] public int ModuleId { get; set; }
    [Parameter] public int ScreenServiceId { get; set; }

    private List<ProcessNode> _processes = new();
    private List<BreadcrumbSegment> _breadcrumb = new();
    private readonly NewProcessModel _newProcess = new();
    private string? _createError;

    private int? _editingId;
    private EditProcessModel? _editModel;
    private string? _editError;

    protected override async Task OnInitializedAsync()
    {
        _breadcrumb = await BreadcrumbBuilder.BuildAsync(AppId, ModuleId, ScreenServiceId);
        _processes = (await ProcessRepository.GetByScreenServiceIdAsync(ScreenServiceId)).ToList();
    }

    private async Task CreateProcessAsync()
    {
        _createError = null;
        try
        {
            await ProcessRepository.AddAsync(new ProcessNode
            {
                ScreenServiceId = ScreenServiceId,
                Name = _newProcess.Name,
                Description = _newProcess.Description
            });

            _newProcess.Name = string.Empty;
            _newProcess.Description = null;
            _processes = (await ProcessRepository.GetByScreenServiceIdAsync(ScreenServiceId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"Process בשם '{_newProcess.Name}' כבר קיים.";
        }
    }

    private void StartEdit(ProcessNode process)
    {
        _editingId = process.Id;
        _editModel = new EditProcessModel { Name = process.Name, Description = process.Description };
        _editError = null;
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
        _editError = null;
    }

    private async Task SaveRenameAsync(int processId)
    {
        _editError = null;
        try
        {
            await ProcessRepository.RenameAsync(processId, _editModel!.Name, _editModel!.Description);
            _editingId = null;
            _editModel = null;
            _processes = (await ProcessRepository.GetByScreenServiceIdAsync(ScreenServiceId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _editError = $"Process בשם '{_editModel!.Name}' כבר קיים.";
        }
    }

    private async Task DeactivateAsync(int processId)
    {
        await ProcessRepository.DeactivateAsync(processId);
        _processes = (await ProcessRepository.GetByScreenServiceIdAsync(ScreenServiceId)).ToList();
    }

    private class NewProcessModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    private class EditProcessModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/LogsPlatform.Web/LogsPlatform.Web.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/ProcessesAdmin.razor
git commit -m "Restyle and translate ProcessesAdmin to Hebrew/RTL"
```

---

### Task 6: `OperationsAdmin.razor`

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Pages/OperationsAdmin.razor`

**Interfaces:**
- Consumes: `BreadcrumbBuilder.BuildAsync`, `IOperationRepository` (unchanged).
- Produces: n/a.

- [ ] **Step 1: Replace the full file content**

```razor
@* src/LogsPlatform.Web/Components/Pages/OperationsAdmin.razor *@
@page "/admin/applications/{AppId:int}/modules/{ModuleId:int}/screen-services/{ScreenServiceId:int}/processes/{ProcessId:int}/operations"
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using LogsPlatform.Web.Services
@using Microsoft.EntityFrameworkCore
@inject IOperationRepository OperationRepository
@inject BreadcrumbBuilder BreadcrumbBuilder
@rendermode InteractiveServer

<nav aria-label="breadcrumb">
    <ol class="breadcrumb">
        @for (var i = 0; i < _breadcrumb.Count; i++)
        {
            var segment = _breadcrumb[i];
            if (i == _breadcrumb.Count - 1)
            {
                <li class="breadcrumb-item active" aria-current="page">@segment.Label</li>
            }
            else
            {
                <li class="breadcrumb-item"><a href="@segment.Url">@segment.Label</a></li>
            }
        }
    </ol>
</nav>

<h1>Operations</h1>

<div class="card mb-4">
    <div class="card-header">הוספת Operation</div>
    <div class="card-body">
        <EditForm Model="_newOperation" OnValidSubmit="CreateOperationAsync">
            <div class="row g-3 align-items-end">
                <div class="col-auto">
                    <label class="form-label">שם</label>
                    <InputText @bind-Value="_newOperation.Name" required maxlength="200" class="form-control" />
                </div>
                <div class="col-auto">
                    <label class="form-label">תיאור</label>
                    <InputText @bind-Value="_newOperation.Description" class="form-control" />
                </div>
                <div class="col-auto">
                    <button type="submit" class="btn btn-primary">צור</button>
                </div>
            </div>
        </EditForm>
        @if (_createError is not null)
        {
            <div class="alert alert-danger mt-3 mb-0">@_createError</div>
        }
    </div>
</div>

<table class="table table-striped table-hover align-middle">
    <thead>
        <tr>
            <th>שם</th>
            <th>תיאור</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var operation in _operations)
        {
            <tr>
                @if (_editingId == operation.Id)
                {
                    <td colspan="2">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(operation.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Name" required maxlength="200" class="form-control form-control-sm" />
                                </div>
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Description" class="form-control form-control-sm" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <div class="alert alert-danger mt-2 mb-0 py-1">@_editError</div>
                        }
                    </td>
                }
                else
                {
                    <td>@operation.Name</td>
                    <td>@operation.Description</td>
                }
                <td>
                    @if (_editingId != operation.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(operation)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(operation.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

@code {
    [Parameter] public int AppId { get; set; }
    [Parameter] public int ModuleId { get; set; }
    [Parameter] public int ScreenServiceId { get; set; }
    [Parameter] public int ProcessId { get; set; }

    private List<Operation> _operations = new();
    private List<BreadcrumbSegment> _breadcrumb = new();
    private readonly NewOperationModel _newOperation = new();
    private string? _createError;

    private int? _editingId;
    private EditOperationModel? _editModel;
    private string? _editError;

    protected override async Task OnInitializedAsync()
    {
        _breadcrumb = await BreadcrumbBuilder.BuildAsync(AppId, ModuleId, ScreenServiceId, ProcessId);
        _operations = (await OperationRepository.GetByProcessIdAsync(ProcessId)).ToList();
    }

    private async Task CreateOperationAsync()
    {
        _createError = null;
        try
        {
            await OperationRepository.AddAsync(new Operation
            {
                ProcessId = ProcessId,
                Name = _newOperation.Name,
                Description = _newOperation.Description
            });

            _newOperation.Name = string.Empty;
            _newOperation.Description = null;
            _operations = (await OperationRepository.GetByProcessIdAsync(ProcessId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"Operation בשם '{_newOperation.Name}' כבר קיימת.";
        }
    }

    private void StartEdit(Operation operation)
    {
        _editingId = operation.Id;
        _editModel = new EditOperationModel { Name = operation.Name, Description = operation.Description };
        _editError = null;
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
        _editError = null;
    }

    private async Task SaveRenameAsync(int operationId)
    {
        _editError = null;
        try
        {
            await OperationRepository.RenameAsync(operationId, _editModel!.Name, _editModel!.Description);
            _editingId = null;
            _editModel = null;
            _operations = (await OperationRepository.GetByProcessIdAsync(ProcessId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _editError = $"Operation בשם '{_editModel!.Name}' כבר קיימת.";
        }
    }

    private async Task DeactivateAsync(int operationId)
    {
        await OperationRepository.DeactivateAsync(operationId);
        _operations = (await OperationRepository.GetByProcessIdAsync(ProcessId)).ToList();
    }

    private class NewOperationModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    private class EditOperationModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/LogsPlatform.Web/LogsPlatform.Web.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/OperationsAdmin.razor
git commit -m "Restyle and translate OperationsAdmin to Hebrew/RTL"
```

---

### Task 7: `CustomersSection.razor`

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Shared/CustomersSection.razor`

**Interfaces:**
- Consumes: `ICustomerRepository` (unchanged), rendered inside `ApplicationsAdmin.razor`'s `app-row-details` container (Task 2).
- Produces: n/a.

- [ ] **Step 1: Replace the full file content**

```razor
@* src/LogsPlatform.Web/Components/Shared/CustomersSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using Microsoft.EntityFrameworkCore
@inject ICustomerRepository CustomerRepository

<h4>Customers</h4>
<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th>מזהה לקוח חיצוני</th>
            <th>שם</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var customer in _customers)
        {
            <tr>
                @if (_editingId == customer.Id)
                {
                    <td colspan="2">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(customer.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Name" required maxlength="200" class="form-control form-control-sm" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                    </td>
                }
                else
                {
                    <td>@customer.ExternalCustomerId</td>
                    <td>@customer.Name</td>
                }
                <td>
                    @if (_editingId != customer.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(customer)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(customer.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newCustomer" OnValidSubmit="CreateCustomerAsync">
    <div class="row g-3 align-items-end">
        <div class="col-auto">
            <label class="form-label">מזהה לקוח חיצוני</label>
            <InputText @bind-Value="_newCustomer.ExternalCustomerId" required maxlength="200" class="form-control" />
        </div>
        <div class="col-auto">
            <label class="form-label">שם</label>
            <InputText @bind-Value="_newCustomer.Name" required maxlength="200" class="form-control" />
        </div>
        <div class="col-auto">
            <button type="submit" class="btn btn-primary">הוסף Customer</button>
        </div>
    </div>
</EditForm>
@if (_createError is not null)
{
    <div class="alert alert-danger mt-3">@_createError</div>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<Customer> _customers = new();
    private readonly NewCustomerModel _newCustomer = new();
    private string? _createError;

    private int? _editingId;
    private EditCustomerModel? _editModel;

    protected override async Task OnInitializedAsync()
    {
        _customers = (await CustomerRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateCustomerAsync()
    {
        _createError = null;
        try
        {
            await CustomerRepository.AddAsync(new Customer
            {
                ApplicationId = ApplicationId,
                ExternalCustomerId = _newCustomer.ExternalCustomerId,
                Name = _newCustomer.Name
            });

            _newCustomer.ExternalCustomerId = string.Empty;
            _newCustomer.Name = string.Empty;
            _customers = (await CustomerRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"Customer עם מזהה חיצוני '{_newCustomer.ExternalCustomerId}' כבר קיים.";
        }
    }

    private void StartEdit(Customer customer)
    {
        _editingId = customer.Id;
        _editModel = new EditCustomerModel { Name = customer.Name };
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
    }

    private async Task SaveRenameAsync(int customerId)
    {
        await CustomerRepository.RenameAsync(customerId, _editModel!.Name);
        _editingId = null;
        _editModel = null;
        _customers = (await CustomerRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task DeactivateAsync(int customerId)
    {
        await CustomerRepository.DeactivateAsync(customerId);
        _customers = (await CustomerRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private class NewCustomerModel
    {
        public string ExternalCustomerId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    private class EditCustomerModel
    {
        public string Name { get; set; } = string.Empty;
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/LogsPlatform.Web/LogsPlatform.Web.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/CustomersSection.razor
git commit -m "Restyle and translate CustomersSection to Hebrew/RTL"
```

---

### Task 8: `UsersSection.razor`

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Shared/UsersSection.razor`

**Interfaces:**
- Consumes: `IAppUserRepository` (unchanged), rendered inside `ApplicationsAdmin.razor`'s `app-row-details` container (Task 2).
- Produces: n/a.

- [ ] **Step 1: Replace the full file content**

```razor
@* src/LogsPlatform.Web/Components/Shared/UsersSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using Microsoft.EntityFrameworkCore
@inject IAppUserRepository AppUserRepository

<h4>Users</h4>
<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th>מזהה משתמש חיצוני</th>
            <th>שם תצוגה</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var user in _users)
        {
            <tr>
                @if (_editingId == user.Id)
                {
                    <td colspan="2">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(user.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.DisplayName" required maxlength="200" class="form-control form-control-sm" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                    </td>
                }
                else
                {
                    <td>@user.ExternalUserId</td>
                    <td>@user.DisplayName</td>
                }
                <td>
                    @if (_editingId != user.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(user)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(user.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newUser" OnValidSubmit="CreateUserAsync">
    <div class="row g-3 align-items-end">
        <div class="col-auto">
            <label class="form-label">מזהה משתמש חיצוני</label>
            <InputText @bind-Value="_newUser.ExternalUserId" required maxlength="200" class="form-control" />
        </div>
        <div class="col-auto">
            <label class="form-label">שם תצוגה</label>
            <InputText @bind-Value="_newUser.DisplayName" required maxlength="200" class="form-control" />
        </div>
        <div class="col-auto">
            <button type="submit" class="btn btn-primary">הוסף User</button>
        </div>
    </div>
</EditForm>
@if (_createError is not null)
{
    <div class="alert alert-danger mt-3">@_createError</div>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<AppUser> _users = new();
    private readonly NewUserModel _newUser = new();
    private string? _createError;

    private int? _editingId;
    private EditUserModel? _editModel;

    protected override async Task OnInitializedAsync()
    {
        _users = (await AppUserRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateUserAsync()
    {
        _createError = null;
        try
        {
            await AppUserRepository.AddAsync(new AppUser
            {
                ApplicationId = ApplicationId,
                ExternalUserId = _newUser.ExternalUserId,
                DisplayName = _newUser.DisplayName
            });

            _newUser.ExternalUserId = string.Empty;
            _newUser.DisplayName = string.Empty;
            _users = (await AppUserRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"User עם מזהה חיצוני '{_newUser.ExternalUserId}' כבר קיים.";
        }
    }

    private void StartEdit(AppUser user)
    {
        _editingId = user.Id;
        _editModel = new EditUserModel { DisplayName = user.DisplayName };
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
    }

    private async Task SaveRenameAsync(int userId)
    {
        await AppUserRepository.RenameAsync(userId, _editModel!.DisplayName);
        _editingId = null;
        _editModel = null;
        _users = (await AppUserRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task DeactivateAsync(int userId)
    {
        await AppUserRepository.DeactivateAsync(userId);
        _users = (await AppUserRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private class NewUserModel
    {
        public string ExternalUserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    private class EditUserModel
    {
        public string DisplayName { get; set; } = string.Empty;
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/LogsPlatform.Web/LogsPlatform.Web.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/UsersSection.razor
git commit -m "Restyle and translate UsersSection to Hebrew/RTL"
```

---

### Task 9: `LogSourcesSection.razor`

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Shared/LogSourcesSection.razor`

**Interfaces:**
- Consumes: `ILogSourceRepository` (unchanged), rendered inside `ApplicationsAdmin.razor`'s `app-row-details` container (Task 2).
- Produces: n/a.

- [ ] **Step 1: Replace the full file content**

```razor
@* src/LogsPlatform.Web/Components/Shared/LogSourcesSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using Microsoft.EntityFrameworkCore
@inject ILogSourceRepository LogSourceRepository

<h4>Log Sources</h4>
<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th>שם</th>
            <th>תיאור</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var logSource in _logSources)
        {
            <tr>
                @if (_editingId == logSource.Id)
                {
                    <td colspan="2">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(logSource.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Name" required maxlength="200" class="form-control form-control-sm" />
                                </div>
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Description" class="form-control form-control-sm" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <div class="alert alert-danger mt-2 mb-0 py-1">@_editError</div>
                        }
                    </td>
                }
                else
                {
                    <td>@logSource.Name</td>
                    <td>@logSource.Description</td>
                }
                <td>
                    @if (_editingId != logSource.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(logSource)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(logSource.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newLogSource" OnValidSubmit="CreateLogSourceAsync">
    <div class="row g-3 align-items-end">
        <div class="col-auto">
            <label class="form-label">שם</label>
            <InputText @bind-Value="_newLogSource.Name" required maxlength="200" class="form-control" />
        </div>
        <div class="col-auto">
            <label class="form-label">תיאור</label>
            <InputText @bind-Value="_newLogSource.Description" class="form-control" />
        </div>
        <div class="col-auto">
            <button type="submit" class="btn btn-primary">הוסף Log Source</button>
        </div>
    </div>
</EditForm>
@if (_createError is not null)
{
    <div class="alert alert-danger mt-3">@_createError</div>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<LogSource> _logSources = new();
    private readonly NewLogSourceModel _newLogSource = new();
    private string? _createError;

    private int? _editingId;
    private EditLogSourceModel? _editModel;
    private string? _editError;

    protected override async Task OnInitializedAsync()
    {
        _logSources = (await LogSourceRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateLogSourceAsync()
    {
        _createError = null;
        try
        {
            await LogSourceRepository.AddAsync(new LogSource
            {
                ApplicationId = ApplicationId,
                Name = _newLogSource.Name,
                Description = _newLogSource.Description
            });

            _newLogSource.Name = string.Empty;
            _newLogSource.Description = null;
            _logSources = (await LogSourceRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"Log Source בשם '{_newLogSource.Name}' כבר קיים.";
        }
    }

    private void StartEdit(LogSource logSource)
    {
        _editingId = logSource.Id;
        _editModel = new EditLogSourceModel { Name = logSource.Name, Description = logSource.Description };
        _editError = null;
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
        _editError = null;
    }

    private async Task SaveRenameAsync(int logSourceId)
    {
        _editError = null;
        try
        {
            await LogSourceRepository.RenameAsync(logSourceId, _editModel!.Name, _editModel!.Description);
            _editingId = null;
            _editModel = null;
            _logSources = (await LogSourceRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _editError = $"Log Source בשם '{_editModel!.Name}' כבר קיים.";
        }
    }

    private async Task DeactivateAsync(int logSourceId)
    {
        await LogSourceRepository.DeactivateAsync(logSourceId);
        _logSources = (await LogSourceRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private class NewLogSourceModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    private class EditLogSourceModel
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/LogsPlatform.Web/LogsPlatform.Web.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/LogSourcesSection.razor
git commit -m "Restyle and translate LogSourcesSection to Hebrew/RTL"
```

---

### Task 10: `ApiKeysSection.razor`

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Shared/ApiKeysSection.razor`

**Interfaces:**
- Consumes: `IApiKeyRepository` (unchanged), rendered inside `ApplicationsAdmin.razor`'s `app-row-details` container (Task 2).
- Produces: n/a.

- [ ] **Step 1: Replace the full file content**

```razor
@* src/LogsPlatform.Web/Components/Shared/ApiKeysSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@inject IApiKeyRepository ApiKeyRepository

<h4>API Keys</h4>
@if (_newRawKey is not null)
{
    <div class="alert alert-warning">זוהי הפעם היחידה שבה תוכל/י לראות את המפתח הזה — העתק/י אותו עכשיו.</div>
    <pre class="bg-light border rounded p-2">@_newRawKey</pre>
}
<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th>תווית</th>
            <th>נוצר בתאריך</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var apiKey in _apiKeys)
        {
            <tr>
                <td>@apiKey.Label</td>
                <td>@apiKey.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) UTC</td>
                <td>
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => RevokeAsync(apiKey.Id)">בטל תוקף</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newApiKey" OnValidSubmit="CreateApiKeyAsync">
    <div class="row g-3 align-items-end">
        <div class="col-auto">
            <label class="form-label">תווית</label>
            <InputText @bind-Value="_newApiKey.Label" required maxlength="200" class="form-control" />
        </div>
        <div class="col-auto">
            <button type="submit" class="btn btn-primary">הוסף API Key</button>
        </div>
    </div>
</EditForm>

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<ApiKey> _apiKeys = new();
    private readonly NewApiKeyModel _newApiKey = new();
    private string? _newRawKey;
    private int? _lastLoadedApplicationId;

    protected override async Task OnParametersSetAsync()
    {
        if (_lastLoadedApplicationId == ApplicationId)
        {
            return;
        }

        _lastLoadedApplicationId = ApplicationId;
        _newRawKey = null;
        _apiKeys = (await ApiKeyRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateApiKeyAsync()
    {
        var (_, rawKey) = await ApiKeyRepository.AddAsync(ApplicationId, _newApiKey.Label);

        _newRawKey = rawKey;
        _newApiKey.Label = string.Empty;
        _apiKeys = (await ApiKeyRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task RevokeAsync(int apiKeyId)
    {
        await ApiKeyRepository.RevokeAsync(apiKeyId);
        _apiKeys = (await ApiKeyRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private class NewApiKeyModel
    {
        public string Label { get; set; } = string.Empty;
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/LogsPlatform.Web/LogsPlatform.Web.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/ApiKeysSection.razor
git commit -m "Restyle and translate ApiKeysSection to Hebrew/RTL"
```

---

### Task 11: `VersionsSection.razor`

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Shared/VersionsSection.razor`

**Interfaces:**
- Consumes: `IAppVersionRepository` (unchanged), rendered inside `ApplicationsAdmin.razor`'s `app-row-details` container (Task 2).
- Produces: n/a.

- [ ] **Step 1: Replace the full file content**

```razor
@* src/LogsPlatform.Web/Components/Shared/VersionsSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@using LogsPlatform.Web
@using Microsoft.EntityFrameworkCore
@inject IAppVersionRepository VersionRepository

<h4>Versions</h4>
<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th>מספר גרסה</th>
            <th>הערות גרסה</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var version in _versions)
        {
            <tr>
                @if (_editingId == version.Id)
                {
                    <td colspan="2">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(version.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.ReleaseNotes" class="form-control form-control-sm" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                    </td>
                }
                else
                {
                    <td>@version.VersionNumber</td>
                    <td>@version.ReleaseNotes</td>
                }
                <td>
                    @if (_editingId != version.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(version)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(version.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newVersion" OnValidSubmit="CreateVersionAsync">
    <div class="row g-3 align-items-end">
        <div class="col-auto">
            <label class="form-label">מספר גרסה</label>
            <InputText @bind-Value="_newVersion.VersionNumber" required maxlength="200" class="form-control" />
        </div>
        <div class="col-auto">
            <label class="form-label">הערות גרסה</label>
            <InputText @bind-Value="_newVersion.ReleaseNotes" class="form-control" />
        </div>
        <div class="col-auto">
            <button type="submit" class="btn btn-primary">הוסף Version</button>
        </div>
    </div>
</EditForm>
@if (_createError is not null)
{
    <div class="alert alert-danger mt-3">@_createError</div>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<AppVersion> _versions = new();
    private readonly NewVersionModel _newVersion = new();
    private string? _createError;

    private int? _editingId;
    private EditVersionModel? _editModel;
    private int? _lastLoadedApplicationId;

    protected override async Task OnParametersSetAsync()
    {
        if (_lastLoadedApplicationId == ApplicationId)
        {
            return;
        }

        _lastLoadedApplicationId = ApplicationId;
        _createError = null;
        _editingId = null;
        _editModel = null;
        _versions = (await VersionRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task CreateVersionAsync()
    {
        _createError = null;
        try
        {
            await VersionRepository.AddAsync(new AppVersion
            {
                ApplicationId = ApplicationId,
                VersionNumber = _newVersion.VersionNumber,
                ReleaseNotes = _newVersion.ReleaseNotes,
                CreatedAt = DateTime.UtcNow
            });

            _newVersion.VersionNumber = string.Empty;
            _newVersion.ReleaseNotes = null;
            _versions = (await VersionRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _createError = $"Version '{_newVersion.VersionNumber}' כבר קיימת.";
        }
    }

    private void StartEdit(AppVersion version)
    {
        _editingId = version.Id;
        _editModel = new EditVersionModel { ReleaseNotes = version.ReleaseNotes };
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
    }

    private async Task SaveRenameAsync(int versionId)
    {
        await VersionRepository.RenameAsync(versionId, _editModel!.ReleaseNotes);
        _editingId = null;
        _editModel = null;
        _versions = (await VersionRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task DeactivateAsync(int versionId)
    {
        await VersionRepository.DeactivateAsync(versionId);
        _versions = (await VersionRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private class NewVersionModel
    {
        public string VersionNumber { get; set; } = string.Empty;
        public string? ReleaseNotes { get; set; }
    }

    private class EditVersionModel
    {
        public string? ReleaseNotes { get; set; }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/LogsPlatform.Web/LogsPlatform.Web.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/VersionsSection.razor
git commit -m "Restyle and translate VersionsSection to Hebrew/RTL"
```

---

### Task 12: `DeploymentsSection.razor`

**Files:**
- Modify: `src/LogsPlatform.Web/Components/Shared/DeploymentsSection.razor`

**Interfaces:**
- Consumes: `IDeploymentRepository`, `IAppEnvironmentRepository`, `IAppVersionRepository` (all unchanged), rendered inside `ApplicationsAdmin.razor`'s `app-row-details` container (Task 2).
- Produces: n/a.

- [ ] **Step 1: Replace the full file content**

```razor
@* src/LogsPlatform.Web/Components/Shared/DeploymentsSection.razor *@
@using LogsPlatform.Domain.Entities
@using LogsPlatform.Domain.Repositories
@inject IDeploymentRepository DeploymentRepository
@inject IAppEnvironmentRepository EnvironmentRepository
@inject IAppVersionRepository VersionRepository

<h4>Deployments</h4>
<table class="table table-sm table-striped align-middle">
    <thead>
        <tr>
            <th>Environment</th>
            <th>Version</th>
            <th>תאריך פריסה</th>
            <th>הערות</th>
            <th></th>
        </tr>
    </thead>
    <tbody>
        @foreach (var deployment in _deployments)
        {
            <tr>
                @if (_editingId == deployment.Id)
                {
                    <td colspan="4">
                        <EditForm Model="_editModel" OnValidSubmit="() => SaveRenameAsync(deployment.Id)">
                            <div class="row g-2 align-items-center">
                                <div class="col-auto">
                                    <InputText @bind-Value="_editModel!.Notes" class="form-control form-control-sm" />
                                </div>
                                <div class="col-auto">
                                    <button type="submit" class="btn btn-sm btn-primary">שמור</button>
                                    <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="CancelEdit">בטל</button>
                                </div>
                            </div>
                        </EditForm>
                    </td>
                }
                else
                {
                    <td>@EnvironmentName(deployment.EnvironmentId)</td>
                    <td>@VersionNumber(deployment.VersionId)</td>
                    <td>@deployment.DeployedAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) UTC</td>
                    <td>@deployment.Notes</td>
                }
                <td>
                    @if (_editingId != deployment.Id)
                    {
                        <button class="btn btn-sm btn-outline-secondary" @onclick="() => StartEdit(deployment)">ערוך</button>
                    }
                    <button class="btn btn-sm btn-outline-danger" @onclick="() => DeactivateAsync(deployment.Id)">השבת</button>
                </td>
            </tr>
        }
    </tbody>
</table>

<EditForm Model="_newDeployment" OnValidSubmit="CreateDeploymentAsync">
    <div class="row g-3 align-items-end">
        <div class="col-auto">
            <label class="form-label">Environment</label>
            <InputSelect @bind-Value="_newDeployment.EnvironmentId" class="form-select">
                <option value="0">-- בחר/י --</option>
                @foreach (var environment in _environments)
                {
                    <option value="@environment.Id">@environment.Name</option>
                }
            </InputSelect>
        </div>
        <div class="col-auto">
            <label class="form-label">Version</label>
            <InputSelect @bind-Value="_newDeployment.VersionId" class="form-select">
                <option value="0">-- בחר/י --</option>
                @foreach (var version in _versions)
                {
                    <option value="@version.Id">@version.VersionNumber</option>
                }
            </InputSelect>
        </div>
        <div class="col-auto">
            <label class="form-label">תאריך פריסה (UTC)</label>
            <InputDate @bind-Value="_newDeployment.DeployedAt" Type="InputDateType.DateTimeLocal" class="form-control" />
        </div>
        <div class="col-auto">
            <label class="form-label">הערות</label>
            <InputText @bind-Value="_newDeployment.Notes" class="form-control" />
        </div>
        <div class="col-auto">
            <button type="submit" class="btn btn-primary">הוסף Deployment</button>
        </div>
    </div>
</EditForm>
@if (_createError is not null)
{
    <div class="alert alert-danger mt-3">@_createError</div>
}

@code {
    [Parameter] public int ApplicationId { get; set; }

    private List<Deployment> _deployments = new();
    private List<AppEnvironment> _environments = new();
    private List<AppVersion> _versions = new();
    private readonly NewDeploymentModel _newDeployment = new();
    private string? _createError;

    private int? _editingId;
    private EditDeploymentModel? _editModel;
    private int? _lastLoadedApplicationId;

    protected override async Task OnParametersSetAsync()
    {
        if (_lastLoadedApplicationId == ApplicationId)
        {
            return;
        }

        _lastLoadedApplicationId = ApplicationId;
        _createError = null;
        _editingId = null;
        _editModel = null;
        _deployments = (await DeploymentRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        _environments = (await EnvironmentRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
        _versions = (await VersionRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private string EnvironmentName(int environmentId) =>
        _environments.FirstOrDefault(e => e.Id == environmentId)?.Name ?? $"#{environmentId}";

    private string VersionNumber(int versionId) =>
        _versions.FirstOrDefault(v => v.Id == versionId)?.VersionNumber ?? $"#{versionId}";

    private async Task CreateDeploymentAsync()
    {
        _createError = null;
        if (_newDeployment.EnvironmentId == 0 || _newDeployment.VersionId == 0)
        {
            _createError = "יש לבחור Environment ו-Version.";
            return;
        }

        if (!_environments.Any(e => e.Id == _newDeployment.EnvironmentId) || !_versions.Any(v => v.Id == _newDeployment.VersionId))
        {
            _createError = "יש לבחור Environment ו-Version תקינים עבור Application זה.";
            return;
        }

        await DeploymentRepository.AddAsync(new Deployment
        {
            ApplicationId = ApplicationId,
            EnvironmentId = _newDeployment.EnvironmentId,
            VersionId = _newDeployment.VersionId,
            DeployedAt = _newDeployment.DeployedAt,
            Notes = _newDeployment.Notes
        });

        _newDeployment.EnvironmentId = 0;
        _newDeployment.VersionId = 0;
        _newDeployment.Notes = null;
        _deployments = (await DeploymentRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private void StartEdit(Deployment deployment)
    {
        _editingId = deployment.Id;
        _editModel = new EditDeploymentModel { Notes = deployment.Notes };
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editModel = null;
    }

    private async Task SaveRenameAsync(int deploymentId)
    {
        await DeploymentRepository.RenameAsync(deploymentId, _editModel!.Notes);
        _editingId = null;
        _editModel = null;
        _deployments = (await DeploymentRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private async Task DeactivateAsync(int deploymentId)
    {
        await DeploymentRepository.DeactivateAsync(deploymentId);
        _deployments = (await DeploymentRepository.GetByApplicationIdAsync(ApplicationId)).ToList();
    }

    private class NewDeploymentModel
    {
        public int EnvironmentId { get; set; }
        public int VersionId { get; set; }
        public DateTime DeployedAt { get; set; } = DateTime.UtcNow;
        public string? Notes { get; set; }
    }

    private class EditDeploymentModel
    {
        public string? Notes { get; set; }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/LogsPlatform.Web/LogsPlatform.Web.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/LogsPlatform.Web/Components/Shared/DeploymentsSection.razor
git commit -m "Restyle and translate DeploymentsSection to Hebrew/RTL"
```

---

### Task 13: Full-solution build and manual browser verification

**Files:** none (verification only).

**Interfaces:**
- Consumes: every file from Tasks 1–12.
- Produces: n/a (final task).

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build LogsPlatform.sln`
Expected: `Build succeeded.` with 0 errors, 0 warnings introduced by this plan's files.

- [ ] **Step 2: Run the existing test suite to confirm no regressions**

Run: `dotnet test LogsPlatform.sln`
Expected: all existing tests pass (they are Controller/API-level and untouched by this plan, so this simply confirms nothing else broke).

- [ ] **Step 3: Start the app and verify in a real browser**

Use the project's `run` skill (or `dotnet run --project src/LogsPlatform.Web`) to start the dev server, then open it in a browser and walk through:
1. `/admin/applications` loads with the dark navbar, RTL layout (page content flows right-to-left, text is right-aligned), and Hebrew nav labels (מה חריג / חיפוש / חריגות greyed out with "בקרוב" badges, ניהול highlighted as active).
2. Create an Application — form is in a card titled "הוספת Application", Hebrew labels, table below lists it with Hebrew column headers.
3. Expand an Application row — Environments table + all six shared sections (Customers, Users, Log Sources, API Keys, Versions, Deployments) render with visible spacing between them, Hebrew labels/buttons, Bootstrap-styled tables.
4. Click into Modules → Screen/Services → Processes → Operations — breadcrumb renders as a proper Bootstrap breadcrumb (current page not a link), each page's create-card and table are styled and in Hebrew.
5. Exercise one full CRUD cycle end-to-end (e.g. create a Module, rename it, deactivate it) to confirm no behavior regressed.
6. Trigger one duplicate-name error (e.g. create two Modules with the same name) and confirm the Hebrew error message renders correctly in a red Bootstrap alert.

- [ ] **Step 4: Stop the dev server**

No commit for this task — it is verification only. If any issue is found, fix it in the relevant task's file and re-run this task's steps.

---

## Execution Notes

- Tasks 2–12 are fully independent of each other (different files, no shared state) and can be dispatched to separate subagents in any order once Task 1 is complete.
- Task 1 must run first — every other task's file references `site.css`'s `app-row-details` class (Task 2 only) or renders inside the `MainLayout`/`NavMenu` shell (all tasks, implicitly via `Routes.razor`).
- Task 13 must run last, after all other tasks are merged.
