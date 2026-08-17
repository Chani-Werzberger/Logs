namespace LogsPlatform.Web.Contracts;

public record CreateApplicationRequest(string Name, string? Description);

public record ApplicationResponse(int Id, string Name, string? Description, DateTime CreatedAt);

public record CreateEnvironmentRequest(string Name, bool IsProduction);

public record EnvironmentResponse(int Id, int ApplicationId, string Name, bool IsProduction);
