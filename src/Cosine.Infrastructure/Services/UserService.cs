using System.Security.Cryptography;
using System.Text;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Konscious.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Infrastructure.Services;

public class UserService : IUserService
{
    private readonly Data.CosineDbContext _db;

    public UserService(Data.CosineDbContext db)
    {
        _db = db;
    }

    public async Task<User> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        // Usernames are matched case-insensitively: mobile keyboards routinely
        // auto-capitalize the first letter ("Admin" for "admin").
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), ct);
        if (user is null || !VerifyPassword(password, user.PasswordHash))
            throw new UnauthorizedAccessException("Invalid username or password");

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return user;
    }

    public async Task<User?> AuthenticateSubsonicAsync(string username, string token, string salt, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), ct);
        if (user is null) return null;

        var expectedToken = ComputeMd5(user.SubsonicSecret + salt);
        return string.Equals(token, expectedToken, StringComparison.OrdinalIgnoreCase) ? user : null;
    }

    public async Task<User> CreateUserAsync(string username, string password, string? email = null, bool isAdmin = false, CancellationToken ct = default)
    {
        // Uniqueness must match how authentication resolves a user, which is
        // case-insensitive (see AuthenticateAsync). A case-sensitive check here would
        // let "Admin" and "admin" coexist and every login would then resolve to
        // whichever row the database happened to return first. The unique index on
        // lower("Username") enforces the same rule against concurrent inserts.
        if (await _db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower(), ct))
            throw new InvalidOperationException("Username already exists");

        var user = new User
        {
            Username = username,
            PasswordHash = HashPassword(password),
            SubsonicSecret = GenerateSubsonicSecret(),
            Email = email,
            IsAdmin = isAdmin,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    public async Task UpdateUserAsync(Guid id, string? email = null, bool? isAdmin = null, CancellationToken ct = default)
    {
        var user = await _db.Users.FindAsync([id], ct);
        if (user is null) throw new InvalidOperationException("User not found");

        if (email is not null) user.Email = email;

        if (isAdmin.HasValue && isAdmin.Value != user.IsAdmin)
        {
            if (!isAdmin.Value && await IsLastAdminAsync(user, ct))
                throw new InvalidOperationException("Cannot remove the last administrator");
            user.IsAdmin = isAdmin.Value;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken ct = default)
    {
        var user = await _db.Users.FindAsync([id], ct);
        if (user is null) return;

        if (await IsLastAdminAsync(user, ct))
            throw new InvalidOperationException("Cannot delete the last administrator");

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Demoting or deleting the only remaining admin locks the instance out permanently,
    /// with no recovery path short of editing the database.
    /// </summary>
    private async Task<bool> IsLastAdminAsync(User user, CancellationToken ct) =>
        user.IsAdmin && !await _db.Users.AnyAsync(u => u.IsAdmin && u.Id != user.Id, ct);

    public Task<User?> GetUserAsync(Guid id, CancellationToken ct = default)
    {
        return _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    public Task<User?> GetUserByUsernameAsync(string username, CancellationToken ct = default)
    {
        return _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), ct);
    }

    public async Task<IReadOnlyList<User>> GetUsersAsync(CancellationToken ct = default)
    {
        return await _db.Users.OrderBy(u => u.Username).ToListAsync(ct);
    }

    public async Task ChangePasswordAsync(Guid id, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _db.Users.FindAsync([id], ct);
        if (user is null) throw new InvalidOperationException("User not found");

        if (!VerifyPassword(currentPassword, user.PasswordHash))
            throw new UnauthorizedAccessException("Current password is incorrect");

        user.PasswordHash = HashPassword(newPassword);
        RotateCredentials(user);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ResetPasswordAsync(Guid id, string newPassword, CancellationToken ct = default)
    {
        var user = await _db.Users.FindAsync([id], ct);
        if (user is null) throw new InvalidOperationException("User not found");

        user.PasswordHash = HashPassword(newPassword);
        RotateCredentials(user);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A password change has to revoke everything the old password could reach, or it is not a
    /// remediation. The Subsonic secret is a standalone password-equivalent (it is what token
    /// auth hashes, and it is handed out at <c>/auth/profile</c>), so a leaked one used to
    /// outlive every reset. Rotating it also moves the user's
    /// <see cref="Cosine.Core.Security.SecurityStamp"/>, which invalidates their issued JWTs.
    ///
    /// The cost is that Subsonic clients must be re-paired after a password change - that is
    /// the point, and it is documented in the README.
    /// </summary>
    private static void RotateCredentials(User user) =>
        user.SubsonicSecret = GenerateSubsonicSecret();

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password));
        argon2.Salt = salt;
        argon2.DegreeOfParallelism = 4;
        argon2.MemorySize = 65536; // 64 MB
        argon2.Iterations = 3;

        var hash = argon2.GetBytes(32);
        var saltBase64 = Convert.ToBase64String(salt);
        var hashBase64 = Convert.ToBase64String(hash);

        return $"{saltBase64}:{hashBase64}";
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split(':');
        if (parts.Length != 2) return false;

        var salt = Convert.FromBase64String(parts[0]);
        var expectedHash = Convert.FromBase64String(parts[1]);

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password));
        argon2.Salt = salt;
        argon2.DegreeOfParallelism = 4;
        argon2.MemorySize = 65536;
        argon2.Iterations = 3;

        var actualHash = argon2.GetBytes(32);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static string GenerateSubsonicSecret()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private static string ComputeMd5(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
