using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/api-keys")]
[Authorize(Policy = "RequireAdmin")]
public class ApiKeysController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly IApiKeyRepository _apiKeys;

    public ApiKeysController(IApplicationRepository applications, IApiKeyRepository apiKeys)
    {
        _applications = applications;
        _apiKeys = apiKeys;
    }

    [HttpPost]
    public async Task<ActionResult<CreateApiKeyResponse>> Create(int appId, CreateApiKeyRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        var (apiKey, rawKey) = await _apiKeys.AddAsync(appId, request.Label);

        var response = new CreateApiKeyResponse(apiKey.Id, apiKey.ApplicationId, apiKey.Label, apiKey.CreatedAt, rawKey);
        return CreatedAtAction(nameof(GetById), new { appId, id = apiKey.Id }, response);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiKeyResponse>> GetById(int appId, int id)
    {
        var apiKey = await _apiKeys.GetByIdAsync(id);
        if (apiKey is null || apiKey.ApplicationId != appId) return NotFound();
        return ToResponse(apiKey);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApiKeyResponse>>> GetAll(int appId, [FromQuery] bool includeRevoked = false)
    {
        var apiKeys = await _apiKeys.GetByApplicationIdAsync(appId, includeRevoked);
        return apiKeys.Select(ToResponse).ToList();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Revoke(int appId, int id)
    {
        var existing = await _apiKeys.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _apiKeys.RevokeAsync(id);
        return NoContent();
    }

    private static ApiKeyResponse ToResponse(ApiKey apiKey) =>
        new(apiKey.Id, apiKey.ApplicationId, apiKey.Label, apiKey.CreatedAt, apiKey.RevokedAt);
}
