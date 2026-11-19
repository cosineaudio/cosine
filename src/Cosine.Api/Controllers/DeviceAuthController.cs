using System.Security.Claims;
using Cosine.Api.Authentication;
using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Cosine.Api.Controllers;

/// <summary>
/// Sign-in for native clients (the Android app). The browser keeps its cookie session; a device
/// instead signs in once, keeps the refresh token it gets back, and sends
/// <c>Authorization: Bearer &lt;accessToken&gt;</c>, minting a new access token from the refresh
/// token whenever the current one expires. See <see cref="DeviceTokenService"/>.
///
/// These routes sit under <c>/auth</c>, so state-changing calls must carry
/// <c>X-Requested-With</c> like any other non-browser client.
/// </summary>
[ApiController]
[Route("auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class DeviceAuthController(
    IUserService userService,
    IAuthThrottle throttle,
    SessionIssuer sessions,
    DeviceTokenService devices,
    IOptions<AuthOptions> authOptions,
    ILogger<DeviceAuthController> logger) : ControllerBase
{
    public record DeviceLoginRequest(string Username, string Password, string? DeviceName);
    public record RefreshRequest(string RefreshToken);
    public record DeviceUser(Guid Id, string Username, bool IsAdmin, string? Email);
    public record DeviceLoginResponse(
        string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DeviceUser User);
    public record AccessTokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, DeviceUser User);
    public record DeviceInfo(Guid Id, string? Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

    [HttpPost("device/login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] DeviceLoginRequest request, CancellationToken ct)
    {
        if (!authOptions.Value.LocalLoginEnabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Password login is disabled" });

        var throttleKey = AuthThrottleKey.For(HttpContext, request.Username);
        if (throttle.IsLocked(throttleKey))
        {
            logger.LogWarning("Device login for '{Username}' rejected: too many failed attempts", request.Username);
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { error = "Too many failed login attempts. Try again later." });
        }

        try
        {
            var user = await userService.AuthenticateAsync(request.Username, request.Password, ct);
            throttle.RecordSuccess(throttleKey);

            var refreshToken = await devices.IssueAsync(user, request.DeviceName, ct);
            var (accessToken, expiresAt) = sessions.GenerateDeviceAccessToken(user);

            logger.LogInformation("Device login succeeded for '{Username}'", user.Username);
            return Ok(new DeviceLoginResponse(accessToken, expiresAt, refreshToken,
                new DeviceUser(user.Id, user.Username, user.IsAdmin, user.Email)));
        }
        catch (UnauthorizedAccessException)
        {
            throttle.RecordFailure(throttleKey);
            logger.LogInformation("Device login failed for '{Username}': invalid credentials", request.Username);
            return Unauthorized(new { error = "Invalid username or password" });
        }
    }

    [HttpPost("device/refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        var redeemed = await devices.RedeemAsync(request.RefreshToken, ct);
        if (redeemed is not { } r)
            return Unauthorized(new { error = "Device session expired or revoked" });

        var (accessToken, expiresAt) = sessions.GenerateDeviceAccessToken(r.User);
        return Ok(new AccessTokenResponse(accessToken, expiresAt,
            new DeviceUser(r.User.Id, r.User.Username, r.User.IsAdmin, r.User.Email)));
    }

    /// <summary>Signs this device out. Always 204, so it cannot be used to probe tokens.</summary>
    [HttpPost("device/logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest request, CancellationToken ct)
    {
        await devices.RevokeAsync(request.RefreshToken, ct);
        return NoContent();
    }

    [HttpGet("devices")]
    [Authorize]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var keys = await devices.ListAsync(CurrentUserId, ct);
        return Ok(keys.Select(k => new DeviceInfo(k.Id, k.Name, k.CreatedAt, k.LastUsedAt)));
    }

    [HttpDelete("devices/{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) =>
        await devices.RevokeAsync(CurrentUserId, id, ct)
            ? NoContent()
            : NotFound(new { error = "Device not found" });

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
