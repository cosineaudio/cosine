using Cosine.Core.Options;
using Cosine.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Cosine.Tests.Unit;

/// <summary>
/// Neither /auth/login nor the Subsonic handler had any failed-attempt counter, so password
/// auth - Argon2id at 64 MB - was both an unthrottled brute-force target and a cheap way for
/// an unauthenticated client to pin every core.
/// </summary>
public class AuthThrottleTests
{
    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (AuthThrottle Throttle, TestTime Time) Create(
        int maxAttempts = 3, int windowSeconds = 60, int lockoutSeconds = 120)
    {
        var time = new TestTime();
        var options = Options.Create(new AuthOptions
        {
            MaxFailedAttempts = maxAttempts,
            FailureWindowSeconds = windowSeconds,
            LockoutSeconds = lockoutSeconds
        });
        return (new AuthThrottle(options, time), time);
    }

    [Fact]
    public void IsLocked_BelowThreshold_IsFalse()
    {
        var (throttle, _) = Create();

        throttle.RecordFailure("ip|alice");
        throttle.RecordFailure("ip|alice");

        Assert.False(throttle.IsLocked("ip|alice"));
    }

    [Fact]
    public void IsLocked_AtThreshold_LocksThatKeyOnly()
    {
        var (throttle, _) = Create();

        for (var i = 0; i < 3; i++)
            throttle.RecordFailure("ip|alice");

        Assert.True(throttle.IsLocked("ip|alice"));
        Assert.False(throttle.IsLocked("ip|bob"));
        Assert.False(throttle.IsLocked("other-ip|alice"));
    }

    [Fact]
    public void IsLocked_AfterLockoutElapses_IsFalse()
    {
        var (throttle, time) = Create(lockoutSeconds: 120);

        for (var i = 0; i < 3; i++)
            throttle.RecordFailure("ip|alice");

        time.Now = time.Now.AddSeconds(119);
        Assert.True(throttle.IsLocked("ip|alice"));

        time.Now = time.Now.AddSeconds(2);
        Assert.False(throttle.IsLocked("ip|alice"));

        // The window restarts with the lockout, so a single later failure must not re-lock.
        throttle.RecordFailure("ip|alice");
        Assert.False(throttle.IsLocked("ip|alice"));
    }

    [Fact]
    public void RecordFailure_FailuresOlderThanTheWindow_DoNotAccumulate()
    {
        var (throttle, time) = Create(windowSeconds: 60);

        throttle.RecordFailure("ip|alice");
        throttle.RecordFailure("ip|alice");

        time.Now = time.Now.AddSeconds(61);

        throttle.RecordFailure("ip|alice");
        throttle.RecordFailure("ip|alice");

        Assert.False(throttle.IsLocked("ip|alice"));
    }

    [Fact]
    public void RecordSuccess_ClearsThePendingFailures()
    {
        var (throttle, _) = Create();

        throttle.RecordFailure("ip|alice");
        throttle.RecordFailure("ip|alice");
        throttle.RecordSuccess("ip|alice");
        throttle.RecordFailure("ip|alice");

        Assert.False(throttle.IsLocked("ip|alice"));
    }
}
