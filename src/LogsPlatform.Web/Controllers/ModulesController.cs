using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/modules")]
public class ModulesController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppModuleRepository _modules;

    public ModulesController(IApplicationRepository applications, IAppModuleRepository modules)
    {
        _applications = applications;
        _modules = modules;
    }

    [HttpPost]
    public async Task<ActionResult<ModuleResponse>> Create(int appId, CreateModuleRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        try
        {
            var module = await _modules.AddAsync(new AppModule
            {
                ApplicationId = appId,
                Name = request.Name,
                Description = request.Description
            });

            return CreatedAtAction(nameof(GetById), new { appId, id = module.Id }, ToResponse(module));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A module named '{request.Name}' already exists in this application." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ModuleResponse>> GetById(int appId, int id)
    {
        var module = await _modules.GetByIdAsync(id);
        if (module is null || module.ApplicationId != appId) return NotFound();
        return ToResponse(module);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ModuleResponse>>> GetAll(int appId, [FromQuery] bool includeInactive = false)
    {
        var modules = await _modules.GetByApplicationIdAsync(appId, includeInactive);
        return modules.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ModuleResponse>> Rename(int appId, int id, RenameModuleRequest request)
    {
        var existing = await _modules.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        try
        {
            var module = await _modules.RenameAsync(id, request.Name, request.Description);
            return ToResponse(module);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A module named '{request.Name}' already exists in this application." });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _modules.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _modules.DeactivateAsync(id);
        return NoContent();
    }

    private static ModuleResponse ToResponse(AppModule module) =>
        new(module.Id, module.ApplicationId, module.Name, module.Description, module.IsActive);
}
