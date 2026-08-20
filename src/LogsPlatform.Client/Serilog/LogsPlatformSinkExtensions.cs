using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace LogsPlatform.Client.Serilog;

public static class LogsPlatformSinkExtensions
{
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
