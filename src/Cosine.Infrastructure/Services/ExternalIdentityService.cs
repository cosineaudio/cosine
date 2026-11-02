using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cosine.Core.Entities;
using Cosine.Core.Options;
using Cosine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Cosine.Infrastructure.Services;

/// <summary>Maps validated OIDC identities onto local ownership and permissions.</summary>
public class ExternalIdentityService(CosineDbContext db, IOptions<AuthOptions> auth)
{
    public async Task<HashSet<Guid>> GetLinkedUserIdsAsync(CancellationToken ct = default) =>
        (await db.ExternalIdentities.Select(x => x.UserId).Distinct().ToListAsync(ct)).ToHashSet();

    public Task<bool> IsLinkedAsync(Guid userId, CancellationToken ct = default) =>
        db.ExternalIdentities.AnyAsync(x => x.UserId == userId
            && (x.Issuer == auth.Value.Oidc.Authority || x.Issuer == auth.Value.Oidc.Authority.TrimEnd('/')), ct);

    public async Task<User> ResolveAsync(string issuer, string subject, string? username,
        string? email, Guid? linkUserId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(issuer) || issuer.Length > 512
            || string.IsNullOrWhiteSpace(subject) || subject.Length > 255)
            throw new UnauthorizedAccessException("Invalid external identity");

        var existing = await db.ExternalIdentities.Include(x => x.User)
            .SingleOrDefaultAsync(x => x.Issuer == issuer && x.Subject == subject, ct);
        if (existing is not null)
        {
            if (linkUserId.HasValue && existing.UserId != linkUserId)
                throw new UnauthorizedAccessException("This identity is already linked to another account");
            existing.User.LastLoginAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing.User;
        }

        User user;
        if (linkUserId.HasValue)
        {
            user = await db.Users.FindAsync([linkUserId.Value], ct)
                ?? throw new UnauthorizedAccessException("Account no longer exists");
        }
        else
        {
            if (!auth.Value.Oidc.AutoProvisionUsers)
                throw new UnauthorizedAccessException("Sign in with your local account and link it from Profile, or ask an administrator for access");

            var name = Regex.Replace(username ?? "", "[^a-zA-Z0-9._-]", "").Trim('.', '-', '_');
            if (name.Length == 0)
                name = "user-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issuer + "\n" + subject)))[..12].ToLowerInvariant();
            name = name[..Math.Min(name.Length, 80)];
            if (await db.Users.AnyAsync(x => x.Username.ToLower() == name.ToLower()
                || (email != null && x.Email != null && x.Email.ToLower() == email.ToLower()), ct))
                throw new UnauthorizedAccessException("An account with these details exists. Sign in locally and link it from Profile");

            user = new User
            {
                Username = name,
                Email = email is { Length: <= 256 } ? email : null,
                SubsonicSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                IsAdmin = false
            };
            db.Users.Add(user);
        }

        user.LastLoginAt = DateTimeOffset.UtcNow;
        db.ExternalIdentities.Add(new ExternalIdentity { User = user, Issuer = issuer, Subject = subject });
        try
        {
            // A single SaveChanges transaction creates the account and identity atomically.
            await db.SaveChangesAsync(ct);
            return user;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A competing callback may have provisioned this same subject or username.
            db.ChangeTracker.Clear();
            var winner = await db.ExternalIdentities.Include(x => x.User)
                .SingleOrDefaultAsync(x => x.Issuer == issuer && x.Subject == subject, ct);
            if (winner is not null && (!linkUserId.HasValue || winner.UserId == linkUserId))
                return winner.User;
            throw new UnauthorizedAccessException("Account details conflict with an existing account. Sign in locally to link it", ex);
        }
    }
}
