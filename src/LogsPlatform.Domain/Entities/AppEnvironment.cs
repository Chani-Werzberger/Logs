namespace LogsPlatform.Domain.Entities;

public class AppEnvironment
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public bool IsProduction { get; set; }
}
