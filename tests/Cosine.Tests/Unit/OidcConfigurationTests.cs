using Cosine.Api.Authentication;
using Cosine.Api.Middleware;
using Cosine.Core.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Cosine.Tests.Unit;

public class OidcConfigurationTests
{
    [Fact]
    public void DefaultsAllowLocalLoginWithoutOidcConfiguration() =>
        Assert.True(new AuthOptionsValidator().Validate(null, new()).Succeeded);

    [Theory]
    [InlineData("http://cosine.example.test", true)]
    [InlineData("https://cosine.example.test/", true)]
    [InlineData("https://cosine.example.test/subpath", true)]
    [InlineData("https://cosine.example.test?query=1", true)]
    [InlineData("https://cosine.example.test", false)]
    [InlineData("http://localhost:8088", false)]
    public void ValidatesBrowserOrigin(string origin, bool fails)
    {
        var auth = Valid();
        auth.Oidc.PublicOrigin = origin;
        Assert.Equal(fails, new AuthOptionsValidator().Validate(null, auth).Failed);
    }

    [Fact]
    public void CannotDisableBothLoginMethods()
    {
        Assert.True(new AuthOptionsValidator().Validate(null, new() { LocalLoginEnabled = false }).Failed);
        var auth = Valid();
        auth.Oidc.ClientSecret = "";
        Assert.True(new AuthOptionsValidator().Validate(null, auth).Failed);
        auth = Valid();
        auth.Oidc.Authority = "http://identity.example.test";
        Assert.True(new AuthOptionsValidator().Validate(null, auth).Failed);
        auth.Oidc.RequireHttpsMetadata = false;
        Assert.True(new AuthOptionsValidator().Validate(null, auth).Succeeded);
    }

    [Theory]
    [InlineData("/albums", true)]
    [InlineData("//evil.test", false)]
    [InlineData("/\\evil.test", false)]
    [InlineData("https://evil.test", false)]
    [InlineData("/\n/evil.test", false)]
    [InlineData(null, false)]
    public void ReturnPathsMustStayLocal(string? path, bool allowed) =>
        Assert.Equal(allowed, OidcAuthentication.IsLocalPath(path));

    [Theory]
    [InlineData(true, "/auth/oidc/callback", true)]
    [InlineData(false, "/auth/oidc/callback", false)]
    [InlineData(true, "/auth/oidc/callback/extra", false)]
    [InlineData(true, "/auth/oidc/link", false)]
    [InlineData(true, "/auth/login", false)]
    public async Task CsrfExemptsOnlyEnabledExactCallback(bool enabled, string path, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        context.Response.Body = new MemoryStream();
        var ran = false;
        var middleware = new CsrfProtectionMiddleware(_ => { ran = true; return Task.CompletedTask; },
            auth: Options.Create(new AuthOptions { Oidc = new() { Enabled = enabled } }));
        await middleware.Invoke(context);
        Assert.Equal(allowed, ran);
        Assert.Equal(allowed ? 200 : 403, context.Response.StatusCode);
    }

    private static AuthOptions Valid() => new()
    {
        Oidc = new()
        {
            Enabled = true, Authority = "https://identity.example.test", ClientId = "cosine",
            ClientSecret = "secret", PublicOrigin = "https://cosine.example.test"
        }
    };
}
