using LogsPlatform.Web.Services;

namespace LogsPlatform.Tests.Web;

public class SeverityLevelsTests
{
    [Theory]
    [InlineData("Trace", 1)]
    [InlineData("Debug", 5)]
    [InlineData("Info", 9)]
    [InlineData("Warn", 13)]
    [InlineData("Error", 17)]
    [InlineData("Fatal", 21)]
    public void ByName_KnownSeverity_ReturnsExpectedValue(string name, int expected)
    {
        Assert.Equal(expected, SeverityLevels.ByName[name]);
    }

    [Fact]
    public void ByValue_IsExactReverseOfByName()
    {
        foreach (var (name, value) in SeverityLevels.ByName)
        {
            Assert.Equal(name, SeverityLevels.ByValue[value]);
        }
        Assert.Equal(SeverityLevels.ByName.Count, SeverityLevels.ByValue.Count);
    }

    [Theory]
    [InlineData(1, "Trace")]
    [InlineData(2, "Trace")]
    [InlineData(4, "Trace")]
    [InlineData(5, "Debug")]
    [InlineData(8, "Debug")]
    [InlineData(9, "Info")]
    [InlineData(12, "Info")]
    [InlineData(13, "Warn")]
    [InlineData(16, "Warn")]
    [InlineData(17, "Error")]
    [InlineData(20, "Error")]
    [InlineData(21, "Fatal")]
    [InlineData(24, "Fatal")]
    public void FromOtelSeverityNumber_ValueInBand_ReturnsBandName(int severityNumber, string expected)
    {
        Assert.Equal(expected, SeverityLevels.FromOtelSeverityNumber(severityNumber));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(-1)]
    public void FromOtelSeverityNumber_OutOfRange_ReturnsNull(int severityNumber)
    {
        Assert.Null(SeverityLevels.FromOtelSeverityNumber(severityNumber));
    }
}
