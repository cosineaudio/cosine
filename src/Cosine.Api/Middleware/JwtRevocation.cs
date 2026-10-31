using System.Security.Claims;
using Cosine.Core.Interfaces;
using Cosine.Core.Security;

namespace Cosine.Api.Middleware;

/// <summary>
/// Re-checks a signature-valid JWT against the account it names. A signature and an expiry
/// alone give the app no revocation path: a token issued before a password change - or before
/// the account was deleted outright - stayed good for the rest of its 24 h lifetime.
///
/// The cost is one primary-key lookup per JWT-authenticated request. That is the same shape of
/// work <see cref="SubsonicAuthHandler"/> already does on every <c>/rest</c> call.
/// </summary>
public static class JwtRevocation
{
    /// <summary>
    /// Returns null when the token is still good, otherwise the reason it is not.
    /// </summary>
    public static async Task<string?> CheckAsync(
        ClaimsPrincipal principal, IUserService users, CancellationToken ct = default)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out var userId))
            return "Token carries no usable subject";

        var user = await users.GetUserAsync(userId, ct);
        if (user is null)
            return "Account no longer exists";

        if (!SecurityStamp.Matches(user, principal.FindFirstValue(SecurityStamp.ClaimType)))
            return "Credentials changed since this token was issued";

        return null;
    }
}
