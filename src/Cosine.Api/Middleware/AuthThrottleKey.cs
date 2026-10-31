namespace Cosine.Api.Middleware;

/// <summary>
/// Builds the key failed authentications are counted against.
/// </summary>
public static class AuthThrottleKey
{
    /// <summary>
    /// Keyed by client address *and* username. Address alone would collapse every client behind
    /// a reverse proxy into one bucket, where ten bad guesses would lock out the whole instance;
    /// username alone would let anyone lock the admin out globally. The pair bounds the damage
    /// to one account from one source, which is also the unit an attacker has to pay for.
    /// </summary>
    public static string For(HttpContext context, string? username)
    {
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"{address}|{username?.Trim().ToLowerInvariant() ?? string.Empty}";
    }
}
