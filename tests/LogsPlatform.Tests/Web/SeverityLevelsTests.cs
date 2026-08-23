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
}
