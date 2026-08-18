namespace LogsPlatform.Domain.Entities;

public class AppModule
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<ScreenService> ScreenServices { get; set; } = new List<ScreenService>();
}
