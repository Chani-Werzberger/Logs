using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/processes/{processId:int}/operations")]
public class OperationsController : ControllerBase
{
    private readonly IProcessNodeRepository _processes;
    private readonly IOperationRepository _operations;

    public OperationsController(IProcessNodeRepository processes, IOperationRepository operations)
    {
        _processes = processes;
        _operations = operations;
    }

    [HttpPost]
    public async Task<ActionResult<OperationResponse>> Create(int processId, CreateOperationRequest request)
    {
        if (await _processes.GetByIdAsync(processId) is null)
        {
            return NotFound(new { message = $"ProcessNode {processId} not found." });
        }

        try
        {
            var operation = await _operations.AddAsync(new Operation
            {
                ProcessId = processId,
                Name = request.Name,
                Description = request.Description
            });

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

        try
        {
            var operation = await _operations.RenameAsync(id, request.Name, request.Description);
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

        await _operations.DeactivateAsync(id);
        return NoContent();
    }

    private static OperationResponse ToResponse(Operation operation) =>
        new(operation.Id, operation.ProcessId, operation.Name, operation.Description, operation.IsActive);
}
