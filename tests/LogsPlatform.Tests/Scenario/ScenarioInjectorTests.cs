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

    [Fact]
    public void NewExceptionInjector_ProducesTriggerAndDownstreamEventsSharingCorrelationId()
    {
        var events = NewExceptionInjector.Inject();

        Assert.Equal(2, events.Count);

        var trigger = events.Single(e => e.Operation == ScenarioConstants.ReserveStockOperation);
        Assert.NotNull(trigger.ExceptionType);
        Assert.NotNull(trigger.CorrelationId);

        var downstream = events.Single(e => e.Operation == ScenarioConstants.ValidateCartOperation);
        Assert.Equal(trigger.CorrelationId, downstream.CorrelationId);
        Assert.Equal("Error", downstream.Severity);
        Assert.True(downstream.Timestamp > trigger.Timestamp);

        var fiveMinutesAgo = DateTime.UtcNow.AddMinutes(-5);
        Assert.True(trigger.Timestamp >= fiveMinutesAgo, "Trigger event must fall inside NewExceptionDetector's 5-minute window.");
    }

    [Fact]
    public void DeploymentAnomalyInjector_InjectEvents_ProducesEventsInCurrentHourOnAggregateJobs()
    {
        var events = DeploymentAnomalyInjector.InjectEvents();

        Assert.Equal(ScenarioConstants.DeploymentAnomalyEventCount, events.Count);
        Assert.All(events, e => Assert.Equal(ScenarioConstants.AggregateJobsOperation, e.Operation));
        Assert.All(events, e => Assert.Equal("Error", e.Severity));
    }

    [Fact]
    public void MissingActivityInjector_Inject_ProducesNoEvents()
    {
        var events = MissingActivityInjector.Inject();

        Assert.Empty(events);
    }
}
