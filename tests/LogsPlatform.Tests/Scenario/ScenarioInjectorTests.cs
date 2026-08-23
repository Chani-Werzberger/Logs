using LogsPlatform.SyntheticDataGenerator;
using LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

public class ScenarioInjectorTests
{
    [Fact]
    public void ErrorSpikeInjector_ProducesEventsInCurrentHourOnChargePayment()
    {
        var events = ErrorSpikeInjector.Inject();

        Assert.Equal(ScenarioConstants.ErrorSpikeEventCount, events.Count);
        Assert.All(events, e => Assert.Equal(ScenarioConstants.ChargePaymentOperation, e.Operation));
        Assert.All(events, e => Assert.Equal("Error", e.Severity));
        var currentHourStart = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        Assert.All(events, e => Assert.InRange(e.Timestamp, currentHourStart, currentHourStart.AddHours(1)));
    }

    [Fact]
    public void PerformanceDegradationInjector_ProducesElevatedDurationOnMatchAvailability()
    {
        var events = PerformanceDegradationInjector.Inject(eventCount: 20);

        Assert.Equal(20, events.Count);
        Assert.All(events, e => Assert.Equal(ScenarioConstants.MatchAvailabilityOperation, e.Operation));
        Assert.All(events, e => Assert.Equal(ScenarioConstants.PerformanceDegradationDurationMs, e.DurationMs));
    }
}
