using System.Security.Cryptography;
using System.Text;
using Cosine.Core.Entities;
using Cosine.Core.Options;
using Cosine.Core.Security;
using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cosine.Tests.Unit;

public class ExternalIdentityTests
{
    private const string Issuer = "https://identity.example.test/realms/music";
    private static IOptions<AuthOptions> OptionsFor(bool provision = true, bool localLogin = true) =>
        Options.Create(new AuthOptions { LocalLoginEnabled = localLogin, Oidc = new() { Enabled = true, AutoProvisionUsers = provision } });

    [Fact]
    public async Task ProvisioningCreatesNonAdminWithoutPassword_AndExistingIdentityIgnoresChangedClaims()
    {
        using var db = TestDbContextFactory.Create();
        var service = new ExternalIdentityService(db, OptionsFor());
        var user = await service.ResolveAsync(Issuer, "subject1", "Alice Smith", "alice@example.test");
        Assert.Null(user.PasswordHash);
        Assert.False(user.IsAdmin);
        Assert.Equal("AliceSmith", user.Username);
        Assert.Equal(64, user.SubsonicSecret.Length);
        var again = await service.ResolveAsync(Issuer, "subject1", "renamed", "new@example.test");
        Assert.Equal(user.Id, again.Id);
        Assert.Equal("AliceSmith", again.Username);
        Assert.Single(await db.Users.ToListAsync());
        Assert.Single(await db.ExternalIdentities.ToListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new UserService(db).AuthenticateAsync(user.Username, "anything"));
    }

    [Fact]
    public async Task UnknownIdentityIsDeniedByDefault_ButExplicitLinkPreservesLocalOwnershipAndPermissions()
    {
        using var db = TestDbContextFactory.Create();
        var users = new UserService(db);
        var owner = await users.CreateUserAsync("owner", "password", isAdmin: true);
        var service = new ExternalIdentityService(db, OptionsFor(provision: false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ResolveAsync(Issuer, "subject", "owner", null));
        var linked = await service.ResolveAsync(Issuer, "subject", "different", null, owner.Id);
        Assert.Equal(owner.Id, linked.Id);
        Assert.True(linked.IsAdmin);
        Assert.NotNull(linked.PasswordHash);
        var login = await service.ResolveAsync(Issuer, "subject", "ignored", null);
        Assert.Equal(owner.Id, login.Id);
    }

    [Theory]
    [InlineData("OWNER", null)]
    [InlineData("new-name", "OWNER@example.test")]
    public async Task MatchingUsernameOrEmailNeverAutomaticallyLinks(string username, string? email)
    {
        using var db = TestDbContextFactory.Create();
        await new UserService(db).CreateUserAsync("owner", "pw", "owner@example.test", true);
        var service = new ExternalIdentityService(db, OptionsFor());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ResolveAsync(Issuer, "attacker", username, email));
        Assert.Empty(await db.ExternalIdentities.ToListAsync());
        Assert.Single(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task IdentityCannotBeRelinkedToAnotherUser_AndIssuerIsPartOfTheIdentity()
    {
        using var db = TestDbContextFactory.Create();
        var service = new ExternalIdentityService(db, OptionsFor());
        var first = await service.ResolveAsync(Issuer, "subject", "first", null);
        var second = await service.ResolveAsync("https://other.example.test", "subject", "second", null);
        Assert.NotEqual(first.Id, second.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ResolveAsync(Issuer, "subject", null, null, second.Id));
    }

    [Fact]
    public async Task PasswordDisablementCoversLegacyLogin_ButSubsonicTokenStillWorks()
    {
        using var db = TestDbContextFactory.Create();
        var user = await new UserService(db).CreateUserAsync("alice", "pw");
        var users = new UserService(db, OptionsFor(localLogin: false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => users.AuthenticateAsync("alice", "pw"));
        var token = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(user.SubsonicSecret + "salt"))).ToLowerInvariant();
        Assert.Equal(user.Id, (await users.AuthenticateSubsonicAsync("alice", token, "salt"))!.Id);
    }

    [Fact]
    public async Task ExternalUserCanRotateCredentialsWithoutSettingPassword()
    {
        using var db = TestDbContextFactory.Create();
        var user = await new ExternalIdentityService(db, OptionsFor()).ResolveAsync(Issuer, "s", "alice", null);
        var users = new UserService(db);
        var original = user.SubsonicSecret;
        var stamp = SecurityStamp.For(user);
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.ResetPasswordAsync(user.Id, "new-password"));
        Assert.Null(user.PasswordHash);
        await users.RotateCredentialsAsync(user.Id);
        Assert.NotEqual(original, user.SubsonicSecret);
        Assert.False(SecurityStamp.Matches(user, stamp));
    }
}
