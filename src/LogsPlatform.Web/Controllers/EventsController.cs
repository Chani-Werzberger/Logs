using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/events")]
public class EventsController : ControllerBase
{
    private readonly IEventRepository _events;

    public EventsController(IEventRepository events)
    {
        _events = events;
    }

    [HttpGet]
    public async Task<ActionResult<EventListResponse>> Query(
        [FromQuery] int applicationId, [FromQuery] int environmentId,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? severity,
        [FromQuery] int? moduleId, [FromQuery] int? screenServiceId, [FromQuery] int? processId, [FromQuery] int? operationId,
        [FromQuery] string? correlationId, [FromQuery] string? traceId, [FromQuery] string? userId, [FromQuery] string? customerId,
        [FromQuery] long? exceptionGroupId, [FromQuery] int? versionId,
        [FromQuery] double? durationMinMs, [FromQuery] double? durationMaxMs, [FromQuery] string? q,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        int? severityValue = null;
        if (severity is not null)
        {
            if (!SeverityLevels.ByName.TryGetValue(severity, out var parsed))
            {
                return ValidationProblem($"severity: invalid value '{severity}'.");
            }
            severityValue = parsed;
        }

        var (items, totalCount) = await _events.QueryAsync(new EventQueryParameters(
            applicationId, environmentId, from, to, severityValue,
            moduleId, screenServiceId, processId, operationId,
            correlationId, traceId, userId, customerId,
            exceptionGroupId, versionId, durationMinMs, durationMaxMs, q, page, pageSize));

        return new EventListResponse(items.Select(ToSummary).ToList(), totalCount);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<EventDetail>> GetById(long id, [FromQuery] int applicationId)
    {
        var evt = await _events.GetByIdAsync(applicationId, id);
        if (evt is null) return NotFound();
        return ToDetail(evt);
    }

    internal static EventSummary ToSummary(Event evt) =>
        new(evt.Id, evt.Timestamp, SeverityLevels.ByValue[evt.Severity], BuildOperationPath(evt), evt.Message, evt.DurationMs, evt.CorrelationId);

    internal static EventDetail ToDetail(Event evt) =>
        new(evt.Id, evt.Timestamp, SeverityLevels.ByValue[evt.Severity], evt.ApplicationId, evt.EnvironmentId,
            evt.VersionId, evt.ModuleId, evt.ScreenServiceId, evt.ProcessId, evt.OperationId,
            evt.CustomerId, evt.AppUserId, evt.EventKey, evt.CorrelationId, evt.TraceId,
            evt.SpanId, evt.ParentSpanId, evt.DurationMs, evt.Message, evt.MessageTemplate,
            evt.ExceptionGroupId, evt.StackTrace, evt.MetadataJson, BuildOperationPath(evt));

    internal static string? BuildOperationPath(Event evt)
    {
        var segments = new[] { evt.Module?.Name, evt.ScreenService?.Name, evt.Process?.Name, evt.Operation?.Name }
            .Where(name => name is not null);
        var path = string.Join(" / ", segments);
        return path.Length == 0 ? null : path;
    }
}
