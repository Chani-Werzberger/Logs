namespace LogsPlatform.Web.Contracts;

public record CreateModuleRequest(string Name, string? Description);

public record ModuleResponse(int Id, int ApplicationId, string Name, string? Description, bool IsActive);

public record RenameModuleRequest(string Name, string? Description);
