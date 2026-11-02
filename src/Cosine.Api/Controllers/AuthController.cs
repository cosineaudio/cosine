using System.Security.Claims;
using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Cosine.Api.Authentication;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Distributed;
using System.Security.Cryptography;

namespace Cosine.Api.Controllers;

[ApiController]
[Route("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AuthController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly IAuthThrottle _throttle;
    private readonly SessionIssuer _sessions;
    private readonly ExternalIdentityService _identities;
    private readonly IDistributedCache _cache;
    private readonly AuthOptions _authOptions;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IUserService userService,
        IAuthThrottle throttle,
        SessionIssuer sessions,
        ExternalIdentityService identities,
        IDistributedCache cache,
        IOptions<AuthOptions> authOptions,
        ILogger<AuthController> logger)
    {
        _userService = userService;
        _throttle = throttle;
        _sessions = sessions;
        _identities = identities;
        _cache = cache;
        _authOptions = authOptions.Value;
        _logger = logger;
    }

    [HttpGet("config")]
    [AllowAnonymous]
    public IActionResult Config() => Ok(new
    {
        _authOptions.LocalLoginEnabled,
        OidcEnabled = _authOptions.Oidc.Enabled,
        OidcDisplayName = _authOptions.Oidc.DisplayName
    });

    [HttpGet("oidc/login")]
    [AllowAnonymous]
    public IActionResult OidcLogin(string? returnUrl = null)
    {
        if (!_authOptions.Oidc.Enabled) return NotFound();
        return Challenge(new AuthenticationProperties
        {
            RedirectUri = OidcAuthentication.IsLocalPath(returnUrl) ? returnUrl : "/"
        }, OidcAuthentication.Scheme);
    }

    [HttpPost("oidc/link")]
    [Authorize]
    public async Task<IActionResult> Link(CancellationToken ct)
    {
        if (!_authOptions.Oidc.Enabled) return NotFound();
        if (!long.TryParse(User.FindFirstValue("auth_time"), out var authenticatedAt)
            || authenticatedAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - authenticatedAt > 300)
            return StatusCode(403, new { error = "Sign out and sign in again before linking an identity" });

        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var binding = User.FindFirstValue(ClaimTypes.NameIdentifier) + ":" + User.FindFirstValue(Core.Security.SecurityStamp.ClaimType);
        await _cache.SetStringAsync("oidc-link:" + ticket, binding,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, ct);
        return Ok(new { redirectUrl = "/auth/oidc/link-start?ticket=" + ticket });
    }

    [HttpGet("oidc/link-start")]
    [Authorize]
    public async Task<IActionResult> LinkStart(string ticket, CancellationToken ct)
    {
        if (!_authOptions.Oidc.Enabled) return NotFound();
        if (ticket.Length != 64) return BadRequest();
        var key = "oidc-link:" + ticket;
        var binding = await _cache.GetStringAsync(key, ct);
        await _cache.RemoveAsync(key, ct);
        var expected = User.FindFirstValue(ClaimTypes.NameIdentifier) + ":" + User.FindFirstValue(Core.Security.SecurityStamp.ClaimType);
        if (binding is null || binding != expected) return Forbid();
        var properties = new AuthenticationProperties { RedirectUri = "/profile", ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) };
        properties.Items[OidcAuthentication.LinkBinding] = binding;
        return Challenge(properties, OidcAuthentication.Scheme);
    }

    [HttpPost("rotate-credentials")]
    [Authorize]
    public async Task<IActionResult> RotateCredentials(CancellationToken ct)
    {
        await _userService.RotateCredentialsAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), ct);
        Response.Cookies.Delete("cosine_auth", new CookieOptions { Path = "/" });
        return NoContent();
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        if (!_authOptions.LocalLoginEnabled)
            return StatusCode(403, new { error = "Password login is disabled" });

        var throttleKey = AuthThrottleKey.For(HttpContext, request.Username);
        if (_throttle.IsLocked(throttleKey))
        {
            _logger.LogWarning("Login for '{Username}' rejected: too many failed attempts", request.Username);
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { error = "Too many failed login attempts. Try again later." });
        }

        try
        {
            var user = await _userService.AuthenticateAsync(request.Username, request.Password, ct);
            _sessions.Issue(HttpContext, user, "password");
            _throttle.RecordSuccess(throttleKey);

            _logger.LogInformation("Login succeeded for '{Username}'", user.Username);

            return Ok(new
            {
                user.Id,
                user.Username,
                user.IsAdmin,
                user.Email
            });
        }
        catch (UnauthorizedAccessException)
        {
            // Audited so repeated failures (brute force) are visible in the log.
            _throttle.RecordFailure(throttleKey);
            _logger.LogInformation("Login failed for '{Username}': invalid credentials", request.Username);
            return Unauthorized(new { error = "Invalid username or password" });
        }
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("cosine_auth", new CookieOptions { Path = "/" });
        return Ok();
    }

    [HttpGet("profile")]
    [Authorize]
    public async Task<IActionResult> Profile(CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _userService.GetUserAsync(userId, ct);
        if (user is null)
            return NotFound();

        return Ok(new
        {
            user.Id,
            user.Username,
            user.Email,
            user.IsAdmin,
            user.CreatedAt,
            user.LastLoginAt,
            HasLocalPassword = user.PasswordHash is not null,
            OidcLinked = _authOptions.Oidc.Enabled && await _identities.IsLinkedAsync(user.Id, ct),
            // The secret Subsonic clients must use as their "password" for token auth.
            SubsonicToken = user.SubsonicSecret
        });
    }

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var username = User.FindFirstValue(ClaimTypes.Name);
        var isAdmin = User.FindFirstValue("IsAdmin");

        return Ok(new
        {
            Id = userId,
            Username = username,
            IsAdmin = isAdmin == "True"
        });
    }
}

public record LoginRequest(string Username, string Password);
