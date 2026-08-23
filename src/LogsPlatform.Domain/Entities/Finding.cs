namespace LogsPlatform.Domain.Entities;

public enum FindingType { ErrorSpike, MissingActivity, PerformanceDegradation, NewException, CustomerAnomaly }

public enum FindingSeverity { Low, Medium, High }

public enum ConfidenceLevel { Low, Medium, High }

public enum FindingStatus { New, Acknowledged, Resolved, Dismissed }

public class Finding
{
    public long Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public int EnvironmentId { get; set; }
    public AppEnvironment Environment { get; set; } = null!;
    public FindingType Type { get; set; }
    public AnalysisScopeType ScopeType { get; set; }
    public long ScopeId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; }
    public FindingSeverity Severity { get; set; }
    public ConfidenceLevel ConfidenceLevel { get; set; }
    public FindingStatus Status { get; set; }
}
