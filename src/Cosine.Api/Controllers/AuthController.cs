using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Cosine.Api.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly IAuthThrottle _throttle;
    private readonly JwtOptions _jwtOptions;
    private readonly AuthOptions _authOptions;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IUserService userService,
        IAuthThrottle throttle,
        IOptions<JwtOptions> jwtOptions,
        IOptions<AuthOptions> authOptions,
        ILogger<AuthController> logger)
    {
        _userService = userService;
        _throttle = throttle;
        _jwtOptions = jwtOptions.Value;
        _authOptions = authOptions.Value;
        _logger = logger;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
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
            var token = GenerateJwt(user);
            SetJwtCookie(token);
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
        Response.Cookies.Delete("cosine_auth");
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

    private string GenerateJwt(Core.Entities.User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("IsAdmin", user.IsAdmin.ToString()),
            // Lets the token be revoked before it expires - see SecurityStamp.
            new Claim(SecurityStamp.ClaimType, SecurityStamp.For(user))
        };

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_jwtOptions.ExpirationMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private void SetJwtCookie(string token)
    {
        Response.Cookies.Append("cosine_auth", token, new CookieOptions
        {
            HttpOnly = true,
            // A Secure cookie is silently dropped by the browser on a plain-HTTP origin, which
            // makes login return 200 and leave the user logged out on any LAN deployment
            // (localhost is exempt, which is why this only bites off-box). Mirror the request
            // scheme by default; Auth__SecureCookie=true forces it on behind a TLS proxy.
            Secure = _authOptions.SecureCookie ?? Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.ExpirationMinutes)
        });
    }
}

public record LoginRequest(string Username, string Password);
