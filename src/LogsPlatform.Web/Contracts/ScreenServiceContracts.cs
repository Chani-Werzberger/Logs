namespace LogsPlatform.Web.Contracts;

public record CreateScreenServiceRequest(string Name, string Type, string? Description);

public record ScreenServiceResponse(int Id, int ModuleId, string Name, string Type, string? Description, bool IsActive);

public record RenameScreenServiceRequest(string Name, string? Description);
