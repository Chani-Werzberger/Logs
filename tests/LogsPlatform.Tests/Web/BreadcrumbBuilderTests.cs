using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class BreadcrumbBuilderTests
{
    private static async Task<(int appId, int moduleId, int screenServiceId, int processId)> CreateFullChainAsync(LogsPlatformDbContext context)
    {
        var application = new Application { Name = "BreadcrumbTestApp", CreatedAt = DateTime.UtcNow };
        var module = new AppModule { Name = "Payments" };
        var screenService = new ScreenService { Name = "PaymentGateway", Type = ScreenServiceType.Service };
        var process = new ProcessNode { Name = "ChargeCard" };
        screenService.Processes.Add(process);
        module.ScreenServices.Add(screenService);
        application.Modules.Add(module);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return (application.Id, module.Id, screenService.Id, process.Id);
    }

    private static BreadcrumbBuilder CreateBuilder(LogsPlatformDbContext context) =>
        new(
            new ApplicationRepository(context),
            new AppModuleRepository(context),
            new ScreenServiceRepository(context),
            new ProcessNodeRepository(context));

    [Fact]
    public async Task BuildAsync_WithOnlyAppId_ReturnsSingleSegmentPointingToModulesPage()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _, _, _) = await CreateFullChainAsync(context);
        var builder = CreateBuilder(context);

        var segments = await builder.BuildAsync(appId);

        Assert.Single(segments);
        Assert.Equal("BreadcrumbTestApp", segments[0].Label);
        Assert.Equal($"/admin/applications/{appId}/modules", segments[0].Url);
    }

    [Fact]
    public async Task BuildAsync_WithModuleId_ReturnsTwoSegments()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, moduleId, _, _) = await CreateFullChainAsync(context);
        var builder = CreateBuilder(context);

        var segments = await builder.BuildAsync(appId, moduleId);

        Assert.Equal(2, segments.Count);
        Assert.Equal("Payments", segments[1].Label);
        Assert.Equal($"/admin/applications/{appId}/modules/{moduleId}/screen-services", segments[1].Url);
    }

    [Fact]
    public async Task BuildAsync_WithFullChain_ReturnsFourSegmentsInRootToLeafOrder()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, moduleId, screenServiceId, processId) = await CreateFullChainAsync(context);
        var builder = CreateBuilder(context);

        var segments = await builder.BuildAsync(appId, moduleId, screenServiceId, processId);

        Assert.Equal(4, segments.Count);
        Assert.Equal("BreadcrumbTestApp", segments[0].Label);
        Assert.Equal("Payments", segments[1].Label);
        Assert.Equal("PaymentGateway", segments[2].Label);
        Assert.Equal("ChargeCard", segments[3].Label);
        Assert.Equal(
            $"/admin/applications/{appId}/modules/{moduleId}/screen-services/{screenServiceId}/processes/{processId}/operations",
            segments[3].Url);
    }

    [Fact]
    public async Task BuildAsync_WithUnknownAppId_Throws()
    {
        using var context = TestDatabase.CreateContext();
        var builder = CreateBuilder(context);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await builder.BuildAsync(999999));
    }
}
