using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class DownstreamFailureCorrelatorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId, int OperationId1, int OperationId2)> SeedAppEnvAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        var module = new AppModule { ApplicationId = app.Id, Name = "Billing" };
        context.AppEnvironments.Add(env);
        context.Modules.Add(module);
        await context.SaveChangesAsync();
        var screenService = new ScreenService { ModuleId = module.Id, Name = "Invoicing", Type = ScreenServiceType.Service };
        context.ScreenServices.Add(screenService);
        await context.SaveChangesAsync();
        var process = new ProcessNode { ScreenServiceId = screenService.Id, Name = "ChargeCard" };
        context.Processes.Add(process);
        await context.SaveChangesAsync();
        var operation1 = new Operation { ProcessId = process.Id, Name = "Authorize" };
        var operation2 = new Operation { ProcessId = process.Id, Name = "Capture" };
        context.Operations.AddRange(operation1, operation2);
        await context.SaveChangesAsync();
        return (app.Id, env.Id, operation1.Id, operation2.Id);
    }

    [Fact]
    public async Task RunAsync_LaterErrorOnDifferentOperationSameCorrelationId_AddsHypothesis()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, operationId1, operationId2) = await SeedAppEnvAsync(context, "DownstreamCorrelatorTestApp");

        var triggerTime = DateTime.UtcNow;
        var triggerEvent = new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = operationId1, CorrelationId = "order-1",
            Timestamp = triggerTime, Severity = 17, Message = "initial failure"
        };
        var downstreamEvent = new Event
        {
            ApplicationId = appId, EnvironmentId = envId, OperationId = operationId2, CorrelationId = "order-1",
            Timestamp = triggerTime.AddSeconds(5), Severity = 17, Message = "downstream failure"
        };
        context.Events.AddRange(triggerEvent, downstreamEvent);
        await context.SaveChangesAsync();

        var finding = new Finding
        {
            ApplicationId = appId, EnvironmentId = envId, Type = FindingType.ErrorSpike,
            ScopeType = AnalysisScopeType.Operation, ScopeId = 1, Title = "test",
            DetectedAt = triggerTime, Severity = FindingSeverity.High, ConfidenceLevel = ConfidenceLevel.High, Status = FindingStatus.New
        };
        context.Findings.Add(finding);
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        var correlator = new DownstreamFailureCorrelator(findingRepository, context);

        await correlator.RunAsync(finding, triggerEvent.CorrelationId!, triggerEvent.OperationId!.Value, triggerTime);

        var details = await findingRepository.GetByIdAsync(finding.Id);
        Assert.Contains(details!.Statements, s => s.Kind == FindingStatementKind.Hypothesis);
        Assert.Contains(details.Evidence, e => e.EvidenceType == EvidenceType.Event);
    }
}
