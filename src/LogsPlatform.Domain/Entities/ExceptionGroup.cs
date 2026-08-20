namespace LogsPlatform.Domain.Entities;

public class ExceptionGroup
{
    public long Id { get; set; }
    public int ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public string Fingerprint { get; set; } = string.Empty;
    public string ExceptionType { get; set; } = string.Empty;
    public string MessageTemplate { get; set; } = string.Empty;
    public string RepresentativeStackTrace { get; set; } = string.Empty;
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public int OccurrenceCount { get; set; }
}
