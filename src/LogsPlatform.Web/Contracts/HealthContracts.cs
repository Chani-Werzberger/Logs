namespace LogsPlatform.Web.Contracts;

public record HealthResponse(string Status, DatabaseHealth Database, AnalysisEngineHealth AnalysisEngine);

public record DatabaseHealth(string Status, double ResponseTimeMs);

public record AnalysisEngineHealth(string Status, DateTime? LastTickCompletedAt, double? SecondsSinceLastTick);
