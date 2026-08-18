namespace LogsPlatform.Web.Contracts;

public record CreateProcessRequest(string Name, string? Description);

public record ProcessResponse(int Id, int ScreenServiceId, string Name, string? Description, bool IsActive);

public record RenameProcessRequest(string Name, string? Description);
