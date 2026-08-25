using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;

namespace LogsPlatform.Tests.Web;

public class OtlpGeneratedTypesTests
{
    [Fact]
    public void ExportLogsServiceRequest_IsConstructibleAndRoundTripsThroughBytes()
    {
        var request = new ExportLogsServiceRequest();
        var resourceLogs = new ResourceLogs();
        var scopeLogs = new ScopeLogs();
        scopeLogs.LogRecords.Add(new LogRecord { SeverityNumber = SeverityNumber.Error });
        resourceLogs.ScopeLogs.Add(scopeLogs);
        request.ResourceLogs.Add(resourceLogs);

        var bytes = request.ToByteArray();
        var roundTripped = ExportLogsServiceRequest.Parser.ParseFrom(bytes);

        Assert.Single(roundTripped.ResourceLogs);
        Assert.Equal(SeverityNumber.Error, roundTripped.ResourceLogs[0].ScopeLogs[0].LogRecords[0].SeverityNumber);
    }
}
