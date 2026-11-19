using System.Security.Cryptography;
using System.Text;
using Cosine.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Long-lived, per-device credentials for native clients (the Android app), stored in the
/// <see cref="ApiKey"/> table.
///
/// A device signs in once and receives a refresh token; it trades that for short-lived access
/// JWTs whenever one expires. The raw token is shown to the device exactly once and only its
/// SHA-256 is stored - the token is 256 bits of randomness, so a slow password hash buys
/// nothing.
///
/// The token is <c>{rowId:N}.{secret}</c>: the id makes lookup a primary-key read, the secret
/// is compared in constant time against the stored hash.
///
/// Tokens are deliberately not rotated on refresh. Rotation with reuse detection logs a device
/// out whenever a refresh response is lost on a flaky mobile connection, which is exactly the
/// "sign in again" experience this exists to remove. Instead a token is revoked by the user
/// (device list), by a password change or credential rotation (see
/// <see cref="UserService"/>), or after <see cref="IdleLifetime"/> without use.
/// </summary>
public class DeviceTokenService(Data.CosineDbContext db)
{
    /// <summary>A device that has not refreshed for this long must sign in again.</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(180);

    /// <summary>Upper bound on devices per account, so a script cannot grow the table unbounded.</summary>
    public const int MaxDevicesPerUser = 50;

    public const int MaxDeviceNameLength = 256;

    public async Task<string> IssueAsync(User user, string? deviceName, CancellationToken ct = default)
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            KeyHash = Hash(secret),
            Name = NormaliseName(deviceName),
            CreatedAt = DateTimeOffset.UtcNow,
            LastUsedAt = DateTimeOffset.UtcNow,
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);

        // Evict the least recently used devices beyond the cap.
        var stale = await db.ApiKeys
            .Where(k => k.UserId == user.Id)
            .OrderByDescending(k => k.LastUsedAt)
            .Skip(MaxDevicesPerUser)
            .Select(k => k.Id)
            .ToListAsync(ct);
        if (stale.Count > 0)
            await db.ApiKeys.Where(k => stale.Contains(k.Id)).ExecuteDeleteAsync(ct);

        return $"{key.Id:N}.{secret}";
    }

    /// <summary>
    /// Resolves a refresh token to its user, recording the use. Returns null for an unknown,
    /// revoked, malformed or idle-expired token.
    /// </summary>
    public async Task<(User User, Guid DeviceId)?> RedeemAsync(string? token, CancellationToken ct = default)
    {
        var key = await FindAsync(token, ct);
        if (key is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        if (key.LastUsedAt is { } lastUsed && now - lastUsed > IdleLifetime)
        {
            db.ApiKeys.Remove(key);
            await db.SaveChangesAsync(ct);
            return null;
        }

        key.LastUsedAt = now;
        await db.SaveChangesAsync(ct);
        return (key.User, key.Id);
    }

    /// <summary>Revokes the device a token belongs to. Unknown tokens are a no-op.</summary>
    public async Task RevokeAsync(string? token, CancellationToken ct = default)
    {
        var key = await FindAsync(token, ct);
        if (key is null)
            return;
        db.ApiKeys.Remove(key);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(Guid userId, CancellationToken ct = default) =>
        await db.ApiKeys.AsNoTracking()
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.LastUsedAt)
            .ToListAsync(ct);

    /// <returns>Whether a device of <paramref name="userId"/> was removed.</returns>
    public async Task<bool> RevokeAsync(Guid userId, Guid deviceId, CancellationToken ct = default) =>
        await db.ApiKeys.Where(k => k.UserId == userId && k.Id == deviceId).ExecuteDeleteAsync(ct) > 0;

    private async Task<ApiKey?> FindAsync(string? token, CancellationToken ct)
    {
        if (!TryParse(token, out var id, out var secret))
            return null;

        var key = await db.ApiKeys.Include(k => k.User).FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null)
            return null;

        var expected = Encoding.ASCII.GetBytes(key.KeyHash);
        var actual = Encoding.ASCII.GetBytes(Hash(secret));
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? key : null;
    }

    private static bool TryParse(string? token, out Guid id, out string secret)
    {
        id = Guid.Empty;
        secret = string.Empty;
        if (string.IsNullOrEmpty(token) || token.Length > 128)
            return false;

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
            return false;
        if (!Guid.TryParseExact(token[..dot], "N", out id))
            return false;

        secret = token[(dot + 1)..];
        return true;
    }

    private static string? NormaliseName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length > MaxDeviceNameLength ? trimmed[..MaxDeviceNameLength] : trimmed;
    }

    private static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
