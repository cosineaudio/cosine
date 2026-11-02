namespace Cosine.Core.Options;

public class OidcOptions
{
    public bool Enabled { get; set; }
    public string DisplayName { get; set; } = "Single sign-on";
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Browser-facing origin, e.g. https://cosine.example.com. No path or trailing slash.</summary>
    public string PublicOrigin { get; set; } = "";
    public bool AutoProvisionUsers { get; set; }
    /// <summary>Explicit development opt-in. Never disable for a remote provider.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}
