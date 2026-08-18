# A3: Hierarchy UI Drill-Down Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Blazor Server UI for the full `Application → AppModule → ScreenService → ProcessNode → Operation` hierarchy — drill-down navigation with breadcrumbs, Create/Rename/Deactivate at each of the four levels — built on top of the already-merged A1+A2 backend (`main` currently has 72/72 passing tests covering that backend).

**Architecture:** Same Blazor Server + direct-repository-injection pattern already established by `ApplicationsAdmin.razor` (no HTTP calls to the app's own API). One new shared service (`BreadcrumbBuilder`), four new pages (one per hierarchy level, each a separate `.razor` file — not a generic component, matching the established precedent), and one small addition to the existing `ApplicationsAdmin.razor`.

**Tech Stack:** Same as the current solution — .NET 10, EF Core 10.0.11, SQL Server, Blazor Server (Interactive Server render mode), xUnit. No new packages.

## Global Constraints

- **Data access is direct repository injection (`@inject`), never HTTP calls to the app's own API** — matching `ApplicationsAdmin.razor`'s established architecture (`src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor`).
- **Duplicate-name handling uses `DbUpdateExceptionExtensions.IsUniqueViolation()`** (`src/LogsPlatform.Web/DbUpdateExceptionExtensions.cs`, already merged) — `catch (DbUpdateException ex) when (ex.IsUniqueViolation())`. Do not reintroduce the older inline `SqlException { Number: 2601 or 2627 }` pattern that `ApplicationsAdmin.razor` still has (that file is intentionally left as-is — not part of this plan's scope). Every new `.razor` file needs `@using LogsPlatform.Web` for the extension method to resolve, and `@using Microsoft.EntityFrameworkCore` for `DbUpdateException` itself.
- **Rename is an inline edit toggle, one row at a time.** Each page tracks a single `int? _editingId`. A row's "Edit" button sets `_editingId` to that row's id and swaps its Name/Description cells for an inline `EditForm` with `Save`/`Cancel` buttons. Only one row can be in edit mode at a time per page (setting `_editingId` to a new row implicitly cancels editing of any other row — this falls out naturally from `_editingId` being a single nullable int, not a set).
- **Deactivate has no confirmation dialog** — it's non-destructive (soft-delete), matching the project's "no custom UI chrome" posture and the hierarchy-spine design doc's Deactivate Semantics.
- **Breadcrumb segment URLs point to that node's own children-list page — the same URL a click on that node's Name would produce from its parent's list page.** Concretely: the `Application` segment's URL is `/admin/applications/{appId}/modules` (its children = Modules), the `AppModule` segment's URL is `/admin/applications/{appId}/modules/{moduleId}/screen-services` (its children = ScreenServices), and so on. This is the standard "file-manager path" breadcrumb convention (clicking a crumb shows what's *inside* it), not a "show me my siblings" convention — every implementer must use this exact interpretation, not invent their own.
- **UI routes are the full nested path, carrying every ancestor ID** — e.g. `/admin/applications/{appId}/modules/{moduleId}/screen-services/{screenServiceId}/processes` — deliberately different from the Admin API's own parent-scoped-only routes. This is already fixed by the design doc; every route in this plan follows it exactly.
- **`@rendermode InteractiveServer` is required on every new page** (matching `ApplicationsAdmin.razor`) — Blazor Server forms/buttons don't work without it.
- **No bUnit or other new automated Razor-component-testing framework.** `BreadcrumbBuilder` (Task 1) is plain C#, tested with ordinary xUnit against real LocalDB (`TestDatabase.CreateContext()`), same as every other repository-adjacent class in this project. The four page tasks (2-5) have no automated test of their own — each ends with a `curl`-based structural smoke check (confirms the page returns `200 OK` and contains expected content markers) as its acceptance step, the same posture as A1/A2's controller smoke checks. Full interactive browser verification (actually clicking through Create/Rename/Deactivate/breadcrumb-navigation) happens once, across the whole finished branch, as a required manual step before this plan is considered done — not repeated per task.
- **No "show inactive" toggle, no authentication, no custom CSS** — plain HTML tables/forms throughout, matching every prior page in this project.
- Target framework `net10.0`, EF Core packages pinned at `10.0.11` everywhere (already the case — this plan adds no new package references).

---

### Task 1: `BreadcrumbBuilder` service + tests + DI registration

**Files:**
- Create: `src/LogsPlatform.Web/Services/BreadcrumbBuilder.cs`
- Create: `tests/LogsPlatform.Tests/Web/BreadcrumbBuilderTests.cs`
- Modify: `src/LogsPlatform.Web/Program.cs` (register `BreadcrumbBuilder` in DI)

**Interfaces:**
- Consumes: `IApplicationRepository`, `IAppModuleRepository`, `IScreenServiceRepository`, `IProcessNodeRepository` (all existing, already registered in DI).
- Produces: `BreadcrumbSegment` record and `BreadcrumbBuilder.BuildAsync(...)` — consumed by Tasks 2-5's pages.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LogsPlatform.Tests/Web/BreadcrumbBuilderTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class BreadcrumbBuilderTests
{
    private static async Task<(int appId, int moduleId, int screenServiceId, int processId)> CreateFullChainAsync(LogsPlatformDbContext context)
    {
        var application = new Application { Name = "BreadcrumbTestApp", CreatedAt = DateTime.UtcNow };
        var module = new AppModule { Name = "Payments" };
        var screenService = new ScreenService { Name = "PaymentGateway", Type = ScreenServiceType.Service };
        var process = new ProcessNode { Name = "ChargeCard" };
        screenService.Processes.Add(process);
        module.ScreenServices.Add(screenService);
        application.Modules.Add(module);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return (application.Id, module.Id, screenService.Id, process.Id);
    }

    private static BreadcrumbBuilder CreateBuilder(LogsPlatformDbContext context) =>
        new(
            new ApplicationRepository(context),
            new AppModuleRepository(context),
            new ScreenServiceRepository(context),
            new ProcessNodeRepository(context));

    [Fact]
    public async Task BuildAsync_WithOnlyAppId_ReturnsSingleSegmentPointingToModulesPage()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _, _, _) = await CreateFullChainAsync(context);
        var builder = CreateBuilder(context);

        var segments = await builder.BuildAsync(appId);

        Assert.Single(segments);
        Assert.Equal("BreadcrumbTestApp", segments[0].Label);
        Assert.Equal($"/admin/applications/{appId}/modules", segments[0].Url);
    }

    [Fact]
    public async Task BuildAsync_WithModuleId_ReturnsTwoSegments()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, moduleId, _, _) = await CreateFullChainAsync(context);
        var builder = CreateBuilder(context);

        var segments = await builder.BuildAsync(appId, moduleId);

        Assert.Equal(2, segments.Count);
        Assert.Equal("Payments", segments[1].Label);
        Assert.Equal($"/admin/applications/{appId}/modules/{moduleId}/screen-services", segments[1].Url);
    }

    [Fact]
    public async Task BuildAsync_WithFullChain_ReturnsFourSegmentsInRootToLeafOrder()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, moduleId, screenServiceId, processId) = await CreateFullChainAsync(context);
        var builder = CreateBuilder(context);

        var segments = await builder.BuildAsync(appId, moduleId, screenServiceId, processId);

        Assert.Equal(4, segments.Count);
        Assert.Equal("BreadcrumbTestApp", segments[0].Label);
        Assert.Equal("Payments", segments[1].Label);
        Assert.Equal("PaymentGateway", segments[2].Label);
        Assert.Equal("ChargeCard", segments[3].Label);
        Assert.Equal(
            $"/admin/applications/{appId}/modules/{moduleId}/screen-services/{screenServiceId}/processes/{processId}/operations",
            segments[3].Url);
    }

    [Fact]
    public async Task BuildAsync_WithUnknownAppId_Throws()
    {
        using var context = TestDatabase.CreateContext();
        var builder = CreateBuilder(context);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await builder.BuildAsync(999999));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter BreadcrumbBuilderTests`
Expected: FAIL — `BreadcrumbBuilder`/`BreadcrumbSegment` do not exist yet.

- [ ] **Step 3: Implement `BreadcrumbBuilder`**

```csharp
// src/LogsPlatform.Web/Services/BreadcrumbBuilder.cs
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services;

