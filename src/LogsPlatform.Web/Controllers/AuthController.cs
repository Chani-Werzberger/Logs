using System.Security.Claims;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly IPlatformUserRepository _users;

    public AuthController(IPlatformUserRepository users)
    {
        _users = users;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _users.GetByUsernameAsync(request.Username);
        if (user is null || !user.IsActive || !PasswordHasher.Verify(request.Password, user.PasswordHash))
        {
            return Unauthorized();
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("IsAdmin", user.IsAdmin ? "true" : "false")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return NoContent();
    }

    [HttpPost("logout")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }
}
