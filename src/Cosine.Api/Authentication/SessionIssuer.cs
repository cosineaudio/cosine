using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Cosine.Core.Options;
using Cosine.Core.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Cosine.Api.Authentication;

public class SessionIssuer(IOptions<JwtOptions> jwt, IOptions<AuthOptions> auth)
{
    private readonly JwtOptions _jwtOptions = jwt.Value;
    private readonly AuthOptions _authOptions = auth.Value;

    public void Issue(HttpContext context, Core.Entities.User user, string method) =>
        SetJwtCookie(context, GenerateJwt(user, method));

    public string GenerateJwt(Core.Entities.User user, string method)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("IsAdmin", user.IsAdmin.ToString()),
            new Claim("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new Claim("amr", method),
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

    private void SetJwtCookie(HttpContext context, string token)
    {
        context.Response.Cookies.Append("cosine_auth", token, new CookieOptions
        {
            HttpOnly = true,
            Path = "/",
            // A Secure cookie is silently dropped by the browser on a plain-HTTP origin, which
            // makes login return 200 and leave the user logged out on any LAN deployment
            // (localhost is exempt, which is why this only bites off-box). Mirror the request
            // scheme by default; Auth__SecureCookie=true forces it on behind a TLS proxy.
            Secure = _authOptions.SecureCookie ?? (context.Request.IsHttps
                || (_authOptions.Oidc.Enabled && _authOptions.Oidc.PublicOrigin.StartsWith("https://", StringComparison.OrdinalIgnoreCase))),
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.ExpirationMinutes)
        });
    }
}
