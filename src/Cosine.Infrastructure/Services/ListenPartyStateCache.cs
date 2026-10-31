using System.Collections.Concurrent;
using Cosine.Core.Interfaces;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Singleton. See <see cref="IListenPartyStateCache"/> - live position never touches the
/// database; this is where it lives between the owner's publishes and a guest's snapshot fetch.
/// </summary>
public sealed class ListenPartyStateCache : IListenPartyStateCache
{
    private readonly ConcurrentDictionary<Guid, ListenPartyState> _states = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastDbTouch = new();

    public void Publish(Guid partyId, ListenPartyState state) => _states[partyId] = state;

    public ListenPartyState? Get(Guid partyId) =>
        _states.TryGetValue(partyId, out var state) ? state : null;

    public bool ShouldTouchDb(Guid partyId, TimeSpan interval)
    {
        var now = DateTimeOffset.UtcNow;
        var last = _lastDbTouch.GetOrAdd(partyId, now);

        // The first publish after startup seeds the entry and does not touch: creation/join
        // already wrote LastActivityAt, and a restart mid-party re-seeds within one tick.
        if (last == now) return false;

        if (now - last < interval) return false;
        return _lastDbTouch.TryUpdate(partyId, now, last);
    }

    public void Remove(Guid partyId)
    {
        _states.TryRemove(partyId, out _);
        _lastDbTouch.TryRemove(partyId, out _);
    }
}
