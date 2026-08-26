namespace LogsPlatform.Web.Services.Analysis;

public class AnalysisEngineHealthStatus
{
    private readonly object _lock = new();
    private DateTime? _lastTickCompletedAt;

    public void RecordTickCompleted(DateTime completedAtUtc)
    {
        lock (_lock)
        {
            _lastTickCompletedAt = completedAtUtc;
        }
    }

    public DateTime? LastTickCompletedAt
    {
        get
        {
            lock (_lock)
            {
                return _lastTickCompletedAt;
            }
        }
    }
}
