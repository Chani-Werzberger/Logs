using System.Text.Json;
using Google.Protobuf;
using LogsPlatform.Web.Services;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using Xunit;

namespace LogsPlatform.Tests.Web;

public class OtlpLogMapperTests
{
    private static KeyValue Attr(string key, string value) =>
        new() { Key = key, Value = new AnyValue { StringValue = value } };

    [Fact]
    public void Map_TimeUnixNano_ConvertsToUtcDateTime()
    {
        var expected = new DateTime(2026, 8, 24, 12, 30, 0, DateTimeKind.Utc);
        var nanosSinceEpoch = (ulong)(expected - DateTime.UnixEpoch).Ticks * 100;
        var record = new LogRecord { TimeUnixNano = nanosSinceEpoch, Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Equal(expected, result.Timestamp);
    }

    [Fact]
    public void Map_ZeroTimeUnixNano_TimestampIsNull()
    {
        var record = new LogRecord { TimeUnixNano = 0, Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Null(result.Timestamp);
    }

    [Theory]
    [InlineData(SeverityNumber.Trace, "Trace")]
    [InlineData(SeverityNumber.Debug, "Debug")]
    [InlineData(SeverityNumber.Info, "Info")]
    [InlineData(SeverityNumber.Warn, "Warn")]
    [InlineData(SeverityNumber.Error, "Error")]
    [InlineData(SeverityNumber.Fatal, "Fatal")]
    public void Map_SeverityNumber_MapsToExpectedName(SeverityNumber severity, string expected)
    {
        var record = new LogRecord { SeverityNumber = severity, Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Equal(expected, result.Severity);
    }

    [Fact]
    public void Map_StringBody_UsesStringDirectly()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "something failed" } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Equal("something failed", result.Message);
    }

    [Fact]
    public void Map_NonStringBody_SerializesToJson()
    {
        var record = new LogRecord { Body = new AnyValue { IntValue = 42 } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Equal("42", result.Message);
    }

    [Fact]
    public void Map_TraceIdAndSpanId_ConvertToLowercaseHex()
    {
        var record = new LogRecord
        {
            Body = new AnyValue { StringValue = "m" },
            TraceId = ByteString.CopyFrom(new byte[] { 0xAB, 0xCD, 0x01 }),
            SpanId = ByteString.CopyFrom(new byte[] { 0xEF, 0x02 })
        };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Equal("abcd01", result.TraceId);
        Assert.Equal("ef02", result.SpanId);
    }

    [Fact]
    public void Map_EmptyTraceId_IsNull()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Null(result.TraceId);
        Assert.Null(result.SpanId);
    }

    [Fact]
    public void Map_DeploymentEnvironmentResourceAttribute_MapsToEnvironment()
    {
        var resource = new Resource();
        resource.Attributes.Add(Attr("deployment.environment", "Production"));
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, resource);

        Assert.Equal("Production", result.Environment);
    }

    [Fact]
    public void Map_ServiceNameResourceAttribute_DoesNotAffectMapping()
    {
        var resource = new Resource();
        resource.Attributes.Add(Attr("service.name", "SomeService"));
        resource.Attributes.Add(Attr("deployment.environment", "Production"));
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, resource);

        Assert.Equal("Production", result.Environment);
        Assert.True(result.Metadata is null || !result.Metadata.ContainsKey("service.name"));
    }

    [Fact]
    public void Map_ExceptionAttributes_MapToExceptionField()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };
        record.Attributes.Add(Attr("exception.type", "System.TimeoutException"));
        record.Attributes.Add(Attr("exception.stacktrace", "at Foo.Bar()"));

        var result = OtlpLogMapper.Map(record, null);

        Assert.NotNull(result.Exception);
        Assert.Equal("System.TimeoutException", result.Exception!.Type);
        Assert.Equal("at Foo.Bar()", result.Exception.StackTrace);
    }

    [Fact]
    public void Map_LogsPlatformHierarchyAttributes_MapToHierarchy()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };
        record.Attributes.Add(Attr("logsplatform.module", "Payments"));
        record.Attributes.Add(Attr("logsplatform.screen_service", "Checkout"));
        record.Attributes.Add(Attr("logsplatform.process", "ChargeCard"));
        record.Attributes.Add(Attr("logsplatform.operation", "Authorize"));

        var result = OtlpLogMapper.Map(record, null);

        Assert.NotNull(result.Hierarchy);
        Assert.Equal("Payments", result.Hierarchy!.Module);
        Assert.Equal("Checkout", result.Hierarchy.ScreenService);
        Assert.Equal("ChargeCard", result.Hierarchy.Process);
        Assert.Equal("Authorize", result.Hierarchy.Operation);
    }

    [Fact]
    public void Map_LogsPlatformCustomerAndUserAttributes_MapDirectly()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };
        record.Attributes.Add(Attr("logsplatform.customer_id", "cust-1"));
        record.Attributes.Add(Attr("logsplatform.user_id", "user-1"));

        var result = OtlpLogMapper.Map(record, null);

        Assert.Equal("cust-1", result.CustomerId);
        Assert.Equal("user-1", result.UserId);
    }

    [Fact]
    public void Map_UnmappedAttributes_GoToMetadata()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };
        record.Attributes.Add(Attr("http.method", "POST"));

        var result = OtlpLogMapper.Map(record, null);

        Assert.NotNull(result.Metadata);
        Assert.Equal("POST", result.Metadata!["http.method"]);
    }

    [Fact]
    public void Map_NoAttributes_MetadataIsNull()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Null(result.Metadata);
    }

    [Fact]
    public void Map_NullResource_EnvironmentIsNull()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Null(result.Environment);
    }
}
