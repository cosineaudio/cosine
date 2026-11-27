namespace Cosine.Core.Options;

public class JwtOptions
{
    public const string Section = "Jwt";
    public string SigningKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "Cosine";
    public string Audience { get; set; } = "Cosine";
    public int ExpirationMinutes { get; set; } = 1440; // 24 hours

    /// <summary>
    /// Lifetime of the access tokens native clients get from <c>/auth/device/*</c>. Short,
    /// because the device holds a refresh token to mint the next one.
    /// </summary>
    public int DeviceAccessTokenMinutes { get; set; } = 60;
}
