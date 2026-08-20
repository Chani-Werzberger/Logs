using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace LogsPlatform.Client.Serilog;

public static class LogsPlatformSinkExtensions
{
    /// <summary>
    /// Adds a LogsPlatform sink to the logger pipeline. This also enables Serilog's
    /// <c>Enrich.FromLogContext()</c> on the returned <see cref="LoggerConfiguration"/>,
    /// since the sink reads Module/ScreenService/Process/Operation/CorrelationId/CustomerId
    /// from ambient <see cref="Serilog.Context.LogContext"/> properties, which Serilog only
    /// populates when that enrichment is active. This affects every sink configured on the
    /// same logger, not just this one.
    /// </summary>
    public static LoggerConfiguration LogsPlatform(
        this LoggerSinkConfiguration sinkConfiguration,
        string apiKey,
        string baseUrl,
        string environment,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(environment);

        var client = new LogsPlatformClient(baseUrl, apiKey, httpClient);
        var sink = new LogsPlatformSink(client, environment);

        // LogsPlatformSink reads CorrelationId/CustomerId off LogEvent.Properties, which are
        // only populated from ambient Serilog.Context.LogContext.PushProperty calls when
        // FromLogContext() enrichment is active. Enable it here so callers get correlation
        // propagation for free, without needing to remember to configure it themselves.
        return sinkConfiguration.Sink(sink, restrictedToMinimumLevel).Enrich.FromLogContext();
    }
}
