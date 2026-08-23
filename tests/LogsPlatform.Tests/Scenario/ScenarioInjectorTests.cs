using LogsPlatform.SyntheticDataGenerator;
using LogsPlatform.SyntheticDataGenerator.ScenarioInjectors;
using Xunit;

namespace LogsPlatform.Tests.Scenario;

public class ScenarioInjectorTests
{
    [Fact]
    public void ErrorSpikeInjector_ProducesEventsInCurrentHourOnChargePayment()
    {
        var referenceTime = DateTime.UtcNow;
        var events = ErrorSpikeInjector.Inject(referenceTime);

        Assert.Equal(ScenarioConstants.ErrorSpikeEventCount, events.Count);
        Assert.All(events, e => Assert.Equal(ScenarioConstants.ChargePaymentOperation, e.Operation));
        Assert.All(events, e => Assert.Equal("Error", e.Severity));
        var currentHourStart = referenceTime.Date.AddHours(referenceTime.Hour);
        Assert.All(events, e => Assert.InRange(e.Timestamp, currentHourStart, currentHourStart.AddHours(1)));
    }

    [Fact]
    public void PerformanceDegradationInjector_ProducesElevatedDurationOnMatchAvailability()
    {
        var events = PerformanceDegradationInjector.Inject(eventCount: 20, DateTime.UtcNow);

        Assert.Equal(20, events.Count);
        Assert.All(events, e => Assert.Equal(ScenarioConstants.MatchAvailabilityOperation, e.Operation));
        Assert.All(events, e => Assert.Equal(ScenarioConstants.PerformanceDegradationDurationMs, e.DurationMs));
    }

    [Fact]
    public void NewExceptionInjector_ProducesTriggerAndDownstreamEventsSharingCorrelationId()
    {
        var beforeCall = DateTime.UtcNow;
        var events = NewExceptionInjector.Inject(beforeCall);

        Assert.Equal(2, events.Count);

        var trigger = events.Single(e => e.Operation == ScenarioConstants.ReserveStockOperation);
        Assert.NotNull(trigger.ExceptionType);
        Assert.NotNull(trigger.CorrelationId);

        var downstream = events.Single(e => e.Operation == ScenarioConstants.ValidateCartOperation);
        Assert.Equal(trigger.CorrelationId, downstream.CorrelationId);
        Assert.Equal("Error", downstream.Severity);
        Assert.True(downstream.Timestamp > trigger.Timestamp);

        var fiveMinutesAgo = beforeCall.AddMinutes(-5);
        Assert.True(trigger.Timestamp >= fiveMinutesAgo, "Trigger event must fall inside NewExceptionDetector's 5-minute window.");
    }

    [Fact]
    public void DeploymentAnomalyInjector_InjectEvents_ProducesEventsInCurrentHourOnAggregateJobs()
    {
        var events = DeploymentAnomalyInjector.InjectEvents(DateTime.UtcNow);

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

    [Fact]
    public void CustomerAnomalyInjector_Inject_GivesOneCustomerFarMoreEventsThanPeers()
    {
        var customerIds = Enumerable.Range(0, ScenarioConstants.CustomerAnomalyPeerCount + 1).Select(i => $"cust-{i}").ToList();
        var beforeCall = DateTime.UtcNow;

        var events = CustomerAnomalyInjector.Inject(customerIds, beforeCall);

        var byCustomer = events.GroupBy(e => e.CustomerId).ToDictionary(g => g.Key!, g => g.Count());
        Assert.Equal(ScenarioConstants.CustomerAnomalyPeerCount + 1, byCustomer.Count);

        var outlierId = customerIds[^1];
        Assert.Equal(ScenarioConstants.CustomerAnomalyOutlierConfirmOrderCount, byCustomer[outlierId]);

        foreach (var peerId in customerIds.Take(ScenarioConstants.CustomerAnomalyPeerCount))
        {
            Assert.Equal(ScenarioConstants.CustomerAnomalyPeerConfirmOrderCount, byCustomer[peerId]);
        }

        Assert.All(events, e => Assert.Equal(ScenarioConstants.ConfirmOrderOperation, e.Operation));

        var oneDayAgo = beforeCall.AddHours(-24);
        var outlierEvents = events.Where(e => e.CustomerId == outlierId).ToList();
        Assert.True(outlierEvents.Select(e => e.Timestamp.Hour).Distinct().Count() > 1,
            "Outlier's events must be spread across multiple hours, not concentrated in one.");
        Assert.All(outlierEvents, e => Assert.InRange(e.Timestamp, oneDayAgo, DateTime.UtcNow));
    }
}
