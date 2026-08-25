using System.Security.Claims;
using Google.Protobuf;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Authentication;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using OpenTelemetry.Proto.Collector.Logs.V1;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("v1/logs")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]
public class OtlpLogsController : ControllerBase
{
    private const int DefaultRateLimitPerMinute = 1000;
    private const string ProtobufContentType = "application/x-protobuf";

    private readonly IngestionProcessor _processor;
    private readonly IEventRepository _events;
    private readonly IMemoryCache _cache;
    private readonly int _rateLimitPerMinute;

    public OtlpLogsController(IngestionProcessor processor, IEventRepository events, IMemoryCache cache, IConfiguration configuration)
    {
        _processor = processor;
        _events = events;
        _cache = cache;
        _rateLimitPerMinute = configuration.GetValue("Ingestion:RateLimitPerMinute", DefaultRateLimitPerMinute);
    }

    [HttpPost]
    [Consumes(ProtobufContentType)]
    public async Task<IActionResult> Export()
    {
        var applicationId = int.Parse(User.FindFirstValue(ApiKeyAuthenticationHandler.ApplicationIdClaimType)!);

        // Kestrel disallows synchronous reads on Request.Body; MessageParser.ParseFrom(Stream)
        // reads synchronously, so the body must be buffered into a seekable MemoryStream first.
        ExportLogsServiceRequest request;
        using (var buffer = new MemoryStream())
        {
            await Request.Body.CopyToAsync(buffer);
            buffer.Position = 0;
            request = ExportLogsServiceRequest.Parser.ParseFrom(buffer);
        }

        var recordCount = request.ResourceLogs.Sum(rl => rl.ScopeLogs.Sum(sl => sl.LogRecords.Count));
        var counter = _cache.GetOrCreate($"ingest-rate:{applicationId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return new RateCounter();
        })!;
        if (Interlocked.Add(ref counter.Count, Math.Max(recordCount, 1)) > _rateLimitPerMinute)
        {
            Response.Headers["Retry-After"] = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var rejectedReasons = new List<string>();
        var toInsert = new List<Event>();

        foreach (var resourceLogs in request.ResourceLogs)
        {
            foreach (var scopeLogs in resourceLogs.ScopeLogs)
            {
                foreach (var logRecord in scopeLogs.LogRecords)
                {
                    var mapped = OtlpLogMapper.Map(logRecord, resourceLogs.Resource);
                    var processed = await _processor.ProcessAsync(applicationId, mapped);
                    if (processed.RejectReason is not null)
                    {
                        rejectedReasons.Add(processed.RejectReason);
                        continue;
                    }
                    toInsert.Add(processed.Event!);
                }
            }
        }

        await _events.AddEventsAsync(applicationId, toInsert);

        var response = new ExportLogsServiceResponse();
        if (rejectedReasons.Count > 0)
        {
            response.PartialSuccess = new ExportLogsPartialSuccess
            {
                RejectedLogRecords = rejectedReasons.Count,
                ErrorMessage = string.Join("; ", rejectedReasons)
            };
        }

        return File(response.ToByteArray(), ProtobufContentType);
    }

    private class RateCounter
    {
        public int Count;
    }
}
