namespace LogsPlatform.Domain.Entities;

public enum AnalysisScopeType { Operation, ExceptionGroup }

public enum AnalysisMetricType { EventCount, ExceptionCount, DurationMs }

public class Baseline
{
    public long Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public int EnvironmentId { get; set; }
    public AppEnvironment Environment { get; set; } = null!;
    public AnalysisScopeType ScopeType { get; set; }
    public long ScopeId { get; set; }
    public AnalysisMetricType MetricType { get; set; }
    public byte BucketHourOfDay { get; set; }
    public double MeanValue { get; set; }
    public double StdDevValue { get; set; }
    public int SampleCount { get; set; }
    public DateTime LastUpdatedAt { get; set; }
}
