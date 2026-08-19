namespace LogsPlatform.Web.Contracts;

public record CreateVersionRequest(string VersionNumber, string? ReleaseNotes);

public record VersionResponse(int Id, int ApplicationId, string VersionNumber, string? ReleaseNotes, DateTime CreatedAt, bool IsActive);

public record RenameVersionRequest(string? ReleaseNotes);
