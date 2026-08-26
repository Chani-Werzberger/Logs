using LogsPlatform.Web.Services.Analysis;
using Xunit;

namespace LogsPlatform.Tests.Web;

public class AnalysisEngineHealthStatusTests
{
    [Fact]
    public void LastTickCompletedAt_BeforeAnyRecord_IsNull()
    {
        var status = new AnalysisEngineHealthStatus();

        Assert.Null(status.LastTickCompletedAt);
    }

    [Fact]
    public void RecordTickCompleted_ThenLastTickCompletedAt_ReturnsRecordedValue()
    {
        var status = new AnalysisEngineHealthStatus();
        var timestamp = new DateTime(2026, 8, 26, 14, 5, 0, DateTimeKind.Utc);

        status.RecordTickCompleted(timestamp);

        Assert.Equal(timestamp, status.LastTickCompletedAt);
    }

    [Fact]
    public void RecordTickCompleted_CalledTwice_ReturnsMostRecentValue()
    {
        var status = new AnalysisEngineHealthStatus();
        var older = new DateTime(2026, 8, 26, 14, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 8, 26, 14, 5, 0, DateTimeKind.Utc);

        status.RecordTickCompleted(older);
        status.RecordTickCompleted(newer);

        Assert.Equal(newer, status.LastTickCompletedAt);
    }
}
