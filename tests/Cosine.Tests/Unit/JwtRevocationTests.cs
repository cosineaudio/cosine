using System.Security.Claims;
using Cosine.Api.Middleware;
using Cosine.Core.Entities;
using Cosine.Core.Security;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

/// <summary>
/// JWTs had no revocation path: a signature and a 24 h expiry were the only checks, so a token
/// kept working after the password behind it changed and after the account was deleted.
/// </summary>
public class JwtRevocationTests
{
    private static ClaimsPrincipal TokenFor(User user) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(SecurityStamp.ClaimType, SecurityStamp.For(user))
        ], "Test"));

    [Fact]
    public async Task FreshToken_IsAccepted()
    {
        using var db = TestDbContextFactory.Create();
        var users = new UserService(db);
        var user = await users.CreateUserAsync("alice", "pw");

        Assert.Null(await JwtRevocation.CheckAsync(TokenFor(user), users));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TokenIssuedBeforeAPasswordChange_IsRejected(bool selfService)
    {
        using var db = TestDbContextFactory.Create();
        var users = new UserService(db);
        var user = await users.CreateUserAsync("alice", "oldpassword");
        var issued = TokenFor(user);

        if (selfService)
            await users.ChangePasswordAsync(user.Id, "oldpassword", "newpassword");
        else
            await users.ResetPasswordAsync(user.Id, "newpassword");

        Assert.NotNull(await JwtRevocation.CheckAsync(issued, users));
    }

    [Fact]
    public async Task TokenForADeletedAccount_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var users = new UserService(db);
        await users.CreateUserAsync("admin", "pw", isAdmin: true);
        var user = await users.CreateUserAsync("alice", "pw");
        var issued = TokenFor(user);

        await users.DeleteUserAsync(user.Id);

        Assert.NotNull(await JwtRevocation.CheckAsync(issued, users));
    }

    [Fact]
    public async Task TokenWithoutAStamp_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var users = new UserService(db);
        var user = await users.CreateUserAsync("alice", "pw");

        // Tokens minted before this check existed carry no stamp; they must not be grandfathered.
        var stampless = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"));

        Assert.NotNull(await JwtRevocation.CheckAsync(stampless, users));
    }

    [Fact]
    public async Task TokenWithoutASubject_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var users = new UserService(db);

        Assert.NotNull(await JwtRevocation.CheckAsync(
            new ClaimsPrincipal(new ClaimsIdentity()), users));
    }

    [Fact]
    public void Stamp_DoesNotLeakTheSubsonicSecret()
    {
        var user = new User { SubsonicSecret = Guid.NewGuid().ToString("N") };

        var stamp = SecurityStamp.For(user);

        Assert.DoesNotContain(stamp, user.SubsonicSecret, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16, stamp.Length);
    }
}
