namespace LogsPlatform.Domain.Entities;

public enum FindingStatementKind { Fact, Observation, Hypothesis, Conclusion }

public enum DetectorStatementKind { Fact, Observation, Hypothesis }

public class FindingStatement
{
    public long Id { get; set; }
    public long FindingId { get; set; }
    public Finding Finding { get; set; } = null!;
    public FindingStatementKind Kind { get; set; }
    public string Text { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
}
