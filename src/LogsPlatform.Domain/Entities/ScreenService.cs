namespace LogsPlatform.Domain.Entities;

public enum ScreenServiceType
{
    Screen,
    Service
}

public class ScreenService
{
    public int Id { get; set; }
    public int ModuleId { get; set; }
    public AppModule Module { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public ScreenServiceType Type { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}
