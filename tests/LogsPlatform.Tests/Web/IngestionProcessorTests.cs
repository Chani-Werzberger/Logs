using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class IngestionProcessorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> CreateFixtureAsync(LogsPlatformDbContext context, string appName)
    {
        var application = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        var environment = new AppEnvironment { Name = "Production", IsProduction = true };
        application.Environments.Add(environment);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return (application.Id, environment.Id);
    }

    private static IngestionProcessor CreateProcessor(LogsPlatformDbContext context) => new(
        new AppEnvironmentRepository(context), new AppVersionRepository(context),
        new CustomerRepository(context), new AppUserRepository(context),
        new ExceptionGroupRepository(context),
        new HierarchyResolver(new AppModuleRepository(context), new ScreenServiceRepository(context), new ProcessNodeRepository(context), new OperationRepository(context)));

    private static IngestEventRequest ValidRequest(string environment) => new(
        EventKey: null, Timestamp: DateTime.UtcNow, Severity: "Error", Environment: environment, Version: null,
        Hierarchy: null, CorrelationId: null, TraceId: null, SpanId: null, ParentSpanId: null, DurationMs: null,
        CustomerId: null, UserId: null, Message: "something failed", MessageTemplate: null, Exception: null, Metadata: null);

    [Fact]
    public async Task ProcessAsync_ValidEvent_ReturnsEventWithNoWarnings()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorValidTestApp");
        var processor = CreateProcessor(context);

        var result = await processor.ProcessAsync(appId, ValidRequest("Production"));

        Assert.Null(result.RejectReason);
        Assert.NotNull(result.Event);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ProcessAsync_MissingTimestamp_Rejects()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorNoTimestampTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { Timestamp = null };

        var result = await processor.ProcessAsync(appId, request);

        Assert.NotNull(result.RejectReason);
        Assert.Null(result.Event);
    }

    [Fact]
    public async Task ProcessAsync_InvalidSeverity_Rejects()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorBadSeverityTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { Severity = "Critical" };

        var result = await processor.ProcessAsync(appId, request);

        Assert.NotNull(result.RejectReason);
        Assert.Null(result.Event);
    }

    [Fact]
    public async Task ProcessAsync_MissingMessage_Rejects()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorNoMessageTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { Message = null };

        var result = await processor.ProcessAsync(appId, request);

        Assert.NotNull(result.RejectReason);
        Assert.Null(result.Event);
    }

    [Fact]
    public async Task ProcessAsync_UnknownEnvironment_Rejects()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorUnknownEnvTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Staging");

        var result = await processor.ProcessAsync(appId, request);

        Assert.NotNull(result.RejectReason);
        Assert.Null(result.Event);
    }

    [Fact]
    public async Task ProcessAsync_UnknownVersion_WarnsButAccepts()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorUnknownVersionTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { Version = "9.9.9" };

        var result = await processor.ProcessAsync(appId, request);

        Assert.Null(result.RejectReason);
        Assert.NotNull(result.Event);
        Assert.Null(result.Event!.VersionId);
        Assert.Contains(result.Warnings, w => w.Field == "version");
    }

    [Fact]
    public async Task ProcessAsync_UnknownCustomerId_WarnsButAccepts()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorUnknownCustomerTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { CustomerId = "cust-does-not-exist" };

        var result = await processor.ProcessAsync(appId, request);

        Assert.Null(result.RejectReason);
        Assert.Null(result.Event!.CustomerId);
        Assert.Contains(result.Warnings, w => w.Field == "customerId");
    }

    [Fact]
    public async Task ProcessAsync_DeactivatedCustomer_StillResolvesWithNoWarning()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorDeactivatedCustomerTestApp");
        var customerRepository = new CustomerRepository(context);
        var customer = await customerRepository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-retired", Name = "Retired Customer" });
        await customerRepository.DeactivateAsync(customer.Id);
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { CustomerId = "cust-retired" };

        var result = await processor.ProcessAsync(appId, request);

        Assert.Null(result.RejectReason);
        Assert.NotNull(result.Event);
        Assert.Equal(customer.Id, result.Event!.CustomerId);
        Assert.DoesNotContain(result.Warnings, w => w.Field == "customerId");
    }

    [Fact]
    public async Task ProcessAsync_UnresolvableHierarchy_WarnsButAccepts()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorUnresolvableHierarchyTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { Hierarchy = new IngestHierarchyRequest("TypoModule", null, null, null) };

        var result = await processor.ProcessAsync(appId, request);

        Assert.Null(result.RejectReason);
        Assert.Null(result.Event!.ModuleId);
        Assert.Contains(result.Warnings, w => w.Field == "module");
    }

    [Fact]
    public async Task ProcessAsync_WithException_ComputesFingerprintAndCreatesExceptionGroup()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorExceptionTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { Exception = new IngestExceptionRequest("System.TimeoutException", "at Foo.Bar()") };

        var result = await processor.ProcessAsync(appId, request);

        Assert.Null(result.RejectReason);
        Assert.NotNull(result.Event!.ExceptionGroupId);
        Assert.Equal(1, await context.ExceptionGroups.CountAsync());
    }

    [Fact]
    public async Task ProcessAsync_ExceptionWithNullStackTrace_WarnsButAccepts()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _) = await CreateFixtureAsync(context, "ProcessorExceptionNullStackTraceTestApp");
        var processor = CreateProcessor(context);
        var request = ValidRequest("Production") with { Exception = new IngestExceptionRequest("System.TimeoutException", null) };

        var result = await processor.ProcessAsync(appId, request);

        Assert.Null(result.RejectReason);
        Assert.NotNull(result.Event);
        Assert.Null(result.Event!.ExceptionGroupId);
        Assert.Null(result.Event!.StackTrace);
        Assert.Contains(result.Warnings, w => w.Field == "exception");
        Assert.Equal(0, await context.ExceptionGroups.CountAsync());
    }
}
