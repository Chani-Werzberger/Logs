using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/versions")]
[Authorize(Policy = "RequireAdmin")]
public class VersionsController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppVersionRepository _versions;

    public VersionsController(IApplicationRepository applications, IAppVersionRepository versions)
    {
        _applications = applications;
        _versions = versions;
    }

    [HttpPost]
    public async Task<ActionResult<VersionResponse>> Create(int appId, CreateVersionRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        try
        {
            var version = await _versions.AddAsync(new AppVersion
            {
                ApplicationId = appId,
                VersionNumber = request.VersionNumber,
                ReleaseNotes = request.ReleaseNotes,
                CreatedAt = DateTime.UtcNow
            });

            return CreatedAtAction(nameof(GetById), new { appId, id = version.Id }, ToResponse(version));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A version '{request.VersionNumber}' already exists in this application." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<VersionResponse>> GetById(int appId, int id)
    {
        var version = await _versions.GetByIdAsync(id);
        if (version is null || version.ApplicationId != appId) return NotFound();
        return ToResponse(version);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<VersionResponse>>> GetAll(int appId, [FromQuery] bool includeInactive = false)
    {
        var versions = await _versions.GetByApplicationIdAsync(appId, includeInactive);
        return versions.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<VersionResponse>> Rename(int appId, int id, RenameVersionRequest request)
    {
        var existing = await _versions.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var version = await _versions.RenameAsync(id, request.ReleaseNotes);
        return ToResponse(version);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _versions.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _versions.DeactivateAsync(id);
        return NoContent();
    }

    private static VersionResponse ToResponse(AppVersion version) =>
        new(version.Id, version.ApplicationId, version.VersionNumber, version.ReleaseNotes, version.CreatedAt, version.IsActive);
}
