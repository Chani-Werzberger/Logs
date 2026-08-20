using System.Security.Claims;
using System.Text.Encodings.Web;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogsPlatform.Web.Authentication;

public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    public const string ApplicationIdClaimType = "ApplicationId";

    private readonly IApiKeyRepository _apiKeys;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyRepository apiKeys)
        : base(options, logger, encoder)
    {
        _apiKeys = apiKeys;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var rawKeyValues) || string.IsNullOrWhiteSpace(rawKeyValues))
        {
            return AuthenticateResult.Fail("Missing X-Api-Key header.");
        }

        var rawKey = rawKeyValues.ToString();
        var keyHash = ApiKeyHasher.Hash(rawKey);
        var apiKey = await _apiKeys.GetByKeyHashAsync(keyHash);

        if (apiKey is null || apiKey.RevokedAt is not null)
        {
            return AuthenticateResult.Fail("Invalid or revoked API key.");
        }

        var claims = new[] { new Claim(ApplicationIdClaimType, apiKey.ApplicationId.ToString()) };
        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationOptions.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, ApiKeyAuthenticationOptions.SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
