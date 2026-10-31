using System.Collections.Concurrent;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Microsoft.Extensions.Options;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// In-process sliding-window lockout, keyed by client address. cosine runs as a single
/// process, so an in-memory counter is sufficient and costs no round trip on the hot path
/// (every Subsonic request authenticates).
/// </summary>
public class AuthThrottle : IAuthThrottle
{
    // Bound on distinct tracked keys. Past this, expired entries are swept; the cap only
    // exists so a spray of spoofed sources cannot grow the map without limit.
    private const int PruneThreshold = 10_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly AuthOptions _options;
    private readonly TimeProvider _time;
    private readonly Telemetry.CosineMetrics? _metrics;

    public AuthThrottle(
        IOptions<AuthOptions> options,
        TimeProvider? timeProvider = null,
        Telemetry.CosineMetrics? metrics = null)
    {
        _options = options.Value;
        _time = timeProvider ?? TimeProvider.System;
        _metrics = metrics;
    }

    public bool IsLocked(string clientKey)
    {
        if (string.IsNullOrEmpty(clientKey) || !_entries.TryGetValue(clientKey, out var entry))
            return false;

        bool locked;
        lock (entry)
            locked = entry.LockedUntil > _time.GetUtcNow();

        if (locked)
            _metrics?.RecordAuthRejectedWhileLocked();
        return locked;
    }

    public void RecordFailure(string clientKey)
    {
        if (string.IsNullOrEmpty(clientKey))
            return;

        _metrics?.RecordAuthFailure();

        var now = _time.GetUtcNow();
        var entry = _entries.GetOrAdd(clientKey, _ => new Entry());

        var lockedOut = false;
        lock (entry)
        {
            // Failures older than the window no longer count towards the lockout.
            if (now - entry.WindowStart > TimeSpan.FromSeconds(_options.FailureWindowSeconds))
            {
                entry.WindowStart = now;
                entry.Failures = 0;
            }

            entry.Failures++;
            if (entry.Failures >= _options.MaxFailedAttempts)
            {
                entry.LockedUntil = now.AddSeconds(_options.LockoutSeconds);
                // Start a fresh window so the lockout does not immediately re-trigger on
                // the first attempt after it expires.
                entry.WindowStart = entry.LockedUntil;
                entry.Failures = 0;
                lockedOut = true;
            }
        }

        if (lockedOut)
            _metrics?.RecordAuthLockout();

        if (_entries.Count > PruneThreshold)
            Prune(now);
    }

    public void RecordSuccess(string clientKey)
    {
        if (!string.IsNullOrEmpty(clientKey))
            _entries.TryRemove(clientKey, out _);
    }

    private void Prune(DateTimeOffset now)
    {
        var staleBefore = now.AddSeconds(-Math.Max(_options.FailureWindowSeconds, _options.LockoutSeconds));
        foreach (var (key, entry) in _entries)
        {
            bool stale;
            lock (entry)
                stale = entry.LockedUntil <= now && entry.WindowStart < staleBefore;
            if (stale)
                _entries.TryRemove(key, out _);
        }
    }

    private sealed class Entry
    {
        public DateTimeOffset WindowStart;
        public DateTimeOffset LockedUntil;
        public int Failures;
    }
}
