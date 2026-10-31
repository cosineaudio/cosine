using System.Security.Cryptography;
using System.Text;
using Cosine.Core.Entities;

namespace Cosine.Core.Security;

/// <summary>
/// A short fingerprint of the credentials a session was issued against, carried in the JWT
/// and re-checked on every request.
///
/// Without it there is no revocation path at all: a JWT stays valid for its full 24 h even
/// after the password changes or the account is deleted. The stamp is derived from
/// <see cref="User.SubsonicSecret"/>, which <see cref="Cosine.Core.Interfaces.IUserService"/>
/// rotates on every password change - so one rotation invalidates both the Subsonic token
/// credential and every issued JWT, with no extra column to keep in sync.
///
/// It is a hash, not the secret itself: the JWT payload is readable by anything holding the
/// cookie, and the secret is a live credential for the <c>/rest</c> surface.
/// </summary>
public static class SecurityStamp
{
    /// <summary>Claim carrying the stamp. Short, because it rides in every request's cookie.</summary>
    public const string ClaimType = "sstamp";

    public static string For(User user) => For(user.SubsonicSecret);

    public static string For(string subsonicSecret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subsonicSecret)))[..16]
            .ToLowerInvariant();

    /// <summary>Whether <paramref name="claimValue"/> still matches the user's current credentials.</summary>
    public static bool Matches(User user, string? claimValue) =>
        !string.IsNullOrEmpty(claimValue)
        && string.Equals(claimValue, For(user), StringComparison.Ordinal);
}
