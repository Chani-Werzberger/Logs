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
[Route("api/v1/admin/applications/{appId:int}/log-sources")]
public class LogSourcesController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly ILogSourceRepository _logSources;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public LogSourcesController(
        IApplicationRepository applications,
        ILogSourceRepository logSources,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _applications = applications;
        _logSources = logSources;
        _audit = audit;
        _access = access;
    }

    [HttpPost]
    public async Task<ActionResult<LogSourceResponse>> Create(int appId, CreateLogSourceRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        try
        {
            var logSource = await _logSources.AddAsync(new LogSource
            {
                ApplicationId = appId,
                Name = request.Name,
                Description = request.Description
            });

            await _audit.RecordAsync(platformUserId, "LogSource", logSource.Id.ToString(), "Create", $"Created log source '{logSource.Name}' in application {appId}");

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

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        try
        {
            var logSource = await _logSources.RenameAsync(id, request.Name, request.Description);

            await _audit.RecordAsync(platformUserId, "LogSource", id.ToString(), "Update", $"Renamed log source {id} to '{request.Name}' in application {appId}");

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

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isSuperAdmin = User.FindFirstValue("IsAdmin") == "true";
        if (!await _access.CanManageApplicationAsync(isSuperAdmin, platformUserId, appId))
        {
            return Forbid();
        }

        await _logSources.DeactivateAsync(id);

        await _audit.RecordAsync(platformUserId, "LogSource", id.ToString(), "Deactivate", $"Deactivated log source {id} in application {appId}");

        return NoContent();
    }

    private static LogSourceResponse ToResponse(LogSource logSource) =>
        new(logSource.Id, logSource.ApplicationId, logSource.Name, logSource.Description, logSource.IsActive);
}
