namespace LogsPlatform.Domain.Entities;

public class ProcessNode
{
    public int Id { get; set; }
    public int ScreenServiceId { get; set; }
    public ScreenService ScreenService { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<Operation> Operations { get; set; } = new List<Operation>();
}
