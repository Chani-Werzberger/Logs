using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/exception-groups")]
public class ExceptionGroupsController : ControllerBase
{
    private const int TrendWindowDays = 14;

    private readonly IExceptionGroupRepository _exceptionGroups;

    public ExceptionGroupsController(IExceptionGroupRepository exceptionGroups)
    {
        _exceptionGroups = exceptionGroups;
    }

    [HttpGet]
    public async Task<ActionResult<List<ExceptionGroupSummary>>> Query(
        [FromQuery] int applicationId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string sortBy = "LastSeenAt")
    {
        var groups = await _exceptionGroups.QueryAsync(new ExceptionGroupQueryParameters(applicationId, from, to, sortBy));

        var result = new List<ExceptionGroupSummary>();
        foreach (var group in groups)
        {
            var dailyCounts = await _exceptionGroups.GetDailyCountsAsync(group.Id, TrendWindowDays);
            var contexts = await _exceptionGroups.GetAffectedContextsAsync(group.Id);
            var operations = contexts.Select(c => c.OperationName).Where(name => name is not null).Distinct().Select(name => name!).ToList();

            result.Add(new ExceptionGroupSummary(group.Id, group.Fingerprint, group.ExceptionType, group.OccurrenceCount, group.FirstSeenAt, group.LastSeenAt, dailyCounts, operations));
        }

        return result;
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ExceptionGroupDetail>> GetById(long id)
    {
        var group = await _exceptionGroups.GetByIdAsync(id);
        if (group is null) return NotFound();

        var dailyCounts = await _exceptionGroups.GetDailyCountsAsync(id, TrendWindowDays);
        var contexts = await _exceptionGroups.GetAffectedContextsAsync(id);

        return new ExceptionGroupDetail(group.Id, group.Fingerprint, group.ExceptionType, group.RepresentativeStackTrace,
            group.OccurrenceCount, group.FirstSeenAt, group.LastSeenAt, dailyCounts, contexts);
    }
}
