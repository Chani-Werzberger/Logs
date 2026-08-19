namespace LogsPlatform.Domain.Entities;

public class Application
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<AppEnvironment> Environments { get; set; } = new List<AppEnvironment>();
    public ICollection<AppModule> Modules { get; set; } = new List<AppModule>();
    public ICollection<Customer> Customers { get; set; } = new List<Customer>();
    public ICollection<AppUser> Users { get; set; } = new List<AppUser>();
    public ICollection<LogSource> LogSources { get; set; } = new List<LogSource>();
}
