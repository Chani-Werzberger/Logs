using Microsoft.AspNetCore.Authentication;

namespace LogsPlatform.Web.Authentication;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "ApiKey";
}
