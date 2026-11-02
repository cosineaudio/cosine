namespace Cosine.Core.Options;

public class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Enables password login in the web app and legacy Subsonic clients.</summary>
    public bool LocalLoginEnabled { get; set; } = true;
    public OidcOptions Oidc { get; set; } = new();

    /// <summary>
    /// Whether the auth cookie carries the <c>Secure</c> flag. <c>null</c> (the default) mirrors
    /// the scheme of the request that issued it, so a plain-HTTP LAN deployment still receives a
    /// usable cookie. Set to <c>true</c> when TLS is terminated by a reverse proxy (the app then
    /// sees plain HTTP but the browser does not), or <c>false</c> to force it off.
    /// </summary>
    public bool? SecureCookie { get; set; }

    /// <summary>
    /// Password for the <c>admin</c> account created on first start when no users exist.
    /// Unset (the default) generates a random one and logs it once - the account is never
    /// created with a guessable password.
    /// </summary>
    public string? BootstrapAdminPassword { get; set; }

    /// <summary>Failed authentications from one client address before it is locked out.</summary>
    public int MaxFailedAttempts { get; set; } = 10;

    /// <summary>Sliding window over which failures are counted.</summary>
    public int FailureWindowSeconds { get; set; } = 300;

    /// <summary>How long a client address stays locked out once the threshold is crossed.</summary>
    public int LockoutSeconds { get; set; } = 300;
}