public record BreadcrumbSegment(string Label, string Url);

public class BreadcrumbBuilder
{
    private readonly IApplicationRepository _applications;
    private readonly IAppModuleRepository _modules;
    private readonly IScreenServiceRepository _screenServices;
    private readonly IProcessNodeRepository _processes;

    public BreadcrumbBuilder(
        IApplicationRepository applications,
        IAppModuleRepository modules,
        IScreenServiceRepository screenServices,
        IProcessNodeRepository processes)
    {
        _applications = applications;
        _modules = modules;
        _screenServices = screenServices;
        _processes = processes;
    }

    public async Task<List<BreadcrumbSegment>> BuildAsync(
        int appId, int? moduleId = null, int? screenServiceId = null, int? processId = null)
    {
        var segments = new List<BreadcrumbSegment>();

        var application = await _applications.GetByIdAsync(appId)
            ?? throw new InvalidOperationException($"Application {appId} not found.");
        segments.Add(new BreadcrumbSegment(application.Name, $"/admin/applications/{appId}/modules"));

        if (moduleId is null) return segments;

        var module = await _modules.GetByIdAsync(moduleId.Value)
            ?? throw new InvalidOperationException($"Module {moduleId} not found.");
        segments.Add(new BreadcrumbSegment(module.Name, $"/admin/applications/{appId}/modules/{moduleId}/screen-services"));

        if (screenServiceId is null) return segments;

        var screenService = await _screenServices.GetByIdAsync(screenServiceId.Value)
            ?? throw new InvalidOperationException($"ScreenService {screenServiceId} not found.");
        segments.Add(new BreadcrumbSegment(
            screenService.Name,
            $"/admin/applications/{appId}/modules/{moduleId}/screen-services/{screenServiceId}/processes"));

        if (processId is null) return segments;

        var process = await _processes.GetByIdAsync(processId.Value)
            ?? throw new InvalidOperationException($"ProcessNode {processId} not found.");
        segments.Add(new BreadcrumbSegment(
            process.Name,
            $"/admin/applications/{appId}/modules/{moduleId}/screen-services/{screenServiceId}/processes/{processId}/operations"));

        return segments;
    }
}
```

- [ ] **Step 4: Register `BreadcrumbBuilder` in DI**

Modify `Program.cs` — add this line directly after the existing `AddScoped<IOperationRepository, OperationRepository>();` line:

```csharp
builder.Services.AddScoped<BreadcrumbBuilder>();
```

The full `Program.cs` after this change:

```csharp
// src/LogsPlatform.Web/Program.cs — full file content
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Web.Components;
using LogsPlatform.Web.Services;
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
builder.Services.AddScoped<IAppModuleRepository, AppModuleRepository>();
builder.Services.AddScoped<IScreenServiceRepository, ScreenServiceRepository>();
builder.Services.AddScoped<IProcessNodeRepository, ProcessNodeRepository>();
builder.Services.AddScoped<IOperationRepository, OperationRepository>();
builder.Services.AddScoped<BreadcrumbBuilder>();

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

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter BreadcrumbBuilderTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 76, Skipped: 0, Total: 76` (72 existing + 4 new).

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Services/BreadcrumbBuilder.cs tests/LogsPlatform.Tests/Web/BreadcrumbBuilderTests.cs src/LogsPlatform.Web/Program.cs
git commit -m "Add BreadcrumbBuilder service for hierarchy drill-down UI"
```

