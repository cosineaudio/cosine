namespace Cosine.Core.Interfaces;

/// <summary>
/// Failed-authentication throttle. Password verification is Argon2id at 64 MB / 3 iterations,
/// which makes every attempt expensive for the *server*, so an unthrottled login path is both a
/// brute-force surface and a CPU/memory DoS vector. Callers must consult
/// <see cref="IsLocked"/> before doing any hashing work.
/// </summary>
public interface IAuthThrottle
{
    /// <summary>True when <paramref name="clientKey"/> has failed too often and is in cooldown.</summary>
    bool IsLocked(string clientKey);

    /// <summary>Records a failed attempt; crossing the threshold starts the lockout.</summary>
    void RecordFailure(string clientKey);

    /// <summary>Clears the failure history for a client that has just authenticated.</summary>
    void RecordSuccess(string clientKey);
}
