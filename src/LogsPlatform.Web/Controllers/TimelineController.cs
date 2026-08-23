using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/timeline")]
public class TimelineController : ControllerBase
{
    private readonly IEventRepository _events;

    public TimelineController(IEventRepository events)
    {
        _events = events;
    }

    [HttpGet]
    public async Task<ActionResult<List<EventSummary>>> Get(
        [FromQuery] int applicationId, [FromQuery] string? correlationId, [FromQuery] string? traceId,
        [FromQuery] int? operationId, [FromQuery] string? userId, [FromQuery] string? customerId)
    {
        var modesSupplied = new[]
        {
            correlationId is not null,
            traceId is not null,
            operationId is not null && userId is not null,
            customerId is not null
        }.Count(supplied => supplied);

        if (modesSupplied != 1)
        {
            return ValidationProblem("Exactly one of correlationId, traceId, (operationId+userId), or customerId must be supplied.");
        }

        var timeline = await _events.GetTimelineAsync(new TimelineQuery(applicationId, correlationId, traceId, operationId, userId, customerId));
        return timeline.Select(EventsController.ToSummary).ToList();
    }
}
