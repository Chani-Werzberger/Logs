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
    private static async Task<(int appId, int moduleId, int screenServiceId, int processId)> CreateFullChainAsync(
        LogsPlatformDbContext context, string appName = "BreadcrumbTestApp")
    {
        var application = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
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
    public async Task BuildAsync_WithOnlyAppId_ReturnsTwoSegments()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _, _, _) = await CreateFullChainAsync(context);
        var builder = CreateBuilder(context);

        var segments = await builder.BuildAsync(appId);

        Assert.Equal(2, segments.Count);
        Assert.Equal("Applications", segments[0].Label);
        Assert.Equal("/admin/applications", segments[0].Url);
        Assert.Equal("BreadcrumbTestApp", segments[1].Label);
        Assert.Equal($"/admin/applications/{appId}/modules", segments[1].Url);
    }

    [Fact]
    public async Task BuildAsync_WithModuleId_ReturnsThreeSegments()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, moduleId, _, _) = await CreateFullChainAsync(context);
        var builder = CreateBuilder(context);

        var segments = await builder.BuildAsync(appId, moduleId);

        Assert.Equal(3, segments.Count);
        Assert.Equal("Payments", segments[2].Label);
        Assert.Equal($"/admin/applications/{appId}/modules/{moduleId}/screen-services", segments[2].Url);
    }

    [Fact]
    public async Task BuildAsync_WithFullChain_ReturnsFiveSegmentsInRootToLeafOrder()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, moduleId, screenServiceId, processId) = await CreateFullChainAsync(context);
        var builder = CreateBuilder(context);

        var segments = await builder.BuildAsync(appId, moduleId, screenServiceId, processId);

        Assert.Equal(5, segments.Count);
        Assert.Equal("Applications", segments[0].Label);
        Assert.Equal("BreadcrumbTestApp", segments[1].Label);
        Assert.Equal("Payments", segments[2].Label);
        Assert.Equal("PaymentGateway", segments[3].Label);
        Assert.Equal(
            $"/admin/applications/{appId}/modules/{moduleId}/screen-services/{screenServiceId}/processes",
            segments[3].Url);
        Assert.Equal("ChargeCard", segments[4].Label);
        Assert.Equal(
            $"/admin/applications/{appId}/modules/{moduleId}/screen-services/{screenServiceId}/processes/{processId}/operations",
            segments[4].Url);
    }

    [Fact]
    public async Task BuildAsync_WithUnknownAppId_Throws()
    {
        using var context = TestDatabase.CreateContext();
        var builder = CreateBuilder(context);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await builder.BuildAsync(999999));
    }

    [Fact]
    public async Task BuildAsync_WithUnknownModuleId_Throws()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, _, _, _) = await CreateFullChainAsync(context);
        var builder = CreateBuilder(context);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await builder.BuildAsync(appId, 999999));
    }

    [Fact]
    public async Task BuildAsync_WithUnknownScreenServiceId_Throws()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, moduleId, _, _) = await CreateFullChainAsync(context);
        var builder = CreateBuilder(context);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await builder.BuildAsync(appId, moduleId, 999999));
    }

    [Fact]
    public async Task BuildAsync_WithUnknownProcessId_Throws()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, moduleId, screenServiceId, _) = await CreateFullChainAsync(context);
        var builder = CreateBuilder(context);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await builder.BuildAsync(appId, moduleId, screenServiceId, 999999));
    }

    [Fact]
    public async Task BuildAsync_WithModuleFromDifferentApplication_Throws()
    {
        using var context = TestDatabase.CreateContext();
        var (appId1, _, _, _) = await CreateFullChainAsync(context, "BreadcrumbTestApp1");
        var (_, moduleId2, _, _) = await CreateFullChainAsync(context, "BreadcrumbTestApp2");
        var builder = CreateBuilder(context);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await builder.BuildAsync(appId1, moduleId2));
    }
}
