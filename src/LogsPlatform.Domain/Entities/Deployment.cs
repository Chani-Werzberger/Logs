namespace LogsPlatform.Domain.Entities;

public class Deployment
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public int EnvironmentId { get; set; }
    public AppEnvironment Environment { get; set; } = null!;
    public int VersionId { get; set; }
    public AppVersion Version { get; set; } = null!;
    public DateTime DeployedAt { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}
