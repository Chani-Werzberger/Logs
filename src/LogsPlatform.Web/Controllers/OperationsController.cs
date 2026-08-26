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
[Route("api/v1/admin/processes/{processId:int}/operations")]
public class OperationsController : ControllerBase
{
    private readonly IProcessNodeRepository _processes;
    private readonly IScreenServiceRepository _screenServices;
    private readonly IAppModuleRepository _modules;
    private readonly IOperationRepository _operations;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public OperationsController(
        IProcessNodeRepository processes,
        IScreenServiceRepository screenServices,
        IAppModuleRepository modules,
        IOperationRepository operations,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _processes = processes;
        _screenServices = screenServices;
        _modules = modules;
        _operations = operations;
        _audit = audit;
        _access = access;
    }

    private async Task<int?> ResolveApplicationIdAsync(int processId)
    {
        var process = await _processes.GetByIdAsync(processId);
        if (process is null) return null;

        var screenService = await _screenServices.GetByIdAsync(process.ScreenServiceId);
        if (screenService is null) return null;

        var module = await _modules.GetByIdAsync(screenService.ModuleId);
        return module?.ApplicationId;
    }

    [HttpPost]
    public async Task<ActionResult<OperationResponse>> Create(int processId, CreateOperationRequest request)
    {
        if (await _processes.GetByIdAsync(processId) is null)
        {
            return NotFound(new { message = $"ProcessNode {processId} not found." });
        }

        var applicationId = await ResolveApplicationIdAsync(processId);
        if (applicationId is null)
        {
            return NotFound(new { message = $"ProcessNode {processId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, applicationId.Value))
        {
            return Forbid();
        }

        try
        {
            var operation = await _operations.AddAsync(new Operation
            {
                ProcessId = processId,
                Name = request.Name,
                Description = request.Description
            });

            await _audit.RecordAsync(platformUserId, "Operation", operation.Id.ToString(), "Create", $"Created operation '{operation.Name}' in process {processId}");

            return CreatedAtAction(nameof(GetById), new { processId, id = operation.Id }, ToResponse(operation));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"An operation named '{request.Name}' already exists in this process." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OperationResponse>> GetById(int processId, int id)
    {
        var operation = await _operations.GetByIdAsync(id);
        if (operation is null || operation.ProcessId != processId) return NotFound();
        return ToResponse(operation);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<OperationResponse>>> GetAll(int processId, [FromQuery] bool includeInactive = false)
    {
        var operations = await _operations.GetByProcessIdAsync(processId, includeInactive);
        return operations.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<OperationResponse>> Rename(int processId, int id, RenameOperationRequest request)
    {
        var existing = await _operations.GetByIdAsync(id);
        if (existing is null || existing.ProcessId != processId) return NotFound();

        var applicationId = await ResolveApplicationIdAsync(processId);
        if (applicationId is null) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, applicationId.Value))
        {
            return Forbid();
        }

        try
        {
            var operation = await _operations.RenameAsync(id, request.Name, request.Description);

            await _audit.RecordAsync(platformUserId, "Operation", id.ToString(), "Update", $"Renamed operation {id} to '{request.Name}' in process {processId}");

            return ToResponse(operation);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"An operation named '{request.Name}' already exists in this process." });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int processId, int id)
    {
        var existing = await _operations.GetByIdAsync(id);
        if (existing is null || existing.ProcessId != processId) return NotFound();

        var applicationId = await ResolveApplicationIdAsync(processId);
        if (applicationId is null) return NotFound();

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, applicationId.Value))
        {
            return Forbid();
        }

        await _operations.DeactivateAsync(id);

        await _audit.RecordAsync(platformUserId, "Operation", id.ToString(), "Deactivate", $"Deactivated operation {id} in process {processId}");

        return NoContent();
    }

    private static OperationResponse ToResponse(Operation operation) =>
        new(operation.Id, operation.ProcessId, operation.Name, operation.Description, operation.IsActive);
}
