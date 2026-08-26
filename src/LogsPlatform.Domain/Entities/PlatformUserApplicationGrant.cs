namespace LogsPlatform.Domain.Entities;

public class PlatformUserApplicationGrant
{
    public int Id { get; set; }
    public int PlatformUserId { get; set; }
    public PlatformUser PlatformUser { get; set; } = null!;
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
}