---

### Task 2: `ModulesAdmin.razor` + entry-point link from `ApplicationsAdmin.razor`

**Files:**
- Create: `src/LogsPlatform.Web/Components/Pages/ModulesAdmin.razor`
- Modify: `src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor` (add a "Modules" link per row)

**Interfaces:**
- Consumes: `IAppModuleRepository` (existing), `BreadcrumbBuilder` (Task 1).
- Produces: `/admin/applications/{appId}/modules` page — the entry point for the rest of the drill-down (Tasks 3-5 link into it and out of it).

- [ ] **Step 1: Write `ModulesAdmin.razor`**

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

<p>
    @for (var i = 0; i < _breadcrumb.Count; i++)
    {
        var segment = _breadcrumb[i];
        <a href="@segment.Url">@segment.Label</a>
        @if (i < _breadcrumb.Count - 1)
        {
            <text> &gt; </text>
        }
    }
</p>

<h1>Modules</h1>

<h2>Create Module</h2>
<EditForm Model="_newModule" OnValidSubmit="CreateModuleAsync">
    <div>
        <label>
            Name:
            <InputText @bind-Value="_newModule.Name" required />
        </label>
    </div>
    <div>
        <label>
            Description:
            <InputText @bind-Value="_newModule.Description" />
        </label>
    </div>
    <button type="submit">Create</button>
