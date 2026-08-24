using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/users")]
[Authorize(Policy = "RequireAdmin")]
public class AppUsersController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppUserRepository _users;

    public AppUsersController(IApplicationRepository applications, IAppUserRepository users)
    {
        _applications = applications;
        _users = users;
    }

    [HttpPost]
    public async Task<ActionResult<AppUserResponse>> Create(int appId, CreateAppUserRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        try
        {
            var user = await _users.AddAsync(new AppUser
            {
                ApplicationId = appId,
                ExternalUserId = request.ExternalUserId,
                DisplayName = request.DisplayName
            });

            return CreatedAtAction(nameof(GetById), new { appId, id = user.Id }, ToResponse(user));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A user with external id '{request.ExternalUserId}' already exists in this application." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<AppUserResponse>> GetById(int appId, int id)
    {
        var user = await _users.GetByIdAsync(id);
        if (user is null || user.ApplicationId != appId) return NotFound();
        return ToResponse(user);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AppUserResponse>>> GetAll(int appId, [FromQuery] bool includeInactive = false)
    {
        var users = await _users.GetByApplicationIdAsync(appId, includeInactive);
        return users.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<AppUserResponse>> Rename(int appId, int id, RenameAppUserRequest request)
    {
        var existing = await _users.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var user = await _users.RenameAsync(id, request.DisplayName);
        return ToResponse(user);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _users.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _users.DeactivateAsync(id);
        return NoContent();
    }

    private static AppUserResponse ToResponse(AppUser user) =>
        new(user.Id, user.ApplicationId, user.ExternalUserId, user.DisplayName, user.IsActive);
}
