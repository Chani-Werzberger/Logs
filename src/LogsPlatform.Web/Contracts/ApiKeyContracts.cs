namespace LogsPlatform.Web.Contracts;

public record CreateApiKeyRequest(string Label);

public record ApiKeyResponse(int Id, int ApplicationId, string Label, DateTime CreatedAt, DateTime? RevokedAt);

public record CreateApiKeyResponse(int Id, int ApplicationId, string Label, DateTime CreatedAt, string ApiKey);
