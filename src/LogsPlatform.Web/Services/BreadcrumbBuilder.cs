using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services;

public record BreadcrumbSegment(string Label, string Url);

public class BreadcrumbBuilder
{
    private readonly IApplicationRepository _applications;
    private readonly IAppModuleRepository _modules;
    private readonly IScreenServiceRepository _screenServices;
    private readonly IProcessNodeRepository _processes;

    public BreadcrumbBuilder(
        IApplicationRepository applications,
        IAppModuleRepository modules,
        IScreenServiceRepository screenServices,
        IProcessNodeRepository processes)
    {
        _applications = applications;
        _modules = modules;
        _screenServices = screenServices;
        _processes = processes;
    }

    public async Task<List<BreadcrumbSegment>> BuildAsync(
        int appId, int? moduleId = null, int? screenServiceId = null, int? processId = null)
    {
        var segments = new List<BreadcrumbSegment>();

        var application = await _applications.GetByIdAsync(appId)
            ?? throw new InvalidOperationException($"Application {appId} not found.");
        segments.Add(new BreadcrumbSegment(application.Name, $"/admin/applications/{appId}/modules"));

        if (moduleId is null) return segments;

        var module = await _modules.GetByIdAsync(moduleId.Value)
            ?? throw new InvalidOperationException($"Module {moduleId} not found.");
        segments.Add(new BreadcrumbSegment(module.Name, $"/admin/applications/{appId}/modules/{moduleId}/screen-services"));

        if (screenServiceId is null) return segments;

        var screenService = await _screenServices.GetByIdAsync(screenServiceId.Value)
            ?? throw new InvalidOperationException($"ScreenService {screenServiceId} not found.");
        segments.Add(new BreadcrumbSegment(
            screenService.Name,
            $"/admin/applications/{appId}/modules/{moduleId}/screen-services/{screenServiceId}/processes"));

        if (processId is null) return segments;

        var process = await _processes.GetByIdAsync(processId.Value)
            ?? throw new InvalidOperationException($"ProcessNode {processId} not found.");
        segments.Add(new BreadcrumbSegment(
            process.Name,
            $"/admin/applications/{appId}/modules/{moduleId}/screen-services/{screenServiceId}/processes/{processId}/operations"));

        return segments;
    }
}
