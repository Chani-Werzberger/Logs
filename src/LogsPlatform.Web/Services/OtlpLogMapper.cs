using System.Text.Json;
using LogsPlatform.Web.Contracts;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace LogsPlatform.Web.Services;

public static class OtlpLogMapper
{
    private static readonly HashSet<string> MappedAttributeKeys = new()
    {
        "logsplatform.module", "logsplatform.screen_service", "logsplatform.process", "logsplatform.operation",
        "logsplatform.customer_id", "logsplatform.user_id", "exception.type", "exception.stacktrace"
    };

    public static IngestEventRequest Map(LogRecord record, Resource? resource)
    {
        var moduleAttr = FindStringAttribute(record.Attributes, "logsplatform.module");
        var screenServiceAttr = FindStringAttribute(record.Attributes, "logsplatform.screen_service");
        var processAttr = FindStringAttribute(record.Attributes, "logsplatform.process");
        var operationAttr = FindStringAttribute(record.Attributes, "logsplatform.operation");
        IngestHierarchyRequest? hierarchy = moduleAttr is null && screenServiceAttr is null && processAttr is null && operationAttr is null
            ? null
            : new IngestHierarchyRequest(moduleAttr, screenServiceAttr, processAttr, operationAttr);

        var exceptionType = FindStringAttribute(record.Attributes, "exception.type");
        IngestExceptionRequest? exception = exceptionType is null
            ? null
            : new IngestExceptionRequest(exceptionType, FindStringAttribute(record.Attributes, "exception.stacktrace"));

        Dictionary<string, object>? metadata = null;
        foreach (var attribute in record.Attributes)
        {
            if (MappedAttributeKeys.Contains(attribute.Key))
            {
                continue;
            }
            metadata ??= new Dictionary<string, object>();
            metadata[attribute.Key] = attribute.Value is null ? string.Empty : AnyValueToObject(attribute.Value);
        }

        return new IngestEventRequest(
            EventKey: null,
            Timestamp: UnixNanoToDateTime(record.TimeUnixNano),
            Severity: SeverityLevels.FromOtelSeverityNumber((int)record.SeverityNumber),
            // Resource.Attributes["service.name"] is intentionally not read: Application identity
            // comes from the API Key alone, matching every other ingestion path in this project.
            Environment: resource is null ? null : FindStringAttribute(resource.Attributes, "deployment.environment"),
            Version: null,
            Hierarchy: hierarchy,
            CorrelationId: null,
            TraceId: ByteStringToHex(record.TraceId),
            SpanId: ByteStringToHex(record.SpanId),
            ParentSpanId: null,
            DurationMs: null,
            CustomerId: FindStringAttribute(record.Attributes, "logsplatform.customer_id"),
            UserId: FindStringAttribute(record.Attributes, "logsplatform.user_id"),
            Message: AnyValueToMessage(record.Body),
            MessageTemplate: null,
            Exception: exception,
            Metadata: metadata);
    }

    private static DateTime? UnixNanoToDateTime(ulong timeUnixNano)
    {
        if (timeUnixNano == 0)
        {
            return null;
        }
        return DateTime.UnixEpoch.AddTicks((long)(timeUnixNano / 100));
    }

    private static string? ByteStringToHex(Google.Protobuf.ByteString id) =>
        id.Length == 0 ? null : Convert.ToHexString(id.Span).ToLowerInvariant();

    private static string? FindStringAttribute(IEnumerable<KeyValue> attributes, string key)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.Key != key)
            {
                continue;
            }
            return attribute.Value?.ValueCase == AnyValue.ValueOneofCase.StringValue ? attribute.Value.StringValue : null;
        }
        return null;
    }

    private static string? AnyValueToMessage(AnyValue? body)
    {
        if (body is null)
        {
            return null;
        }
        return body.ValueCase == AnyValue.ValueOneofCase.StringValue
            ? body.StringValue
            : JsonSerializer.Serialize(AnyValueToObject(body));
    }

    private static object AnyValueToObject(AnyValue value) => value.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
        AnyValue.ValueOneofCase.IntValue => value.IntValue,
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
        AnyValue.ValueOneofCase.BytesValue => Convert.ToHexString(value.BytesValue.Span).ToLowerInvariant(),
        AnyValue.ValueOneofCase.ArrayValue => value.ArrayValue.Values.Select(AnyValueToObject).ToList(),
        AnyValue.ValueOneofCase.KvlistValue => value.KvlistValue.Values.ToDictionary(kv => kv.Key, kv => AnyValueToObject(kv.Value)),
        _ => string.Empty
    };
}