</EditForm>
@if (_createError is not null)
{
    <p style="color:red">@_createError</p>
}

<h2>All Modules</h2>
<table>
    <thead>
        <tr>
            <th>Name</th>
            <th>Description</th>
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
                            <InputText @bind-Value="_editModel!.Name" required />
                            <InputText @bind-Value="_editModel!.Description" />
                            <button type="submit">Save</button>
                            <button type="button" @onclick="CancelEdit">Cancel</button>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <p style="color:red">@_editError</p>
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
                        <button @onclick="() => StartEdit(module)">Edit</button>
                    }
                    <button @onclick="() => DeactivateAsync(module.Id)">Deactivate</button>
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
            _createError = $"A module named '{_newModule.Name}' already exists.";
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
            _editError = $"A module named '{_editModel!.Name}' already exists.";
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

- [ ] **Step 2: Add the "Modules" link to `ApplicationsAdmin.razor`**

Modify the table header row — add one `<th></th>` after the existing expand-toggle `<th></th>`:

```razor
<thead>
    <tr>
        <th></th>
        <th></th>
        <th>Name</th>
        <th>Description</th>
        <th>Created At</th>
    </tr>
</thead>
```

And modify each data row — add one `<td>` immediately after the existing expand-toggle `<td>` (before the Name `<td>`):

```razor
<tr>
    <td>
        <button @onclick="() => ToggleExpandAsync(application.Id)">
            @(_expandedAppIds.Contains(application.Id) ? "-" : "+")
        </button>
    </td>
    <td><a href="/admin/applications/@application.Id/modules">Modules</a></td>
    <td>@application.Name</td>
    <td>@application.Description</td>
    <td>@application.CreatedAt</td>
</tr>
```

The `colspan="4"` on the expanded-Environments row (currently `<td colspan="4">`) must become `colspan="5"`, since the row now has one more column:

```razor
@if (_expandedAppIds.Contains(application.Id))
{
    <tr>
        <td colspan="5">
```

Do not change anything else in the file — the rest of `ApplicationsAdmin.razor` (Create form, Environments expand/collapse logic, `@code` block) is unmodified.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 76, Skipped: 0, Total: 76` — unchanged (this task adds no automated tests, per this plan's Testing Global Constraint).

- [ ] **Step 5: Manual smoke check**

The dev database must already have A1/A2's migrations applied (it does, if you followed `docs/database-setup.md` after those plans merged — if unsure, run `dotnet ef database update --project src/LogsPlatform.Infrastructure --connection "<your dev connection string>"` first).

```bash
dotnet run --project src/LogsPlatform.Web --launch-profile http &
sleep 5
curl -s http://localhost:5201/admin/applications | grep -o "Modules" | head -1
```
Expected: prints `Modules` — confirms the new link renders on the Applications page without a server error. Then, using a real `appId` from your dev database (create one via `/admin/applications` if needed):
```bash
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5201/admin/applications/<appId>/modules
```
Expected: `200`. Stop the background process afterward with `taskkill //F //IM dotnet.exe`.

