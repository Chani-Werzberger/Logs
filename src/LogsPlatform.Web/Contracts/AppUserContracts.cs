namespace LogsPlatform.Web.Contracts;

public record CreateAppUserRequest(string ExternalUserId, string DisplayName);

public record AppUserResponse(int Id, int ApplicationId, string ExternalUserId, string DisplayName, bool IsActive);

public record RenameAppUserRequest(string DisplayName);
