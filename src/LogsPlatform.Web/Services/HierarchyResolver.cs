using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services;

public record HierarchyResolutionResult(int? ModuleId, int? ScreenServiceId, int? ProcessId, int? OperationId, string? WarningField);

public class HierarchyResolver
{
    private readonly IAppModuleRepository _modules;
    private readonly IScreenServiceRepository _screenServices;
    private readonly IProcessNodeRepository _processes;
    private readonly IOperationRepository _operations;

    public HierarchyResolver(
        IAppModuleRepository modules,
        IScreenServiceRepository screenServices,
        IProcessNodeRepository processes,
        IOperationRepository operations)
    {
        _modules = modules;
        _screenServices = screenServices;
        _processes = processes;
        _operations = operations;
    }

    public async Task<HierarchyResolutionResult> ResolveAsync(int applicationId, string? module, string? screenService, string? process, string? operation)
    {
        if (string.IsNullOrWhiteSpace(module))
        {
            return new HierarchyResolutionResult(null, null, null, null, null);
        }

        var modules = await _modules.GetByApplicationIdAsync(applicationId);
        var moduleEntity = modules.FirstOrDefault(m => m.Name == module);
        if (moduleEntity is null)
        {
            return new HierarchyResolutionResult(null, null, null, null, "module");
        }

        if (string.IsNullOrWhiteSpace(screenService))
        {
            return new HierarchyResolutionResult(moduleEntity.Id, null, null, null, null);
        }

        var screenServices = await _screenServices.GetByModuleIdAsync(moduleEntity.Id);
        var screenServiceEntity = screenServices.FirstOrDefault(s => s.Name == screenService);
        if (screenServiceEntity is null)
        {
            return new HierarchyResolutionResult(moduleEntity.Id, null, null, null, "screenService");
        }

        if (string.IsNullOrWhiteSpace(process))
        {
            return new HierarchyResolutionResult(moduleEntity.Id, screenServiceEntity.Id, null, null, null);
        }

        var processes = await _processes.GetByScreenServiceIdAsync(screenServiceEntity.Id);
        var processEntity = processes.FirstOrDefault(p => p.Name == process);
        if (processEntity is null)
        {
            return new HierarchyResolutionResult(moduleEntity.Id, screenServiceEntity.Id, null, null, "process");
        }

        if (string.IsNullOrWhiteSpace(operation))
        {
            return new HierarchyResolutionResult(moduleEntity.Id, screenServiceEntity.Id, processEntity.Id, null, null);
        }

        var operations = await _operations.GetByProcessIdAsync(processEntity.Id);
        var operationEntity = operations.FirstOrDefault(o => o.Name == operation);
        if (operationEntity is null)
        {
            return new HierarchyResolutionResult(moduleEntity.Id, screenServiceEntity.Id, processEntity.Id, null, "operation");
        }

        return new HierarchyResolutionResult(moduleEntity.Id, screenServiceEntity.Id, processEntity.Id, operationEntity.Id, null);
    }
}
