using System.Security.Claims;
using Cosine.Core.Options;
using Cosine.Core.Security;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Cosine.Api.Authentication;

public static class OidcAuthentication
{
    public const string Scheme = "oidc";
    public const string CallbackPath = "/auth/oidc/callback";
    public const string LinkBinding = "cosine.link";

    public static void AddCosineOidc(this AuthenticationBuilder builder, AuthOptions auth)
    {
        if (!auth.Oidc.Enabled) return;
        var oidc = auth.Oidc;
        var origin = new Uri(oidc.PublicOrigin);
        builder
            // Required sign-in target for the remote handler. OnTicketReceived handles the
            // response itself, issuing our existing JWT instead of an external cookie.
            .AddCookie("OidcCompletion")
            .AddOpenIdConnect(Scheme, options =>
            {
                options.SignInScheme = "OidcCompletion";
                options.Authority = oidc.Authority;
                options.ClientId = oidc.ClientId;
                options.ClientSecret = oidc.ClientSecret;
                options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
                options.CallbackPath = CallbackPath;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                options.MapInboundClaims = false;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(5);
                // Loopback HTTP is a deliberate local development exception. Production
                // correlation/nonce cookies retain the framework's Secure / SameSite=None.
                if (origin.Scheme == "http" && origin.IsLoopback)
                {
                    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.None;
                    options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                    options.NonceCookie.SecurePolicy = CookieSecurePolicy.None;
                    options.NonceCookie.SameSite = SameSiteMode.Lax;
                }
                options.Events = new OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider = context =>
                    {
                        context.ProtocolMessage.RedirectUri = oidc.PublicOrigin + CallbackPath;
                        if (context.Properties.Items.ContainsKey(LinkBinding))
                        {
                            context.ProtocolMessage.Prompt = "login";
                            context.ProtocolMessage.MaxAge = "0";
                        }
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        // Capture the issuer from the validated security token. Do not infer
                        // identity from a display claim or an unvalidated callback parameter.
                        context.Properties!.Items["cosine.issuer"] = context.SecurityToken.Issuer;
                        return Task.CompletedTask;
                    },
                    OnTicketReceived = CompleteAsync,
                    OnRemoteFailure = context =>
                    {
                        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Cosine.Oidc").LogWarning("OIDC authentication failed");
                        context.Response.Redirect(context.Properties?.Items.ContainsKey(LinkBinding) == true ? "/profile?authError=failed" : "/login?authError=failed");
                        context.HandleResponse();
                        return Task.CompletedTask;
                    },
                    OnAccessDenied = context =>
                    {
                        context.Response.Redirect(context.Properties?.Items.ContainsKey(LinkBinding) == true ? "/profile?authError=denied" : "/login?authError=denied");
                        context.HandleResponse();
                        return Task.CompletedTask;
                    }
                };
            });
    }

    private static async Task CompleteAsync(TicketReceivedContext context)
    {
        var services = context.HttpContext.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Cosine.Oidc");
        try
        {
            Guid? linkUserId = null;
            if (context.Properties!.Items.TryGetValue(LinkBinding, out var binding))
            {
                // Link completion must still be in the original authenticated browser session.
                // The stamp also rejects password changes/credential rotations during the flow.
                var local = await context.HttpContext.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
                var principal = local.Principal;
                var expected = principal?.FindFirstValue(ClaimTypes.NameIdentifier) + ":" + principal?.FindFirstValue(SecurityStamp.ClaimType);
                if (!local.Succeeded || binding != expected
                    || !Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
                    throw new UnauthorizedAccessException("The local session changed. Sign in and link again");
                linkUserId = id;
            }
            var claims = context.Principal!;
            var email = bool.TryParse(claims.FindFirstValue("email_verified"), out var verified) && verified
                ? claims.FindFirstValue("email") : null;
            var user = await services.GetRequiredService<ExternalIdentityService>().ResolveAsync(
                context.Properties.Items["cosine.issuer"]!, claims.FindFirstValue("sub")!,
                claims.FindFirstValue("preferred_username"), email, linkUserId,
                context.HttpContext.RequestAborted);
            services.GetRequiredService<SessionIssuer>().Issue(context.HttpContext, user, "oidc");
            logger.LogInformation("OIDC {Action} succeeded for user {UserId}", linkUserId.HasValue ? "link" : "login", user.Id);
            var destination = context.ReturnUri;
            // AuthenticationProperties is protected, but validate here as well as at entry.
            context.Response.Redirect(IsLocalPath(destination) ? destination! : "/");
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogInformation("OIDC sign-in rejected: {Reason}", ex.Message);
            // No provider-supplied text goes into the redirect or public error message.
            context.Response.Redirect(context.Properties?.Items.ContainsKey(LinkBinding) == true ? "/profile?authError=account" : "/login?authError=account");
        }
        context.HandleResponse();
    }

    public static bool IsLocalPath(string? path) => !string.IsNullOrEmpty(path)
        && path[0] == '/' && (path.Length == 1 || (path[1] != '/' && path[1] != '\\'))
        && !path.Any(char.IsControl);
}
