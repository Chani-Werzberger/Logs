namespace LogsPlatform.Web.Contracts;

public record CreateLogSourceRequest(string Name, string? Description);

public record LogSourceResponse(int Id, int ApplicationId, string Name, string? Description, bool IsActive);

public record RenameLogSourceRequest(string Name, string? Description);
