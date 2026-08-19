namespace LogsPlatform.Web.Contracts;

public record CreateDeploymentRequest(int EnvironmentId, int VersionId, DateTime DeployedAt, string? Notes);

public record DeploymentResponse(int Id, int ApplicationId, int EnvironmentId, int VersionId, DateTime DeployedAt, string? Notes, bool IsActive);

public record RenameDeploymentRequest(string? Notes);
