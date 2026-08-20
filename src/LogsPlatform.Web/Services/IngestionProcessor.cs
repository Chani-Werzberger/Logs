using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;

namespace LogsPlatform.Web.Services;

public record ProcessedEvent(Event? Event, string? RejectReason, IReadOnlyList<(string Field, string Reason)> Warnings);

public class IngestionProcessor
{
    private static readonly Dictionary<string, int> SeverityMap = new()
    {
        ["Trace"] = 1, ["Debug"] = 5, ["Info"] = 9, ["Warn"] = 13, ["Error"] = 17, ["Fatal"] = 21
    };

    private readonly IAppEnvironmentRepository _environments;
    private readonly IAppVersionRepository _versions;
    private readonly ICustomerRepository _customers;
    private readonly IAppUserRepository _appUsers;
    private readonly IExceptionGroupRepository _exceptionGroups;
    private readonly HierarchyResolver _hierarchyResolver;

    public IngestionProcessor(
        IAppEnvironmentRepository environments,
        IAppVersionRepository versions,
        ICustomerRepository customers,
        IAppUserRepository appUsers,
        IExceptionGroupRepository exceptionGroups,
        HierarchyResolver hierarchyResolver)
    {
        _environments = environments;
        _versions = versions;
        _customers = customers;
        _appUsers = appUsers;
        _exceptionGroups = exceptionGroups;
        _hierarchyResolver = hierarchyResolver;
    }

    public async Task<ProcessedEvent> ProcessAsync(int applicationId, IngestEventRequest request)
    {
        if (request.Timestamp is null)
        {
            return Reject("timestamp: required field missing");
        }
        if (string.IsNullOrWhiteSpace(request.Severity) || !SeverityMap.TryGetValue(request.Severity, out var severityValue))
        {
            return Reject($"severity: invalid value '{request.Severity}'");
        }
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Reject("message: required field missing");
        }
        if (string.IsNullOrWhiteSpace(request.Environment))
        {
            return Reject("environment: required field missing");
        }

        var environments = await _environments.GetByApplicationIdAsync(applicationId);
        var environmentEntity = environments.FirstOrDefault(e => e.Name == request.Environment);
        if (environmentEntity is null)
        {
            return Reject($"environment: '{request.Environment}' not found");
        }

        var warnings = new List<(string Field, string Reason)>();

        int? versionId = null;
        if (!string.IsNullOrWhiteSpace(request.Version))
        {
            // AppVersion is an external identity reference: deactivating a version means "this
            // release is retired," not "erase historical linkage for events still arriving that
            // reference it." VersionNumber is unique per-application regardless of IsActive, so
            // includeInactive can never introduce ambiguity here.
            var versions = await _versions.GetByApplicationIdAsync(applicationId, includeInactive: true);
            var versionEntity = versions.FirstOrDefault(v => v.VersionNumber == request.Version);
            if (versionEntity is null)
            {
                warnings.Add(("version", "not found, event stored without version reference"));
            }
            else
            {
                versionId = versionEntity.Id;
            }
        }

        int? customerId = null;
        if (!string.IsNullOrWhiteSpace(request.CustomerId))
        {
            // Customer is an external identity reference: deactivating one means "this customer
            // left," not "erase historical linkage." ExternalCustomerId is unique per-application
            // regardless of IsActive, so includeInactive can never introduce ambiguity here.
            var customers = await _customers.GetByApplicationIdAsync(applicationId, includeInactive: true);
            var customerEntity = customers.FirstOrDefault(c => c.ExternalCustomerId == request.CustomerId);
            if (customerEntity is null)
            {
                warnings.Add(("customerId", "not found, event stored without customer reference"));
            }
            else
            {
                customerId = customerEntity.Id;
            }
        }

        int? appUserId = null;
        if (!string.IsNullOrWhiteSpace(request.UserId))
        {
            // AppUser is an external identity reference: deactivating one doesn't erase
            // historical linkage for events still arriving that reference it. ExternalUserId is
            // unique per-application regardless of IsActive, so includeInactive can never
            // introduce ambiguity here.
            var appUsers = await _appUsers.GetByApplicationIdAsync(applicationId, includeInactive: true);
            var appUserEntity = appUsers.FirstOrDefault(u => u.ExternalUserId == request.UserId);
            if (appUserEntity is null)
            {
                warnings.Add(("userId", "not found, event stored without user reference"));
            }
            else
            {
                appUserId = appUserEntity.Id;
            }
        }

        var hierarchy = request.Hierarchy is null
            ? new HierarchyResolutionResult(null, null, null, null, null)
            : await _hierarchyResolver.ResolveAsync(applicationId, request.Hierarchy.Module, request.Hierarchy.ScreenService, request.Hierarchy.Process, request.Hierarchy.Operation);
        if (hierarchy.WarningField is not null)
        {
            warnings.Add((hierarchy.WarningField, "not found, event stored without this and deeper hierarchy references"));
        }

        long? exceptionGroupId = null;
        string? stackTrace = null;
        if (request.Exception is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Exception.StackTrace))
            {
                // A malformed/incomplete exception object must never reject the whole event -
                // just skip grouping for it and record a warning, same as the other
                // optional-but-unresolvable fields above.
                warnings.Add(("exception", "stackTrace missing, event stored without exception grouping"));
            }
            else
            {
                var fingerprint = ExceptionFingerprinter.Compute(request.Exception.Type, request.Exception.StackTrace, request.MessageTemplate);
                var group = await _exceptionGroups.GetOrCreateAsync(
                    applicationId, fingerprint, request.Exception.Type, request.MessageTemplate ?? string.Empty, request.Exception.StackTrace, request.Timestamp.Value);
                exceptionGroupId = group.Id;
                stackTrace = request.Exception.StackTrace;
            }
        }

        var evt = new Event
        {
            ApplicationId = applicationId,
            Timestamp = request.Timestamp.Value,
            Severity = severityValue,
            EnvironmentId = environmentEntity.Id,
            VersionId = versionId,
            ModuleId = hierarchy.ModuleId,
            ScreenServiceId = hierarchy.ScreenServiceId,
            ProcessId = hierarchy.ProcessId,
            OperationId = hierarchy.OperationId,
            CustomerId = customerId,
            AppUserId = appUserId,
            EventKey = request.EventKey,
            CorrelationId = request.CorrelationId,
            TraceId = request.TraceId,
            SpanId = request.SpanId,
            ParentSpanId = request.ParentSpanId,
            DurationMs = request.DurationMs,
            Message = request.Message,
            MessageTemplate = request.MessageTemplate,
            ExceptionGroupId = exceptionGroupId,
            StackTrace = stackTrace,
            MetadataJson = request.Metadata is null ? null : System.Text.Json.JsonSerializer.Serialize(request.Metadata)
        };

        return new ProcessedEvent(evt, null, warnings);
    }

    private static ProcessedEvent Reject(string reason) => new(null, reason, Array.Empty<(string, string)>());
}
