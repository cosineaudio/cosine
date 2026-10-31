using System.Security.Cryptography;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;

namespace Cosine.Api;

/// <summary>
/// Creates the first admin account when the users table is empty. The account used to be
/// <c>admin</c>/<c>admin</c>: an instance reachable by anyone before its owner got around to
/// changing the password was trivially owned, and nothing ever prompted them to.
/// </summary>
public static class AdminBootstrap
{
    public const string Username = "admin";

    /// <summary>
    /// The configured password when one is set, otherwise a freshly generated one
    /// (the flag says which, so only a generated password is printed to the log).
    /// </summary>
    public static (string Password, bool Generated) ResolvePassword(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? (GeneratePassword(), true) : (configured, false);

    /// <summary>18 random bytes as 24 base64url characters - ~144 bits, and safe to paste.</summary>
    public static string GeneratePassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    /// <summary>
    /// Creates the bootstrap admin if there are no users at all. Returns whether it created one.
    /// </summary>
    public static async Task<bool> EnsureAdminAsync(
        IUserService users, AuthOptions auth, ILogger logger, CancellationToken ct = default)
    {
        var existing = await users.GetUsersAsync(ct);
        if (existing.Count > 0)
            return false;

        var (password, generated) = ResolvePassword(auth.BootstrapAdminPassword);
        await users.CreateUserAsync(Username, password, isAdmin: true, ct: ct);

        if (generated)
        {
            logger.LogWarning(
                "No users existed - created the bootstrap admin account.\n" +
                "    username: {Username}\n" +
                "    password: {Password}\n" +
                "This password is shown only here. Change it under Admin -> Users, or set " +
                "Auth__BootstrapAdminPassword before the first start to choose your own.",
                Username, password);
        }
        else
        {
            logger.LogWarning(
                "No users existed - created the bootstrap admin account '{Username}' with the "
                + "password from Auth__BootstrapAdminPassword.", Username);
        }

        return true;
    }
}
