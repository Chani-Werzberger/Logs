using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/deployments")]
public class DeploymentsController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IAppEnvironmentRepository _environments;
    private readonly IAppVersionRepository _versions;
    private readonly IDeploymentRepository _deployments;

    public DeploymentsController(
        IApplicationRepository applications,
        IAppEnvironmentRepository environments,
        IAppVersionRepository versions,
        IDeploymentRepository deployments)
    {
        _applications = applications;
        _environments = environments;
        _versions = versions;
        _deployments = deployments;
    }

    [HttpPost]
    public async Task<ActionResult<DeploymentResponse>> Create(int appId, CreateDeploymentRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var environment = await _environments.GetByIdAsync(request.EnvironmentId);
        if (environment is null || environment.ApplicationId != appId)
        {
            return NotFound(new { message = $"Environment {request.EnvironmentId} not found in application {appId}." });
        }

        var version = await _versions.GetByIdAsync(request.VersionId);
        if (version is null || version.ApplicationId != appId)
        {
            return NotFound(new { message = $"Version {request.VersionId} not found in application {appId}." });
        }

        var deployment = await _deployments.AddAsync(new Deployment
        {
            ApplicationId = appId,
            EnvironmentId = request.EnvironmentId,
            VersionId = request.VersionId,
            DeployedAt = request.DeployedAt,
            Notes = request.Notes
        });

        return CreatedAtAction(nameof(GetById), new { appId, id = deployment.Id }, ToResponse(deployment));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<DeploymentResponse>> GetById(int appId, int id)
    {
        var deployment = await _deployments.GetByIdAsync(id);
        if (deployment is null || deployment.ApplicationId != appId) return NotFound();
        return ToResponse(deployment);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DeploymentResponse>>> GetAll(int appId, [FromQuery] bool includeInactive = false)
    {
        var deployments = await _deployments.GetByApplicationIdAsync(appId, includeInactive);
        return deployments.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<DeploymentResponse>> Rename(int appId, int id, RenameDeploymentRequest request)
    {
        var existing = await _deployments.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var deployment = await _deployments.RenameAsync(id, request.Notes);
        return ToResponse(deployment);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _deployments.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _deployments.DeactivateAsync(id);
        return NoContent();
    }

    private static DeploymentResponse ToResponse(Deployment deployment) =>
        new(deployment.Id, deployment.ApplicationId, deployment.EnvironmentId, deployment.VersionId, deployment.DeployedAt, deployment.Notes, deployment.IsActive);
}
