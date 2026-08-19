namespace LogsPlatform.Domain.Entities;

public class AppVersion
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public string VersionNumber { get; set; } = string.Empty;
    public string? ReleaseNotes { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; } = true;
}
