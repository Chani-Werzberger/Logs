using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Authentication;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/ingest")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]
public class IngestionController : ControllerBase
{
    private const int RateLimitPerMinute = 1000;

    private readonly IngestionProcessor _processor;
    private readonly IEventRepository _events;
    private readonly IMemoryCache _cache;

    public IngestionController(IngestionProcessor processor, IEventRepository events, IMemoryCache cache)
    {
        _processor = processor;
        _events = events;
        _cache = cache;
    }

    [HttpPost("events")]
    public async Task<ActionResult<IngestResponse>> IngestEvents([FromBody] List<IngestEventRequest> requests)
    {
        var applicationId = int.Parse(User.FindFirstValue(ApiKeyAuthenticationHandler.ApplicationIdClaimType)!);

        var counter = _cache.GetOrCreate($"ingest-rate:{applicationId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return new RateCounter();
        })!;
        if (Interlocked.Increment(ref counter.Count) > RateLimitPerMinute)
        {
            Response.Headers["Retry-After"] = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, new { title = "Rate limit exceeded", status = 429 });
        }

        var errors = new List<IngestErrorEntry>();
        var hierarchyWarnings = new List<IngestWarningEntry>();
        var toInsert = new List<Event>();

        for (var index = 0; index < requests.Count; index++)
        {
            var processed = await _processor.ProcessAsync(applicationId, requests[index]);
            if (processed.RejectReason is not null)
            {
                errors.Add(new IngestErrorEntry(index, processed.RejectReason));
                continue;
            }
            foreach (var (field, reason) in processed.Warnings)
            {
                hierarchyWarnings.Add(new IngestWarningEntry(index, field, reason));
            }
            toInsert.Add(processed.Event!);
        }

        await _events.AddEventsAsync(applicationId, toInsert);

        return Accepted(new IngestResponse(toInsert.Count, errors.Count, errors, hierarchyWarnings));
    }

    private class RateCounter
    {
        public int Count;
    }
}
