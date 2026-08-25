namespace LogsPlatform.Domain.Entities;

public class AdminAuditLogEntry
{
    public long Id { get; set; }
    public int PlatformUserId { get; set; }
    public PlatformUser PlatformUser { get; set; } = null!;
    public DateTime Timestamp { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
