using Serilog.Core;
using Serilog.Events;

namespace LogsPlatform.Client.Serilog;

public sealed class LogsPlatformSink : ILogEventSink, IDisposable
{
    private static readonly Dictionary<LogEventLevel, string> SeverityMap = new()
    {
        [LogEventLevel.Verbose] = "Trace",
        [LogEventLevel.Debug] = "Debug",
        [LogEventLevel.Information] = "Info",
        [LogEventLevel.Warning] = "Warn",
        [LogEventLevel.Error] = "Error",
        [LogEventLevel.Fatal] = "Fatal",
    };

    private readonly ILogsPlatformClient _client;
    private readonly string _environment;

    public LogsPlatformSink(ILogsPlatformClient client, string environment)
    {
        _client = client;
        _environment = environment;
    }

    public void Emit(LogEvent logEvent)
    {
        var payload = new EventPayload(
            EventKey: null,
            Timestamp: logEvent.Timestamp.UtcDateTime,
            Severity: SeverityMap[logEvent.Level],
            Environment: _environment,
            Version: null,
            Hierarchy: BuildHierarchy(logEvent),
            CorrelationId: GetProperty(logEvent, "CorrelationId"),
            TraceId: null,
            SpanId: null,
            ParentSpanId: null,
            DurationMs: null,
            CustomerId: GetProperty(logEvent, "CustomerId"),
            UserId: null,
            Message: logEvent.RenderMessage(),
            MessageTemplate: logEvent.MessageTemplate.Text,
            Exception: BuildException(logEvent),
            Metadata: null);

        _ = _client.SendEventAsync(payload);
    }

    public void Dispose()
    {
        _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static string? GetProperty(LogEvent logEvent, string name)
    {
        if (logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar)
        {
            return scalar.Value?.ToString();
        }
        return null;
    }

    private static IngestHierarchyPayload? BuildHierarchy(LogEvent logEvent)
    {
        var module = GetProperty(logEvent, "Module");
        var screenService = GetProperty(logEvent, "ScreenService");
        var process = GetProperty(logEvent, "Process");
        var operation = GetProperty(logEvent, "Operation");

        if (module is null && screenService is null && process is null && operation is null)
        {
            return null;
        }

        return new IngestHierarchyPayload(module, screenService, process, operation);
    }

    private static IngestExceptionPayload? BuildException(LogEvent logEvent)
    {
        if (logEvent.Exception is null)
        {
            return null;
        }

        return new IngestExceptionPayload(
            logEvent.Exception.GetType().FullName ?? logEvent.Exception.GetType().Name,
            logEvent.Exception.StackTrace);
    }
}
