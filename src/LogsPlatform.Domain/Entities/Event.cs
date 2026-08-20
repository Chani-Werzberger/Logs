namespace LogsPlatform.Domain.Entities;

public class Event
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public int Severity { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public int EnvironmentId { get; set; }
    public AppEnvironment Environment { get; set; } = null!;
    public int? VersionId { get; set; }
    public AppVersion? Version { get; set; }
    public int? ModuleId { get; set; }
    public AppModule? Module { get; set; }
    public int? ScreenServiceId { get; set; }
    public ScreenService? ScreenService { get; set; }
    public int? ProcessId { get; set; }
    public ProcessNode? Process { get; set; }
    public int? OperationId { get; set; }
    public Operation? Operation { get; set; }
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? AppUserId { get; set; }
    public AppUser? AppUser { get; set; }
    public string? EventKey { get; set; }
    public string? CorrelationId { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public string? ParentSpanId { get; set; }
    public double? DurationMs { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? MessageTemplate { get; set; }
    public long? ExceptionGroupId { get; set; }
    public ExceptionGroup? ExceptionGroup { get; set; }
    public string? StackTrace { get; set; }
    public string? MetadataJson { get; set; }
}
