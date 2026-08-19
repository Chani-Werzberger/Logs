using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/log-sources")]
public class LogSourcesController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly ILogSourceRepository _logSources;

    public LogSourcesController(IApplicationRepository applications, ILogSourceRepository logSources)
    {
        _applications = applications;
        _logSources = logSources;
    }

    [HttpPost]
    public async Task<ActionResult<LogSourceResponse>> Create(int appId, CreateLogSourceRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        try
        {
            var logSource = await _logSources.AddAsync(new LogSource
            {
                ApplicationId = appId,
                Name = request.Name,
                Description = request.Description
            });

            return CreatedAtAction(nameof(GetById), new { appId, id = logSource.Id }, ToResponse(logSource));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A log source named '{request.Name}' already exists in this application." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<LogSourceResponse>> GetById(int appId, int id)
    {
        var logSource = await _logSources.GetByIdAsync(id);
        if (logSource is null || logSource.ApplicationId != appId) return NotFound();
        return ToResponse(logSource);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LogSourceResponse>>> GetAll(int appId, [FromQuery] bool includeInactive = false)
    {
        var logSources = await _logSources.GetByApplicationIdAsync(appId, includeInactive);
        return logSources.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<LogSourceResponse>> Rename(int appId, int id, RenameLogSourceRequest request)
    {
        var existing = await _logSources.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        try
        {
            var logSource = await _logSources.RenameAsync(id, request.Name, request.Description);
            return ToResponse(logSource);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A log source named '{request.Name}' already exists in this application." });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _logSources.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _logSources.DeactivateAsync(id);
        return NoContent();
    }

    private static LogSourceResponse ToResponse(LogSource logSource) =>
        new(logSource.Id, logSource.ApplicationId, logSource.Name, logSource.Description, logSource.IsActive);
}
