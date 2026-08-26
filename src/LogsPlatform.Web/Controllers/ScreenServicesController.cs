using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/modules/{moduleId:int}/screen-services")]
public class ScreenServicesController : ControllerBase
{
    private readonly IAppModuleRepository _modules;
    private readonly IScreenServiceRepository _screenServices;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public ScreenServicesController(
        IAppModuleRepository modules,
        IScreenServiceRepository screenServices,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _modules = modules;
        _screenServices = screenServices;
        _audit = audit;
        _access = access;
    }

    [HttpPost]
    public async Task<ActionResult<ScreenServiceResponse>> Create(int moduleId, CreateScreenServiceRequest request)
    {
        var module = await _modules.GetByIdAsync(moduleId);
        if (module is null)
        {
            return NotFound(new { message = $"Module {moduleId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, module.ApplicationId))
        {
            return Forbid();
        }

        if (!Enum.TryParse<ScreenServiceType>(request.Type, ignoreCase: true, out var type))
        {
            return BadRequest(new { message = $"Type must be 'Screen' or 'Service', got '{request.Type}'." });
        }

        try
        {
            var screenService = await _screenServices.AddAsync(new ScreenService
            {
                ModuleId = moduleId,
                Name = request.Name,
                Type = type,
                Description = request.Description
            });

            await _audit.RecordAsync(platformUserId, "ScreenService", screenService.Id.ToString(), "Create", $"Created screen/service '{screenService.Name}' in module {moduleId}");

            return CreatedAtAction(nameof(GetById), new { moduleId, id = screenService.Id }, ToResponse(screenService));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A screen/service named '{request.Name}' already exists in this module." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ScreenServiceResponse>> GetById(int moduleId, int id)
    {
        var screenService = await _screenServices.GetByIdAsync(id);
        if (screenService is null || screenService.ModuleId != moduleId) return NotFound();
        return ToResponse(screenService);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ScreenServiceResponse>>> GetAll(int moduleId, [FromQuery] bool includeInactive = false)
    {
        var screenServices = await _screenServices.GetByModuleIdAsync(moduleId, includeInactive);
        return screenServices.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ScreenServiceResponse>> Rename(int moduleId, int id, RenameScreenServiceRequest request)
    {
        var existing = await _screenServices.GetByIdAsync(id);
        if (existing is null || existing.ModuleId != moduleId) return NotFound();

        var module = await _modules.GetByIdAsync(moduleId);
        if (module is null) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, module.ApplicationId))
        {
            return Forbid();
        }

        try
        {
            var screenService = await _screenServices.RenameAsync(id, request.Name, request.Description);

            await _audit.RecordAsync(platformUserId, "ScreenService", id.ToString(), "Update", $"Renamed screen/service {id} to '{request.Name}' in module {moduleId}");

            return ToResponse(screenService);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A screen/service named '{request.Name}' already exists in this module." });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int moduleId, int id)
    {
        var existing = await _screenServices.GetByIdAsync(id);
        if (existing is null || existing.ModuleId != moduleId) return NotFound();

        var module = await _modules.GetByIdAsync(moduleId);
        if (module is null) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, module.ApplicationId))
        {
            return Forbid();
        }

        await _screenServices.DeactivateAsync(id);

        await _audit.RecordAsync(platformUserId, "ScreenService", id.ToString(), "Deactivate", $"Deactivated screen/service {id} in module {moduleId}");

        return NoContent();
    }

    private static ScreenServiceResponse ToResponse(ScreenService screenService) =>
        new(screenService.Id, screenService.ModuleId, screenService.Name, screenService.Type.ToString(), screenService.Description, screenService.IsActive);
}
