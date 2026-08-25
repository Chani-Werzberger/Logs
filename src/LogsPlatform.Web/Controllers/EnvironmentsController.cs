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
[Authorize(Policy = "RequireAdmin")]
public class EnvironmentsController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppEnvironmentRepository _environments;
    private readonly AuditLogger _audit;

    public EnvironmentsController(IApplicationRepository applications, IAppEnvironmentRepository environments, AuditLogger audit)
    {
        _applications = applications;
        _environments = environments;
        _audit = audit;
    }

    [HttpPost]
    public async Task<ActionResult<EnvironmentResponse>> Create(int appId, CreateEnvironmentRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var environment = await _environments.AddAsync(new AppEnvironment
        {
            ApplicationId = appId,
            Name = request.Name,
            IsProduction = request.IsProduction
        });

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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