This is a structural smoke check only — it does not exercise Create/Rename/Deactivate interactively (Blazor Server's forms require a live SignalR circuit, which `curl` can't drive). Full interactive verification happens once, across the whole finished branch, before this plan is considered done (see the plan's closing section).

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/ModulesAdmin.razor src/LogsPlatform.Web/Components/Pages/ApplicationsAdmin.razor
git commit -m "Add ModulesAdmin.razor and entry-point link from ApplicationsAdmin"
```

---

### Task 3: `ScreenServicesAdmin.razor`

**Files:**
- Create: `src/LogsPlatform.Web/Components/Pages/ScreenServicesAdmin.razor`

**Interfaces:**
- Consumes: `IScreenServiceRepository` (existing), `BreadcrumbBuilder` (Task 1).
- Produces: `/admin/applications/{appId}/modules/{moduleId}/screen-services` page — linked from Task 2's `ModulesAdmin.razor` rows, links onward to Task 4's page.

- [ ] **Step 1: Write `ScreenServicesAdmin.razor`**

Unlike the other three levels, `ScreenService` has a `Type` field (`ScreenServiceType`: `Screen`/`Service`). Since this page injects the repository directly (not the HTTP API), it binds to the enum directly — no string parsing needed (that was only required for the HTTP API's JSON contract). `Type` is set at creation and is **not** editable via Rename (matching the API's `RenameScreenServiceRequest`, which only carries `Name`/`Description` — this is an intentional, already-established constraint, not an oversight).

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

<p>
    @for (var i = 0; i < _breadcrumb.Count; i++)
    {
        var segment = _breadcrumb[i];
        <a href="@segment.Url">@segment.Label</a>
        @if (i < _breadcrumb.Count - 1)
        {
            <text> &gt; </text>
        }
    }
</p>

<h1>Screen/Services</h1>

<h2>Create Screen/Service</h2>
<EditForm Model="_newScreenService" OnValidSubmit="CreateScreenServiceAsync">
    <div>
        <label>
            Name:
            <InputText @bind-Value="_newScreenService.Name" required />
        </label>
    </div>
    <div>
        <label>
            Type:
            <InputSelect @bind-Value="_newScreenService.Type">
                <option value="@ScreenServiceType.Screen">Screen</option>
                <option value="@ScreenServiceType.Service">Service</option>
            </InputSelect>
        </label>
    </div>
    <div>
        <label>
            Description:
            <InputText @bind-Value="_newScreenService.Description" />
        </label>
    </div>
    <button type="submit">Create</button>
</EditForm>
@if (_createError is not null)
{
    <p style="color:red">@_createError</p>
}

<h2>All Screen/Services</h2>
<table>
    <thead>
        <tr>
            <th>Name</th>
            <th>Type</th>
            <th>Description</th>
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
                            <InputText @bind-Value="_editModel!.Name" required />
                            <InputText @bind-Value="_editModel!.Description" />
                            <button type="submit">Save</button>
                            <button type="button" @onclick="CancelEdit">Cancel</button>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <p style="color:red">@_editError</p>
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
                        <button @onclick="() => StartEdit(screenService)">Edit</button>
                    }
                    <button @onclick="() => DeactivateAsync(screenService.Id)">Deactivate</button>
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
            _createError = $"A screen/service named '{_newScreenService.Name}' already exists.";
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
            _editError = $"A screen/service named '{_editModel!.Name}' already exists.";
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

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 76, Skipped: 0, Total: 76` — unchanged.

- [ ] **Step 4: Manual smoke check**

```bash
dotnet run --project src/LogsPlatform.Web --launch-profile http &
sleep 5
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5201/admin/applications/<appId>/modules/<moduleId>/screen-services
```
Expected: `200` (use real `appId`/`moduleId` values from your dev database — create them via the UI or `/swagger` if needed). Stop the background process afterward with `taskkill //F //IM dotnet.exe`.

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/ScreenServicesAdmin.razor
git commit -m "Add ScreenServicesAdmin.razor"
```

---

### Task 4: `ProcessesAdmin.razor`

**Files:**
- Create: `src/LogsPlatform.Web/Components/Pages/ProcessesAdmin.razor`

**Interfaces:**
- Consumes: `IProcessNodeRepository` (existing), `BreadcrumbBuilder` (Task 1).
- Produces: `/admin/applications/{appId}/modules/{moduleId}/screen-services/{screenServiceId}/processes` page — linked from Task 3's rows, links onward to Task 5's page.

- [ ] **Step 1: Write `ProcessesAdmin.razor`**

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

<p>
    @for (var i = 0; i < _breadcrumb.Count; i++)
    {
        var segment = _breadcrumb[i];
        <a href="@segment.Url">@segment.Label</a>
        @if (i < _breadcrumb.Count - 1)
        {
            <text> &gt; </text>
        }
    }
</p>

<h1>Processes</h1>

<h2>Create Process</h2>
<EditForm Model="_newProcess" OnValidSubmit="CreateProcessAsync">
    <div>
        <label>
            Name:
            <InputText @bind-Value="_newProcess.Name" required />
        </label>
    </div>
    <div>
        <label>
            Description:
            <InputText @bind-Value="_newProcess.Description" />
        </label>
    </div>
    <button type="submit">Create</button>
</EditForm>
@if (_createError is not null)
{
    <p style="color:red">@_createError</p>
}

<h2>All Processes</h2>
<table>
    <thead>
        <tr>
            <th>Name</th>
            <th>Description</th>
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
                            <InputText @bind-Value="_editModel!.Name" required />
                            <InputText @bind-Value="_editModel!.Description" />
                            <button type="submit">Save</button>
                            <button type="button" @onclick="CancelEdit">Cancel</button>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <p style="color:red">@_editError</p>
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
                        <button @onclick="() => StartEdit(process)">Edit</button>
                    }
                    <button @onclick="() => DeactivateAsync(process.Id)">Deactivate</button>
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
            _createError = $"A process named '{_newProcess.Name}' already exists.";
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
            _editError = $"A process named '{_editModel!.Name}' already exists.";
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

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 76, Skipped: 0, Total: 76` — unchanged.

- [ ] **Step 4: Manual smoke check**

```bash
dotnet run --project src/LogsPlatform.Web --launch-profile http &
sleep 5
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5201/admin/applications/<appId>/modules/<moduleId>/screen-services/<screenServiceId>/processes
```
Expected: `200` (use real IDs from your dev database). Stop the background process afterward with `taskkill //F //IM dotnet.exe`.

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/ProcessesAdmin.razor
git commit -m "Add ProcessesAdmin.razor"
```

---

### Task 5: `OperationsAdmin.razor`

**Files:**
- Create: `src/LogsPlatform.Web/Components/Pages/OperationsAdmin.razor`

**Interfaces:**
- Consumes: `IOperationRepository` (existing), `BreadcrumbBuilder` (Task 1).
- Produces: `/admin/applications/{appId}/modules/{moduleId}/screen-services/{screenServiceId}/processes/{processId}/operations` page — the leaf level, linked from Task 4's rows. No further drill-down (Operations have no children).

- [ ] **Step 1: Write `OperationsAdmin.razor`**

Identical shape to Task 4's page, one level deeper, with the Name column rendered as **plain text, not a link** (there's nothing further to drill into):

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

<p>
    @for (var i = 0; i < _breadcrumb.Count; i++)
    {
        var segment = _breadcrumb[i];
        <a href="@segment.Url">@segment.Label</a>
        @if (i < _breadcrumb.Count - 1)
        {
            <text> &gt; </text>
        }
    }
</p>

<h1>Operations</h1>

<h2>Create Operation</h2>
<EditForm Model="_newOperation" OnValidSubmit="CreateOperationAsync">
    <div>
        <label>
            Name:
            <InputText @bind-Value="_newOperation.Name" required />
        </label>
    </div>
    <div>
        <label>
            Description:
            <InputText @bind-Value="_newOperation.Description" />
        </label>
    </div>
    <button type="submit">Create</button>
</EditForm>
@if (_createError is not null)
{
    <p style="color:red">@_createError</p>
}

<h2>All Operations</h2>
<table>
    <thead>
        <tr>
            <th>Name</th>
            <th>Description</th>
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
                            <InputText @bind-Value="_editModel!.Name" required />
                            <InputText @bind-Value="_editModel!.Description" />
                            <button type="submit">Save</button>
                            <button type="button" @onclick="CancelEdit">Cancel</button>
                        </EditForm>
                        @if (_editError is not null)
                        {
                            <p style="color:red">@_editError</p>
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
                        <button @onclick="() => StartEdit(operation)">Edit</button>
                    }
                    <button @onclick="() => DeactivateAsync(operation.Id)">Deactivate</button>
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
            _createError = $"An operation named '{_newOperation.Name}' already exists.";
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
            _editError = $"An operation named '{_editModel!.Name}' already exists.";
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

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Run the full existing test suite to confirm no regression**

Run: `dotnet test`
Expected: `Passed! - Failed: 0, Passed: 76, Skipped: 0, Total: 76` — unchanged.

- [ ] **Step 4: Manual smoke check**

```bash
dotnet run --project src/LogsPlatform.Web --launch-profile http &
sleep 5
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5201/admin/applications/<appId>/modules/<moduleId>/screen-services/<screenServiceId>/processes/<processId>/operations
```
Expected: `200` (use real IDs from your dev database). Stop the background process afterward with `taskkill //F //IM dotnet.exe`.

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Web/Components/Pages/OperationsAdmin.razor
git commit -m "Add OperationsAdmin.razor — completes the full 4-level hierarchy UI"
```

---

## Self-Review Notes

- **Spec coverage:** every element of `docs/superpowers/specs/2026-08-18-application-hierarchy-a3-ui-drilldown-design.md` is implemented — the 4 pages, the entry-point link, the `BreadcrumbBuilder` service, the inline-edit Rename mechanism, the no-confirmation Deactivate, and the `ScreenService.Type` dropdown handled via direct enum binding.
- **Type consistency:** every page's injected repository interface (`IAppModuleRepository`, `IScreenServiceRepository`, `IProcessNodeRepository`, `IOperationRepository`) and method signatures (`GetByXIdAsync`, `AddAsync`, `RenameAsync`, `DeactivateAsync`) were re-verified directly against the actual current files on `main` before writing this plan — not from memory. `BreadcrumbBuilder.BuildAsync`'s signature is used identically across all four page tasks.
- **Route consistency:** every page's `@page` route, every row's drill-down link `href`, and every `BreadcrumbBuilder` call's resulting segment URLs were cross-checked against each other for exact string-for-string agreement (e.g. `ModulesAdmin.razor`'s row link to `/admin/applications/{AppId}/modules/{module.Id}/screen-services` matches `ScreenServicesAdmin.razor`'s own `@page` route exactly).
- **No placeholders:** every step has complete, runnable code or an exact command with an expected result, including the running test-count expectation (72 → 76, then flat through Tasks 2-5 since those add no automated tests — matching the design doc's stated testing posture, not an oversight).

## After This Plan: Required Full Interactive Verification

Once all 5 tasks are merged, before this plan is considered fully done, walk through the entire drill-down live in a browser (starting from `/admin/applications`):
1. Click "Modules" on an existing Application → create a Module → click its name to drill into Screen/Services.
2. Create a Screen/Service (try both `Screen` and `Service` types) → click its name to drill into Processes.
3. Create a Process → click its name to drill into Operations.
4. Create an Operation (leaf level — confirm its Name is plain text, not a link).
5. At each level: click "Edit" on a row, change the Name, click "Save" — confirm it updates in the table. Click "Edit" again, click "Cancel" — confirm no change.
6. At each level: try creating a second item with a duplicate name — confirm the inline error message appears (both for Create and for Rename-into-a-duplicate).
7. At each level: click "Deactivate" on a row — confirm it disappears from the table.
8. Click every breadcrumb segment from the deepest page (Operations) back up to Applications — confirm each one navigates to the expected level and shows the expected ancestor's children.

This matches the same manual-verification requirement the first two Blazor UI slices in this project required before being considered complete.
