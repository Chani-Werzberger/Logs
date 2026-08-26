namespace LogsPlatform.Domain.Entities;

public enum EvidenceType { Event, ExceptionGroup, Deployment, Baseline, Operation, Finding }

public class Evidence
{
    public long Id { get; set; }
    public long FindingId { get; set; }
    public Finding Finding { get; set; } = null!;
    public EvidenceType EvidenceType { get; set; }
    public long ReferenceId { get; set; }
    public string Description { get; set; } = string.Empty;
}
