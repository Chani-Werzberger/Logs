using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/modules/{moduleId:int}/screen-services")]
public class ScreenServicesController : ControllerBase
{
    private readonly IScreenServiceRepository _screenServices;

    public ScreenServicesController(IScreenServiceRepository screenServices)
    {
        _screenServices = screenServices;
    }

    [HttpPost]
    public async Task<ActionResult<ScreenServiceResponse>> Create(int moduleId, CreateScreenServiceRequest request)
    {
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

            return CreatedAtAction(nameof(GetById), new { moduleId, id = screenService.Id }, ToResponse(screenService));
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
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

        try
        {
            var screenService = await _screenServices.RenameAsync(id, request.Name, request.Description);
            return ToResponse(screenService);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return Conflict(new { message = $"A screen/service named '{request.Name}' already exists in this module." });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int moduleId, int id)
    {
        var existing = await _screenServices.GetByIdAsync(id);
        if (existing is null || existing.ModuleId != moduleId) return NotFound();

        await _screenServices.DeactivateAsync(id);
        return NoContent();
    }

    private static ScreenServiceResponse ToResponse(ScreenService screenService) =>
        new(screenService.Id, screenService.ModuleId, screenService.Name, screenService.Type.ToString(), screenService.Description, screenService.IsActive);
}
