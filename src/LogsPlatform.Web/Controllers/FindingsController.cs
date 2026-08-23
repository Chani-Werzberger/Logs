using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/findings")]
public class FindingsController : ControllerBase
{
    private readonly IFindingRepository _findings;
    private readonly IApplicationRepository _applications;
    private readonly IAppEnvironmentRepository _environments;
    private readonly IOperationRepository _operations;

    public FindingsController(IFindingRepository findings, IApplicationRepository applications, IAppEnvironmentRepository environments, IOperationRepository operations)
    {
        _findings = findings;
        _applications = applications;
        _environments = environments;
        _operations = operations;
    }

    [HttpGet]
    public async Task<ActionResult<List<FindingSummary>>> Query(
        [FromQuery] int applicationId, [FromQuery] int environmentId,
        [FromQuery] FindingStatus? status, [FromQuery] FindingSeverity? severity, [FromQuery] FindingType? type,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var findings = await _findings.QueryAsync(new FindingQueryParameters(applicationId, environmentId, status, severity, type, from, to));
        var application = await _applications.GetByIdAsync(applicationId);

        var result = new List<FindingSummary>();
        foreach (var finding in findings)
        {
            string? operationName = null;
            if (finding.ScopeType == AnalysisScopeType.Operation)
            {
                var operation = await _operations.GetByIdAsync((int)finding.ScopeId);
                operationName = operation?.Name;
            }

            result.Add(new FindingSummary(finding.Id, finding.Type.ToString(), finding.Title, finding.Severity.ToString(),
                finding.ConfidenceLevel.ToString(), finding.Status.ToString(), finding.DetectedAt, application?.Name ?? string.Empty, operationName));
        }

        return result;
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<FindingDetail>> GetById(long id)
    {
        var details = await _findings.GetByIdAsync(id);
        if (details is null) return NotFound();

        var application = await _applications.GetByIdAsync(details.Finding.ApplicationId);
        var environment = await _environments.GetByIdAsync(details.Finding.EnvironmentId);

        var statements = details.Statements
            .Select(s => new FindingStatementDto(s.Id, s.Kind.ToString(), s.Text, s.OrderIndex, s.ApprovedBy, s.ApprovedAt))
            .ToList();
        var evidence = details.Evidence
            .Select(e => new EvidenceDto(e.Id, e.EvidenceType.ToString(), e.ReferenceId, e.Description))
            .ToList();

        return new FindingDetail(details.Id, details.Finding.Type.ToString(), details.Finding.Title, details.Finding.Severity.ToString(),
            details.Finding.ConfidenceLevel.ToString(), details.Status.ToString(), details.Finding.DetectedAt,
            application?.Name ?? string.Empty, environment?.Name ?? string.Empty, statements, evidence);
    }

    [HttpPatch("{id:long}/status")]
    public async Task<IActionResult> UpdateStatus(long id, [FromBody] UpdateFindingStatusRequest request)
    {
        if (!Enum.TryParse<FindingStatus>(request.Status, ignoreCase: true, out var status))
        {
            return ValidationProblem($"status: invalid value '{request.Status}'.");
        }

        var finding = await _findings.UpdateStatusAsync(id, status);
        if (finding is null) return NotFound();
        return NoContent();
    }

    [HttpPost("{id:long}/statements/{statementId:long}/promote")]
    public async Task<IActionResult> Promote(long id, long statementId, [FromBody] PromoteStatementRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ApprovedBy))
        {
            return ValidationProblem("approvedBy is required.");
        }

        var statement = await _findings.PromoteToConclusionAsync(id, statementId, request.ApprovedBy);
        if (statement is null) return NotFound();
        return NoContent();
    }
}
