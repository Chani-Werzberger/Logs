namespace LogsPlatform.Web.Contracts;

public record CreateOperationRequest(string Name, string? Description);

public record OperationResponse(int Id, int ProcessId, string Name, string? Description, bool IsActive);

public record RenameOperationRequest(string Name, string? Description);
