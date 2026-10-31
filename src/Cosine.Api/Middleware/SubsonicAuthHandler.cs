using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Cosine.Api.Middleware;

public class SubsonicAuthHandler : AuthenticationHandler<SubsonicAuthOptions>
{
    public const string SchemeName = "Subsonic";

    public SubsonicAuthHandler(
        IOptionsMonitor<SubsonicAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var (username, token, salt, password) = await ReadCredentialsAsync();

        if (string.IsNullOrEmpty(username))
        {
            return AuthenticateResult.NoResult();
        }

        var userService = Context.RequestServices.GetRequiredService<Cosine.Core.Interfaces.IUserService>();
        var throttle = Context.RequestServices.GetRequiredService<Cosine.Core.Interfaces.IAuthThrottle>();
        var throttleKey = AuthThrottleKey.For(Context, username);

        // Checked before any credential work: legacy `p=` auth runs Argon2id (64 MB) on every
        // request, so an unthrottled failure path is a CPU/memory DoS as well as a brute-force
        // surface. Clients see the usual error 40, not a 429 they would not understand.
        if (throttle.IsLocked(throttleKey))
        {
            Logger.LogWarning("Subsonic auth for '{Username}' rejected: too many failed attempts", username);
            return AuthenticateResult.Fail($"Too many failed authentication attempts for '{username}'");
        }

        AuthenticateResult result;
        if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(salt))
        {
            result = await HandleTokenAuthAsync(username, token, salt, userService);
        }
        else if (!string.IsNullOrEmpty(password))
        {
            result = await HandlePasswordAuthAsync(username, password, userService);
        }
        else
        {
            return AuthenticateResult.NoResult();
        }

        if (result.Succeeded)
            throttle.RecordSuccess(throttleKey);
        else
            throttle.RecordFailure(throttleKey);

        return result;
    }

    private async Task<(string? Username, string? Token, string? Salt, string? Password)> ReadCredentialsAsync()
    {
        var query = Request.Query;
        var username = query["u"].FirstOrDefault();
        var token = query["t"].FirstOrDefault();
        var salt = query["s"].FirstOrDefault();
        var password = query["p"].FirstOrDefault();

        // OpenSubsonic formPost clients send credentials in the form body instead of
        // the query string. ReadFormAsync caches the form, so MVC can still bind it.
        if (string.IsNullOrEmpty(username) && Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(Context.RequestAborted);
            username = form["u"].FirstOrDefault();
            token = form["t"].FirstOrDefault();
            salt = form["s"].FirstOrDefault();
            password = form["p"].FirstOrDefault();
        }

        // Values pasted into mobile clients often pick up stray whitespace.
        return (username?.Trim(), token?.Trim(), salt?.Trim(), password?.Trim());
    }

    private async Task<AuthenticateResult> HandleTokenAuthAsync(
        string username, string token, string salt,
        Cosine.Core.Interfaces.IUserService userService)
    {
        try
        {
            var user = await userService.AuthenticateSubsonicAsync(username, token, salt);

            if (user is null)
            {
                // A rejected credential is expected traffic (misconfigured client,
                // brute force) - Information keeps an audit trail without alarming.
                Logger.LogInformation("Subsonic token auth failed for '{Username}': invalid credentials", username);
                return AuthenticateResult.Fail($"Invalid Subsonic credentials for '{username}' (token auth)");
            }

            return AuthenticateResult.Success(CreateTicket(user));
        }
        catch (Exception ex)
        {
            // Unlike a bad password, this is an unexpected fault (DB down, bug) - without
            // a log it is invisible, presenting to the client as a plain wrong-password.
            Logger.LogWarning(ex, "Subsonic token auth errored for '{Username}'", username);
            return AuthenticateResult.Fail($"Subsonic authentication failed for '{username}' (token auth)");
        }
    }

    private async Task<AuthenticateResult> HandlePasswordAuthAsync(
        string username, string password,
        Cosine.Core.Interfaces.IUserService userService)
    {
        try
        {
            // Legacy Subsonic auth: password is sent either in clear text or hex-encoded
            // with an "enc:" prefix.
            var plainPassword = password.StartsWith("enc:", StringComparison.OrdinalIgnoreCase)
                ? System.Text.Encoding.UTF8.GetString(Convert.FromHexString(password[4..]))
                : password;

            var user = await userService.AuthenticateAsync(username, plainPassword);
            return AuthenticateResult.Success(CreateTicket(user));
        }
        catch (UnauthorizedAccessException)
        {
            // Expected: wrong username or password. Audited at Information.
            Logger.LogInformation("Subsonic password auth failed for '{Username}': invalid credentials", username);
            return AuthenticateResult.Fail($"Invalid Subsonic credentials for '{username}' (password auth)");
        }
        catch (Exception ex)
        {
            // Unexpected fault (e.g. malformed enc: hex, DB down) - surface it rather
            // than let it masquerade as a wrong password.
            Logger.LogWarning(ex, "Subsonic password auth errored for '{Username}'", username);
            return AuthenticateResult.Fail($"Invalid Subsonic credentials for '{username}' (password auth)");
        }
    }

    private static AuthenticationTicket CreateTicket(Cosine.Core.Entities.User user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("IsAdmin", user.IsAdmin.ToString())
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return new AuthenticationTicket(principal, SchemeName);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Subsonic clients expect HTTP 200 with a spec-formatted error body (code 40);
        // a real 401 reads as a broken server or a reverse proxy demanding HTTP auth.
        var wantsJson = SubsonicErrors.WantsJson(Request);
        Response.StatusCode = SubsonicErrors.HttpStatusFor(40);
        Response.ContentType = wantsJson ? "application/json; charset=utf-8" : "application/xml; charset=utf-8";

        const string message = "Wrong username or password.";
        await Response.WriteAsync(wantsJson
            ? SubsonicErrors.BuildJson(40, message)
            : SubsonicErrors.BuildXml(40, message));
    }
}

public class SubsonicAuthOptions : AuthenticationSchemeOptions
{
}
