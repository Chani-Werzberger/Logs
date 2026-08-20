using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class HierarchyResolverTests
{
    private static async Task<(int ApplicationId, int ModuleId, int ScreenServiceId, int ProcessId, int OperationId)> CreateFullFixtureAsync(LogsPlatformDbContext context)
    {
        var application = new Application { Name = $"HierarchyResolverTestApp-{Guid.NewGuid()}", CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();

        var moduleRepo = new AppModuleRepository(context);
        var screenServiceRepo = new ScreenServiceRepository(context);
        var processRepo = new ProcessNodeRepository(context);
        var operationRepo = new OperationRepository(context);

        var module = await moduleRepo.AddAsync(new AppModule { ApplicationId = application.Id, Name = "Payments" });
        var screenService = await screenServiceRepo.AddAsync(new ScreenService { ModuleId = module.Id, Name = "PaymentGateway", Type = ScreenServiceType.Service });
        var process = await processRepo.AddAsync(new ProcessNode { ScreenServiceId = screenService.Id, Name = "ProcessPayment" });
        var operation = await operationRepo.AddAsync(new Operation { ProcessId = process.Id, Name = "AuthorizeCard" });

        return (application.Id, module.Id, screenService.Id, process.Id, operation.Id);
    }

    private static HierarchyResolver CreateResolver(LogsPlatformDbContext context) => new(
        new AppModuleRepository(context), new ScreenServiceRepository(context),
        new ProcessNodeRepository(context), new OperationRepository(context));

    [Fact]
    public async Task ResolveAsync_AllLayersResolve_ReturnsAllIds()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, moduleId, screenServiceId, processId, operationId) = await CreateFullFixtureAsync(context);
        var resolver = CreateResolver(context);

        var result = await resolver.ResolveAsync(appId, "Payments", "PaymentGateway", "ProcessPayment", "AuthorizeCard");

        Assert.Equal(moduleId, result.ModuleId);
        Assert.Equal(screenServiceId, result.ScreenServiceId);
        Assert.Equal(processId, result.ProcessId);
        Assert.Equal(operationId, result.OperationId);
        Assert.Null(result.WarningField);
    }

    [Fact]
    public async Task ResolveAsync_ModuleNotFound_ReturnsAllNullWithModuleWarning()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _, _, _, _) = await CreateFullFixtureAsync(context);
        var resolver = CreateResolver(context);

        var result = await resolver.ResolveAsync(appId, "TypoModule", "PaymentGateway", "ProcessPayment", "AuthorizeCard");

        Assert.Null(result.ModuleId);
        Assert.Null(result.ScreenServiceId);
        Assert.Null(result.ProcessId);
        Assert.Null(result.OperationId);
        Assert.Equal("module", result.WarningField);
    }

    [Fact]
    public async Task ResolveAsync_ScreenServiceNotFound_ReturnsModuleIdOnlyWithWarning()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, moduleId, _, _, _) = await CreateFullFixtureAsync(context);
        var resolver = CreateResolver(context);

        var result = await resolver.ResolveAsync(appId, "Payments", "TypoScreenService", "ProcessPayment", "AuthorizeCard");

        Assert.Equal(moduleId, result.ModuleId);
        Assert.Null(result.ScreenServiceId);
        Assert.Null(result.ProcessId);
        Assert.Null(result.OperationId);
        Assert.Equal("screenService", result.WarningField);
    }

    [Fact]
    public async Task ResolveAsync_NoHierarchyProvided_ReturnsAllNullNoWarning()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _, _, _, _) = await CreateFullFixtureAsync(context);
        var resolver = CreateResolver(context);

        var result = await resolver.ResolveAsync(appId, null, null, null, null);

        Assert.Null(result.ModuleId);
        Assert.Null(result.WarningField);
    }

    [Fact]
    public async Task ResolveAsync_PartialPathProvided_StopsAtLastProvidedLayerNoWarning()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, moduleId, screenServiceId, _, _) = await CreateFullFixtureAsync(context);
        var resolver = CreateResolver(context);

        var result = await resolver.ResolveAsync(appId, "Payments", "PaymentGateway", null, null);

        Assert.Equal(moduleId, result.ModuleId);
        Assert.Equal(screenServiceId, result.ScreenServiceId);
        Assert.Null(result.ProcessId);
        Assert.Null(result.WarningField);
    }
}
