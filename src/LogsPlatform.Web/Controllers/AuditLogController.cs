using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/audit-log")]
[Authorize(Policy = "RequireAdmin")]
public class AuditLogController : ControllerBase
{
    private readonly IAuditLogRepository _auditLog;

    public AuditLogController(IAuditLogRepository auditLog)
    {
        _auditLog = auditLog;
    }

    [HttpGet]
    public async Task<ActionResult<AuditLogListResponse>> Query(
        [FromQuery] int? platformUserId, [FromQuery] string? entityType, [FromQuery] string? action,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var (items, totalCount) = await _auditLog.QueryAsync(
            new AuditLogQueryParameters(platformUserId, entityType, action, from, to, page, pageSize));

        return new AuditLogListResponse(
            items.Select(e => new AuditLogEntrySummary(e.Id, e.PlatformUserId, e.PlatformUser.Username, e.Timestamp, e.EntityType, e.EntityId, e.Action, e.Description)).ToList(),
            totalCount);
    }
}
