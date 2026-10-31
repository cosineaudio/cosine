using Cosine.Api.Controllers;
using Cosine.Core.Options;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cosine.Tests.Unit;

/// <summary>
/// The auth cookie was unconditionally <c>Secure</c> while the app serves plain HTTP, so any
/// LAN deployment without a TLS proxy had the browser silently drop it: login returned 200 and
/// the user stayed logged out. And nothing throttled failed logins.
/// </summary>
public class AuthControllerTests
{
    private static AuthController CreateController(
        TestServiceProvider sp, string scheme = "http", AuthOptions? authOptions = null)
    {
        var jwt = Options.Create(new JwtOptions
        {
            SigningKey = "test-signing-key-that-is-long-enough-for-hmac-sha256",
            ExpirationMinutes = 60
        });

        var controller = new AuthController(
            sp.Get<UserService>(),
            new AuthThrottle(Options.Create(authOptions ?? new AuthOptions())),
            jwt,
            Options.Create(authOptions ?? new AuthOptions()),
            new LoggerFactory().CreateLogger<AuthController>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = scheme;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static string SetCookieHeader(AuthController controller)
        => Assert.Single(controller.Response.Headers.SetCookie!)!;

    [Fact]
    public async Task Login_OverPlainHttp_IssuesACookieTheBrowserWillKeep()
    {
        using var sp = new TestServiceProvider();
        await sp.Get<UserService>().CreateUserAsync("alice", "pw");

        var controller = CreateController(sp, scheme: "http");

        Assert.IsType<OkObjectResult>(await controller.Login(new LoginRequest("alice", "pw"), default));
        Assert.DoesNotContain("secure", SetCookieHeader(controller), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_OverHttps_MarksTheCookieSecure()
    {
        using var sp = new TestServiceProvider();
        await sp.Get<UserService>().CreateUserAsync("alice", "pw");

        var controller = CreateController(sp, scheme: "https");

        await controller.Login(new LoginRequest("alice", "pw"), default);
        Assert.Contains("secure", SetCookieHeader(controller), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WithSecureCookieForced_MarksItSecureBehindAPlainHttpProxyHop()
    {
        using var sp = new TestServiceProvider();
        await sp.Get<UserService>().CreateUserAsync("alice", "pw");

        var controller = CreateController(sp, scheme: "http", new AuthOptions { SecureCookie = true });

        await controller.Login(new LoginRequest("alice", "pw"), default);
        Assert.Contains("secure", SetCookieHeader(controller), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_AfterRepeatedFailures_IsThrottled()
    {
        using var sp = new TestServiceProvider();
        await sp.Get<UserService>().CreateUserAsync("alice", "pw");

        var options = new AuthOptions { MaxFailedAttempts = 3, LockoutSeconds = 600 };
        var controller = CreateController(sp, authOptions: options);

        for (var i = 0; i < 3; i++)
            Assert.IsType<UnauthorizedObjectResult>(
                await controller.Login(new LoginRequest("alice", "wrong"), default));

        // The correct password is refused too - the lockout is what bounds the Argon2 work.
        var locked = Assert.IsType<ObjectResult>(await controller.Login(new LoginRequest("alice", "pw"), default));
        Assert.Equal(StatusCodes.Status429TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task Login_SuccessBetweenFailures_ResetsTheCounter()
    {
        using var sp = new TestServiceProvider();
        await sp.Get<UserService>().CreateUserAsync("alice", "pw");

        var options = new AuthOptions { MaxFailedAttempts = 3, LockoutSeconds = 600 };
        var controller = CreateController(sp, authOptions: options);

        await controller.Login(new LoginRequest("alice", "wrong"), default);
        await controller.Login(new LoginRequest("alice", "wrong"), default);
        await controller.Login(new LoginRequest("alice", "pw"), default);
        await controller.Login(new LoginRequest("alice", "wrong"), default);

        Assert.IsType<OkObjectResult>(await controller.Login(new LoginRequest("alice", "pw"), default));
    }
}
