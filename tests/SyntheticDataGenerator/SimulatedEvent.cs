namespace LogsPlatform.SyntheticDataGenerator;

public record SimulatedEvent(
    DateTime Timestamp,
    string Severity,
    string? Module,
    string? ScreenService,
    string? Process,
    string? Operation,
    string? CorrelationId,
    double? DurationMs,
    string? CustomerId,
    string Message,
    string? ExceptionType,
    string? StackTrace);
