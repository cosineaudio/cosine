using Microsoft.Extensions.Options;

namespace Cosine.Core.Options;

public class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var errors = new List<string>();
        if (!options.LocalLoginEnabled && !options.Oidc.Enabled)
            errors.Add("Auth:LocalLoginEnabled cannot be false while OIDC is disabled");
        if (options.Oidc.Enabled)
        {
            var oidc = options.Oidc;
            if (!Uri.TryCreate(oidc.Authority, UriKind.Absolute, out var authority)
                || authority.Scheme is not ("https" or "http")
                || !string.IsNullOrEmpty(authority.Query) || !string.IsNullOrEmpty(authority.Fragment)
                || !string.IsNullOrEmpty(authority.UserInfo))
                errors.Add("Auth:Oidc:Authority must be an absolute HTTP(S) issuer URL");
            else if (oidc.RequireHttpsMetadata && authority.Scheme != "https")
                errors.Add("Auth:Oidc:Authority must use HTTPS (RequireHttpsMetadata=false is for development only)");
            if (string.IsNullOrWhiteSpace(oidc.ClientId)) errors.Add("Auth:Oidc:ClientId is required");
            if (string.IsNullOrWhiteSpace(oidc.ClientSecret)) errors.Add("Auth:Oidc:ClientSecret is required");
            if (string.IsNullOrWhiteSpace(oidc.DisplayName)) errors.Add("Auth:Oidc:DisplayName is required");
            if (!Uri.TryCreate(oidc.PublicOrigin, UriKind.Absolute, out var origin)
                || (origin.Scheme != "https" && !(origin.Scheme == "http" && origin.IsLoopback))
                || origin.AbsolutePath != "/" || oidc.PublicOrigin.EndsWith('/')
                || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment)
                || !string.IsNullOrEmpty(origin.UserInfo))
                errors.Add("Auth:Oidc:PublicOrigin must be an HTTPS origin without a path or trailing slash (HTTP loopback is allowed for development)");
            if (oidc.PublicOrigin.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && options.SecureCookie == false)
                errors.Add("Auth:SecureCookie cannot be false for an HTTPS OIDC origin");
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
