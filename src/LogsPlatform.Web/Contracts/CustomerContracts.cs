namespace LogsPlatform.Web.Contracts;

public record CreateCustomerRequest(string ExternalCustomerId, string Name);

public record CustomerResponse(int Id, int ApplicationId, string ExternalCustomerId, string Name, bool IsActive);

public record RenameCustomerRequest(string Name);
