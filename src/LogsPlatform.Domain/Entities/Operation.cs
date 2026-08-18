namespace LogsPlatform.Domain.Entities;

public class Operation
{
    public int Id { get; set; }
    public int ProcessId { get; set; }
    public ProcessNode Process { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}
