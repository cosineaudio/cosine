using Cosine.Core.Entities;

namespace Cosine.Core.Interfaces;

public interface IUserService
{
    Task<User> AuthenticateAsync(string username, string password, CancellationToken ct = default);
    Task<User?> AuthenticateSubsonicAsync(string username, string token, string salt, CancellationToken ct = default);
    Task<User> CreateUserAsync(string username, string password, string? email = null, bool isAdmin = false, CancellationToken ct = default);
    Task UpdateUserAsync(Guid id, string? email = null, bool? isAdmin = null, CancellationToken ct = default);
    Task DeleteUserAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetUserAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetUserByUsernameAsync(string username, CancellationToken ct = default);
    Task<IReadOnlyList<User>> GetUsersAsync(CancellationToken ct = default);
    /// <summary>
    /// Self-service password change. Verifies <paramref name="currentPassword"/> and throws
    /// <see cref="UnauthorizedAccessException"/> if it does not match.
    /// </summary>
    Task ChangePasswordAsync(Guid id, string currentPassword, string newPassword, CancellationToken ct = default);

    /// <summary>Rotates the Subsonic credential and revokes all existing browser sessions.</summary>
    Task RotateCredentialsAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Administrative password reset for an account with a local password.
    /// Callers must have already authorized the request.
    /// </summary>
    Task ResetPasswordAsync(Guid id, string newPassword, CancellationToken ct = default);
}
