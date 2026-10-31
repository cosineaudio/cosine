using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class UserServiceDbTests
{
    [Fact]
    public async Task CreateUser_SetsAllProperties()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var user = await service.CreateUserAsync("testuser", "password123", "test@example.com", isAdmin: true);

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("testuser", user.Username);
        Assert.Equal("test@example.com", user.Email);
        Assert.True(user.IsAdmin);
        Assert.NotNull(user.PasswordHash);
        Assert.NotEmpty(user.PasswordHash);
        Assert.NotNull(user.SubsonicSecret);
        Assert.Equal(64, user.SubsonicSecret.Length);
        Assert.NotEqual(default, user.CreatedAt);
    }

    [Fact]
    public async Task CreateUser_DuplicateUsername_Throws()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        await service.CreateUserAsync("dupe", "pw1");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateUserAsync("dupe", "pw2"));
    }

    // Authentication resolves usernames case-insensitively, so uniqueness has to as well:
    // "Admin" alongside "admin" makes every login resolve to whichever row comes back first.
    [Fact]
    public async Task CreateUser_UsernameDifferingOnlyByCase_Throws()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        await service.CreateUserAsync("admin", "pw1");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateUserAsync("Admin", "pw2"));

        Assert.Single(await service.GetUsersAsync());
    }

    [Fact]
    public async Task Authenticate_ValidCredentials_ReturnsUser()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        await service.CreateUserAsync("authuser", "correctpassword");

        var user = await service.AuthenticateAsync("authuser", "correctpassword");

        Assert.NotNull(user);
        Assert.Equal("authuser", user.Username);
    }

    [Fact]
    public async Task Authenticate_WrongPassword_Throws()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        await service.CreateUserAsync("authuser", "correctpassword");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.AuthenticateAsync("authuser", "wrongpassword"));
    }

    [Fact]
    public async Task Authenticate_NonExistentUser_Throws()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.AuthenticateAsync("nobody", "password"));
    }

    [Fact]
    public async Task Authenticate_UpdatesLastLoginAt()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var created = await service.CreateUserAsync("loginuser", "pw");
        Assert.Null(created.LastLoginAt);

        var authenticated = await service.AuthenticateAsync("loginuser", "pw");
        Assert.NotNull(authenticated.LastLoginAt);
    }

    [Fact]
    public async Task AuthenticateSubsonic_ValidToken_ReturnsUser()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var created = await service.CreateUserAsync("subuser", "pw");
        var secret = created.SubsonicSecret;

        var salt = "testsalt";
        var expectedToken = ComputeMd5ForTest(secret + salt);

        var result = await service.AuthenticateSubsonicAsync("subuser", expectedToken, salt);

        Assert.NotNull(result);
        Assert.Equal("subuser", result.Username);
    }

    [Fact]
    public async Task AuthenticateSubsonic_WrongToken_ReturnsNull()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        await service.CreateUserAsync("subuser", "pw");

        var result = await service.AuthenticateSubsonicAsync("subuser", "wrongtoken", "testsalt");

        Assert.Null(result);
    }

    [Fact]
    public async Task AuthenticateSubsonic_NonExistentUser_ReturnsNull()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var result = await service.AuthenticateSubsonicAsync("nobody", "token", "salt");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetUser_ReturnsUser()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var created = await service.CreateUserAsync("getuser", "pw");

        var retrieved = await service.GetUserAsync(created.Id);

        Assert.NotNull(retrieved);
        Assert.Equal(created.Id, retrieved.Id);
        Assert.Equal("getuser", retrieved.Username);
    }

    [Fact]
    public async Task GetUser_NonExistent_ReturnsNull()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var result = await service.GetUserAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetUserByUsername_ReturnsUser()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        await service.CreateUserAsync("nameuser", "pw");

        var result = await service.GetUserByUsernameAsync("nameuser");

        Assert.NotNull(result);
        Assert.Equal("nameuser", result.Username);
    }

    [Fact]
    public async Task GetUserByUsername_NonExistent_ReturnsNull()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var result = await service.GetUserByUsernameAsync("nobody");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetUsers_ReturnsAllOrderedByUsername()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        await service.CreateUserAsync("zeta", "pw");
        await service.CreateUserAsync("alpha", "pw");
        await service.CreateUserAsync("gamma", "pw");

        var users = await service.GetUsersAsync();

        Assert.Equal(3, users.Count);
        Assert.Equal("alpha", users[0].Username);
        Assert.Equal("gamma", users[1].Username);
        Assert.Equal("zeta", users[2].Username);
    }

    [Fact]
    public async Task UpdateUser_ChangesEmailAndAdmin()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var user = await service.CreateUserAsync("updateuser", "pw");
        Assert.Null(user.Email);
        Assert.False(user.IsAdmin);

        await service.UpdateUserAsync(user.Id, email: "new@email.com", isAdmin: true);

        var updated = await service.GetUserAsync(user.Id);
        Assert.NotNull(updated);
        Assert.Equal("new@email.com", updated!.Email);
        Assert.True(updated.IsAdmin);
    }

    [Fact]
    public async Task UpdateUser_NonExistent_Throws()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateUserAsync(Guid.NewGuid(), email: "x@y.com"));
    }

    [Fact]
    public async Task UpdateUser_NullEmail_DoesNotChangeExisting()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var user = await service.CreateUserAsync("partial", "pw", email: "original@email.com");
        await service.UpdateUserAsync(user.Id, isAdmin: true);

        var updated = await service.GetUserAsync(user.Id);
        Assert.NotNull(updated);
        Assert.Equal("original@email.com", updated!.Email);
        Assert.True(updated.IsAdmin);
    }

    [Fact]
    public async Task DeleteUser_RemovesFromDatabase()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var user = await service.CreateUserAsync("deleteuser", "pw");

        await service.DeleteUserAsync(user.Id);

        var result = await service.GetUserAsync(user.Id);
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteUser_NonExistent_DoesNotThrow()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        await service.DeleteUserAsync(Guid.NewGuid());

        Assert.True(true);
    }

    [Fact]
    public async Task ChangePassword_UpdatesHash()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var user = await service.CreateUserAsync("pwuser", "oldpassword");
        var oldHash = user.PasswordHash;

        await service.ChangePasswordAsync(user.Id, "oldpassword", "newpassword");

        var updated = await service.GetUserAsync(user.Id);
        Assert.NotNull(updated);
        Assert.NotEqual(oldHash, updated!.PasswordHash);
    }

    [Fact]
    public async Task ChangePassword_NewPasswordWorks()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        await service.CreateUserAsync("pwuser2", "oldpassword");
        await service.ChangePasswordAsync((await service.GetUserByUsernameAsync("pwuser2"))!.Id, "oldpassword", "newpassword");

        var authenticated = await service.AuthenticateAsync("pwuser2", "newpassword");
        Assert.NotNull(authenticated);
    }

    [Fact]
    public async Task ChangePassword_NonExistentUser_Throws()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ChangePasswordAsync(Guid.NewGuid(), "old", "new"));
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_Throws()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var user = await service.CreateUserAsync("pwuser3", "oldpassword");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ChangePasswordAsync(user.Id, "notmypassword", "newpassword"));

        // The old password must still work.
        Assert.NotNull(await service.AuthenticateAsync("pwuser3", "oldpassword"));
    }

    [Fact]
    public async Task ResetPassword_SkipsCurrentPasswordCheck()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var user = await service.CreateUserAsync("resetuser", "oldpassword");

        await service.ResetPasswordAsync(user.Id, "newpassword");

        Assert.NotNull(await service.AuthenticateAsync("resetuser", "newpassword"));
    }

    [Fact]
    public async Task ResetPassword_NonExistentUser_Throws()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResetPasswordAsync(Guid.NewGuid(), "new"));
    }

    // --- Credential rotation (#9): a password change has to revoke the Subsonic secret too. ---
    // The secret is a standalone password-equivalent handed out at /auth/profile, so leaving it
    // in place meant a leaked one survived every password reset forever.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PasswordChange_RotatesTheSubsonicSecret(bool selfService)
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var user = await service.CreateUserAsync("rotateuser", "oldpassword");
        var leakedSecret = user.SubsonicSecret;

        if (selfService)
            await service.ChangePasswordAsync(user.Id, "oldpassword", "newpassword");
        else
            await service.ResetPasswordAsync(user.Id, "newpassword");

        const string salt = "testsalt";
        var leakedToken = ComputeMd5ForTest(leakedSecret + salt);
        Assert.Null(await service.AuthenticateSubsonicAsync("rotateuser", leakedToken, salt));

        // ...and the freshly issued secret works, so the user is not locked out of /rest.
        var current = (await service.GetUserAsync(user.Id))!.SubsonicSecret;
        Assert.NotEqual(leakedSecret, current);
        Assert.NotNull(await service.AuthenticateSubsonicAsync(
            "rotateuser", ComputeMd5ForTest(current + salt), salt));
    }

    [Fact]
    public async Task RejectedPasswordChange_LeavesTheSubsonicSecretAlone()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var user = await service.CreateUserAsync("norotate", "oldpassword");
        var secret = user.SubsonicSecret;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ChangePasswordAsync(user.Id, "notmypassword", "newpassword"));

        Assert.Equal(secret, (await service.GetUserAsync(user.Id))!.SubsonicSecret);
    }

    [Fact]
    public async Task UpdateUser_DemotingLastAdmin_Throws()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var admin = await service.CreateUserAsync("onlyadmin", "pw", isAdmin: true);
        await service.CreateUserAsync("plain", "pw");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateUserAsync(admin.Id, isAdmin: false));

        Assert.True((await service.GetUserAsync(admin.Id))!.IsAdmin);
    }

    [Fact]
    public async Task UpdateUser_DemotingAdmin_AllowedWhenAnotherAdminExists()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var first = await service.CreateUserAsync("admin1", "pw", isAdmin: true);
        await service.CreateUserAsync("admin2", "pw", isAdmin: true);

        await service.UpdateUserAsync(first.Id, isAdmin: false);

        Assert.False((await service.GetUserAsync(first.Id))!.IsAdmin);
    }

    [Fact]
    public async Task DeleteUser_LastAdmin_Throws()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var admin = await service.CreateUserAsync("soleadmin", "pw", isAdmin: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteUserAsync(admin.Id));

        Assert.NotNull(await service.GetUserAsync(admin.Id));
    }

    [Fact]
    public async Task GetUsers_EmptyTable_ReturnsEmptyList()
    {
        using var db = TestDbContextFactory.Create();
        var service = new UserService(db);

        var users = await service.GetUsersAsync();

        Assert.Empty(users);
    }

    private static string ComputeMd5ForTest(string input)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
