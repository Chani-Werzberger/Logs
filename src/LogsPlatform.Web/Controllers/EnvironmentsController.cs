using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/environments")]
public class EnvironmentsController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppEnvironmentRepository _environments;
    private readonly AuditLogger _audit;
    private readonly ApplicationAccessService _access;

    public EnvironmentsController(
        IApplicationRepository applications,
        IAppEnvironmentRepository environments,
        AuditLogger audit,
        ApplicationAccessService access)
    {
        _applications = applications;
        _environments = environments;
        _audit = audit;
        _access = access;
    }

    [HttpPost]
    public async Task<ActionResult<EnvironmentResponse>> Create(int appId, CreateEnvironmentRequest request)
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

        var environment = await _environments.AddAsync(new AppEnvironment
        {
            ApplicationId = appId,
            Name = request.Name,
            IsProduction = request.IsProduction
        });

        await _audit.RecordAsync(platformUserId, "AppEnvironment", environment.Id.ToString(), "Create", $"Created environment '{environment.Name}' in application {appId}");

        var response = new EnvironmentResponse(environment.Id, environment.ApplicationId, environment.Name, environment.IsProduction);
        return CreatedAtAction(nameof(GetAll), new { appId }, response);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EnvironmentResponse>>> GetAll(int appId)
    {
        var environments = await _environments.GetByApplicationIdAsync(appId);
        return environments
            .Select(e => new EnvironmentResponse(e.Id, e.ApplicationId, e.Name, e.IsProduction))
            .ToList();
    }
}
