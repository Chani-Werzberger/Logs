using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/screen-services/{screenServiceId:int}/processes")]
[Authorize(Policy = "RequireAdmin")]
public class ProcessesController : ControllerBase
{
    private readonly IScreenServiceRepository _screenServices;
    private readonly IProcessNodeRepository _processes;

    public ProcessesController(IScreenServiceRepository screenServices, IProcessNodeRepository processes)
    {
        _screenServices = screenServices;
        _processes = processes;
    }

    [HttpPost]
    public async Task<ActionResult<ProcessResponse>> Create(int screenServiceId, CreateProcessRequest request)
    {
        if (await _screenServices.GetByIdAsync(screenServiceId) is null)
        {
            return NotFound(new { message = $"ScreenService {screenServiceId} not found." });
        }

        try
        {
            var process = await _processes.AddAsync(new ProcessNode
            {
                ScreenServiceId = screenServiceId,
                Name = request.Name,
                Description = request.Description
            });

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

        try
        {
            var process = await _processes.RenameAsync(id, request.Name, request.Description);
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

        await _processes.DeactivateAsync(id);
        return NoContent();
    }

    private static ProcessResponse ToResponse(ProcessNode process) =>
        new(process.Id, process.ScreenServiceId, process.Name, process.Description, process.IsActive);
}
