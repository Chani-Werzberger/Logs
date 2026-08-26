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
[Route("api/v1/admin/screen-services/{screenServiceId:int}/processes")]
public class ProcessesController : ControllerBase
{
    private readonly IScreenServiceRepository _screenServices;
    private readonly IAppModuleRepository _modules;
    private readonly IProcessNodeRepository _processes;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public ProcessesController(
        IScreenServiceRepository screenServices,
        IAppModuleRepository modules,
        IProcessNodeRepository processes,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _screenServices = screenServices;
        _modules = modules;
        _processes = processes;
        _audit = audit;
        _access = access;
    }

    private async Task<int?> ResolveApplicationIdAsync(int screenServiceId)
    {
        var screenService = await _screenServices.GetByIdAsync(screenServiceId);
        if (screenService is null) return null;

        var module = await _modules.GetByIdAsync(screenService.ModuleId);
        return module?.ApplicationId;
    }

    [HttpPost]
    public async Task<ActionResult<ProcessResponse>> Create(int screenServiceId, CreateProcessRequest request)
    {
        if (await _screenServices.GetByIdAsync(screenServiceId) is null)
        {
            return NotFound(new { message = $"ScreenService {screenServiceId} not found." });
        }

        var applicationId = await ResolveApplicationIdAsync(screenServiceId);
        if (applicationId is null)
        {
            return NotFound(new { message = $"ScreenService {screenServiceId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, applicationId.Value))
        {
            return Forbid();
        }

        try
        {
            var process = await _processes.AddAsync(new ProcessNode
            {
                ScreenServiceId = screenServiceId,
                Name = request.Name,
                Description = request.Description
            });

            await _audit.RecordAsync(platformUserId, "ProcessNode", process.Id.ToString(), "Create", $"Created process '{process.Name}' in screen/service {screenServiceId}");

            return CreatedAtAction(nameof(GetById), new { screenServiceId, id = process.Id }, ToResponse(process));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A process named '{request.Name}' already exists in this screen/service." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProcessResponse>> GetById(int screenServiceId, int id)
    {
        var process = await _processes.GetByIdAsync(id);
        if (process is null || process.ScreenServiceId != screenServiceId) return NotFound();
        return ToResponse(process);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProcessResponse>>> GetAll(int screenServiceId, [FromQuery] bool includeInactive = false)
    {
        var processes = await _processes.GetByScreenServiceIdAsync(screenServiceId, includeInactive);
        return processes.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ProcessResponse>> Rename(int screenServiceId, int id, RenameProcessRequest request)
    {
        var existing = await _processes.GetByIdAsync(id);
        if (existing is null || existing.ScreenServiceId != screenServiceId) return NotFound();

        var applicationId = await ResolveApplicationIdAsync(screenServiceId);
        if (applicationId is null) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, applicationId.Value))
        {
            return Forbid();
        }

        try
        {
            var process = await _processes.RenameAsync(id, request.Name, request.Description);

            await _audit.RecordAsync(platformUserId, "ProcessNode", id.ToString(), "Update", $"Renamed process {id} to '{request.Name}' in screen/service {screenServiceId}");

            return ToResponse(process);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A process named '{request.Name}' already exists in this screen/service." });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int screenServiceId, int id)
    {
        var existing = await _processes.GetByIdAsync(id);
        if (existing is null || existing.ScreenServiceId != screenServiceId) return NotFound();

        var applicationId = await ResolveApplicationIdAsync(screenServiceId);
        if (applicationId is null) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, applicationId.Value))
        {
            return Forbid();
        }

        await _processes.DeactivateAsync(id);

        await _audit.RecordAsync(platformUserId, "ProcessNode", id.ToString(), "Deactivate", $"Deactivated process {id} in screen/service {screenServiceId}");

        return NoContent();
    }

    private static ProcessResponse ToResponse(ProcessNode process) =>
        new(process.Id, process.ScreenServiceId, process.Name, process.Description, process.IsActive);
}
