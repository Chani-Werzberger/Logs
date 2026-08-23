using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class NewExceptionDetectorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> SeedAppEnvAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();
        return (app.Id, env.Id);
    }

    [Fact]
    public async Task RunAsync_RecentlyFirstSeenGroup_CreatesNewExceptionFinding()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "NewExceptionDetectorTestApp");

        var group = new ExceptionGroup
        {
            ApplicationId = appId, Fingerprint = "fp-new", ExceptionType = "System.TimeoutException",
            MessageTemplate = "timed out", RepresentativeStackTrace = "at Foo.Bar()",
            FirstSeenAt = DateTime.UtcNow.AddMinutes(-1), LastSeenAt = DateTime.UtcNow, OccurrenceCount = 1
        };
        context.ExceptionGroups.Add(group);
        await context.SaveChangesAsync();

        context.Events.Add(new Event
        {
            ApplicationId = appId, EnvironmentId = envId, Timestamp = DateTime.UtcNow,
            Severity = 17, Message = "boom", ExceptionGroupId = group.Id
        });
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var downstreamCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var detector = new NewExceptionDetector(context, writer, downstreamCorrelator);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.NewException);

        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.High, finding!.Severity);
        Assert.Equal(ConfidenceLevel.High, finding.ConfidenceLevel);
    }

    [Fact]
    public async Task RunAsync_OldExistingGroup_NoFindingCreated()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "NewExceptionDetectorOldGroupTestApp");

        var group = new ExceptionGroup
        {
            ApplicationId = appId, Fingerprint = "fp-old", ExceptionType = "System.Exception",
            MessageTemplate = "old", RepresentativeStackTrace = "at Foo.Bar()",
            FirstSeenAt = DateTime.UtcNow.AddDays(-10), LastSeenAt = DateTime.UtcNow, OccurrenceCount = 5
        };
        context.ExceptionGroups.Add(group);
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var downstreamCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var detector = new NewExceptionDetector(context, writer, downstreamCorrelator);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var findingCount = await verifyContext.Findings.CountAsync(f => f.ApplicationId == appId);

        Assert.Equal(0, findingCount);
    }

    [Fact]
    public async Task RunAsync_TriggerEventHasCorrelationIdWithDownstreamFailure_AddsHypothesisFromCorrelator()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "NewExceptionDownstreamTestApp");

        var module = new AppModule { ApplicationId = appId, Name = "Billing" };
        context.Modules.Add(module);
        await context.SaveChangesAsync();
        var screenService = new ScreenService { ModuleId = module.Id, Name = "Invoicing", Type = ScreenServiceType.Service };
        context.ScreenServices.Add(screenService);
        await context.SaveChangesAsync();
        var process = new ProcessNode { ScreenServiceId = screenService.Id, Name = "ChargeCard" };
        context.Processes.Add(process);
        await context.SaveChangesAsync();
        var triggerOperation = new Operation { ProcessId = process.Id, Name = "Authorize" };
        var downstreamOperation = new Operation { ProcessId = process.Id, Name = "Capture" };
        context.Operations.AddRange(triggerOperation, downstreamOperation);
        await context.SaveChangesAsync();

        var group = new ExceptionGroup
        {
            ApplicationId = appId, Fingerprint = "fp-downstream", ExceptionType = "System.TimeoutException",
            MessageTemplate = "timed out", RepresentativeStackTrace = "at Foo.Bar()",
            FirstSeenAt = DateTime.UtcNow.AddMinutes(-1), LastSeenAt = DateTime.UtcNow, OccurrenceCount = 1
        };
        context.ExceptionGroups.Add(group);
        await context.SaveChangesAsync();

        var triggerTime = DateTime.UtcNow;
        context.Events.Add(new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = triggerOperation.Id, CorrelationId = "order-99",
            Timestamp = triggerTime, Severity = 17, Message = "boom", ExceptionGroupId = group.Id
        });
        context.Events.Add(new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = downstreamOperation.Id, CorrelationId = "order-99",
            Timestamp = triggerTime.AddSeconds(2), Severity = 17, Message = "downstream failure"
        });
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var downstreamCorrelator = new DownstreamFailureCorrelator(findingRepository, context);
        var detector = new NewExceptionDetector(context, writer, downstreamCorrelator);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.NewException);
        Assert.NotNull(finding);

        var findingRepositoryForVerify = new FindingRepository(verifyContext);
        var details = await findingRepositoryForVerify.GetByIdAsync(finding!.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
    }
}
