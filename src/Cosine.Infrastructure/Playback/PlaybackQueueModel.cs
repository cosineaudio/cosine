using Cosine.Core.Entities;

namespace Cosine.Infrastructure.Playback;

/// <summary>What the caller should do after a queue operation.</summary>
public enum QueueAction
{
    /// <summary>
    /// Whatever is playing carries on. Distinct from <see cref="Stop"/>: a queue edit that
    /// doesn't touch the playing track must not interrupt it.
    /// </summary>
    None,

    /// <summary>Play the track now at <see cref="QueueMove.Index"/>.</summary>
    Play,

    /// <summary>Nothing left to play - empty queue, or the pass ended with repeat off.</summary>
    Stop,

    /// <summary>Stay on the current track and restart it from zero.</summary>
    RestartCurrent,
}

/// <param name="Index">Only meaningful when <see cref="QueueAction.Play"/>.</param>
public readonly record struct QueueMove(QueueAction Action, int Index)
{
    public static readonly QueueMove None = new(QueueAction.None, -1);
    public static readonly QueueMove Stop = new(QueueAction.Stop, -1);
    public static readonly QueueMove RestartCurrent = new(QueueAction.RestartCurrent, -1);
    public static QueueMove Play(int index) => new(QueueAction.Play, index);
}

/// <summary>
/// The server-side queue: order, index, shuffle permutation and repeat. Pure - no database, no
/// device, no clock - so the advance rules can be tested directly.
/// <para>
/// This is a deliberate port of <c>stores/player.ts</c> rather than a rewrite (plan §4.5). The
/// browser's version already handles the awkward cases - no immediate repeat across a reshuffle,
/// pointer resync when the queue mutates underneath, "previous" meaning restart after three
/// seconds - and re-deriving them would have re-introduced the bugs those rules exist to fix.
/// The duplication is recorded as accepted debt in plan §9.
/// </para>
/// </summary>
public sealed class PlaybackQueueModel
{
    /// <summary>
    /// Matches <c>player.ts</c>: past this point "previous" restarts the current track instead
    /// of stepping back, which is what every other music player does.
    /// </summary>
    public static readonly TimeSpan PreviousRestartThreshold = TimeSpan.FromSeconds(3);

    private readonly List<Guid> _tracks = [];
    private readonly Random _rng;

    /// <summary>A permutation of queue indices, current track first. Empty while shuffle is off.</summary>
    private List<int> _shuffleOrder = [];

    /// <summary>Position of the playing track within <see cref="_shuffleOrder"/>.</summary>
    private int _shufflePos;

    /// <param name="rng">
    /// Seedable so shuffle behaviour is reproducible in tests. Not a test double - a shuffle
    /// with no injectable source of randomness is simply untestable.
    /// </param>
    public PlaybackQueueModel(Random? rng = null) => _rng = rng ?? Random.Shared;

    public IReadOnlyList<Guid> Tracks => _tracks;
    public int Count => _tracks.Count;
    public int Index { get; private set; }
    public bool Shuffled { get; private set; }
    public PlaybackRepeatMode RepeatMode { get; set; } = PlaybackRepeatMode.Off;

    /// <summary>
    /// Bumped by every mutation of the track list. The hub sends the queue only when this
    /// changes, so clients aren't re-diffing an unchanged list once a second (plan §4.8 item 3).
    /// </summary>
    public int Version { get; private set; }

    public Guid? CurrentTrackId =>
        Index >= 0 && Index < _tracks.Count ? _tracks[Index] : null;

    // ---- Shuffle ----
    //
    // Shuffle picked a fresh random index on every advance once, which could repeat the track
    // that just played and replayed tracks long before the queue was exhausted. Instead the queue
    // is walked in a random permutation: every track plays once per pass.

    private void BuildShuffleOrder(int startIndex)
    {
        var order = new List<int>(_tracks.Count);
        for (var i = 0; i < _tracks.Count; i++) order.Add(i);

        for (var i = order.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        // The playing track leads the permutation, so the first advance moves off it.
        var at = order.IndexOf(startIndex);
        if (at > 0) (order[0], order[at]) = (order[at], order[0]);

        _shuffleOrder = order;
        _shufflePos = 0;
    }

    /// <summary>
    /// Point <see cref="_shufflePos"/> at the playing track, rebuilding the permutation if the
    /// queue changed underneath it. Picking a track by hand keeps the pass consistent instead of
    /// desyncing the pointer.
    /// </summary>
    private void SyncShufflePointer()
    {
        if (_shuffleOrder.Count != _tracks.Count)
        {
            BuildShuffleOrder(Index);
            return;
        }

        var at = _shuffleOrder.IndexOf(Index);
        if (at == -1) BuildShuffleOrder(Index);
        else _shufflePos = at;
    }

    /// <summary>
    /// Fold newly appended slots into the current permutation rather than redrawing it. A redraw
    /// puts every track already heard this pass back in play, which is the one thing an endless
    /// queue must not do - it is the difference between a radio and a loop.
    /// <para>
    /// The new slots land at random positions <em>after</em> the pointer, so they belong to the
    /// rest of this pass without displacing what has already been decided.
    /// </para>
    /// </summary>
    private void ExtendShuffleOrder(int firstIndex, int addedCount)
    {
        // The permutation described a different queue than the one being appended to; there is
        // nothing to preserve and a redraw is the honest repair.
        if (_shuffleOrder.Count != firstIndex)
        {
            BuildShuffleOrder(Index);
            return;
        }

        for (var i = 0; i < addedCount; i++)
        {
            var at = _shufflePos + 1 + _rng.Next(_shuffleOrder.Count - _shufflePos);
            _shuffleOrder.Insert(at, firstIndex + i);
        }
    }

    /// <summary>The next index in the permutation, or null when playback should stop.</summary>
    private int? NextShuffleIndex()
    {
        SyncShufflePointer();
        if (_shufflePos + 1 < _shuffleOrder.Count) return _shuffleOrder[_shufflePos + 1];
        if (RepeatMode == PlaybackRepeatMode.Off) return null;

        // The pass is over: reshuffle and resume at the second entry, so the track that just
        // finished doesn't immediately repeat as the first of the new pass.
        BuildShuffleOrder(Index);
        return _shuffleOrder.Count > 1 ? _shuffleOrder[1] : _shuffleOrder[0];
    }

    /// <summary>
    /// The track that would play next, for the gapless preload hint. Null at the end of a shuffle
    /// pass even under repeat, because the next permutation isn't drawn yet - telling a device to
    /// prebuffer a track we might not actually choose would be worse than not hinting at all.
    /// <para>
    /// Null under repeat-one for the same reason, and it is not merely a wasted hint: a renderer
    /// acts on the next-URI by itself, so hinting the following track under repeat-one is the
    /// device being told to do the opposite of what the mode says.
    /// </para>
    /// </summary>
    public Guid? Upcoming()
    {
        if (_tracks.Count == 0) return null;
        if (RepeatMode == PlaybackRepeatMode.One) return null;

        if (Shuffled)
        {
            SyncShufflePointer();
            var at = _shufflePos + 1;
            return at < _shuffleOrder.Count ? _tracks[_shuffleOrder[at]] : null;
        }

        var nextIdx = Index + 1;
        if (nextIdx >= _tracks.Count)
            return RepeatMode == PlaybackRepeatMode.All ? _tracks.FirstOrDefault() : null;

        return _tracks[nextIdx];
    }

    // ---- Mutation ----

    public QueueMove SetQueue(IReadOnlyList<Guid> trackIds, int startIndex)
    {
        _tracks.Clear();
        _tracks.AddRange(trackIds);
        Version++;

        if (_tracks.Count == 0)
        {
            Index = 0;
            _shuffleOrder = [];
            return QueueMove.Stop;
        }

        Index = Math.Clamp(startIndex, 0, _tracks.Count - 1);
        if (Shuffled) BuildShuffleOrder(Index);
        return QueueMove.Play(Index);
    }

    /// <param name="insertAt">Null appends; otherwise inserts before that position.</param>
    /// <returns>
    /// <see cref="QueueAction.Play"/> only when the queue was empty and there is now something
    /// to start; otherwise <see cref="QueueAction.None"/> and the caller keeps playing.
    /// </returns>
    public QueueMove Add(IReadOnlyList<Guid> trackIds, int? insertAt)
    {
        if (trackIds.Count == 0) return QueueMove.None;

        var wasEmpty = _tracks.Count == 0;
        // Where the pass has got to, read before the queue grows underneath the permutation.
        if (Shuffled && !wasEmpty) SyncShufflePointer();

        var appendedAt = _tracks.Count;
        if (insertAt is { } at)
        {
            var clamped = Math.Clamp(at, 0, _tracks.Count);
            _tracks.InsertRange(clamped, trackIds);
            // Inserting ahead of the playing track shifts it along; the same track must keep playing.
            if (!wasEmpty && clamped <= Index) Index += trackIds.Count;
        }
        else
        {
            _tracks.AddRange(trackIds);
        }

        Version++;
        if (Shuffled && !wasEmpty)
        {
            // An append only adds slots; an insert renumbers the ones the permutation refers to,
            // so only the first can be folded into the pass rather than redrawn.
            if (insertAt is null) ExtendShuffleOrder(appendedAt, trackIds.Count);
            else BuildShuffleOrder(Index);
        }

        if (!wasEmpty) return QueueMove.None;

        Index = 0;
        if (Shuffled) BuildShuffleOrder(Index);
        return QueueMove.Play(Index);
    }

    /// <summary>
    /// Drop a queue entry. Removing the entry that is playing must not leave the index pointing
    /// at a track the queue no longer contains - the track that shifted into the slot takes over.
    /// </summary>
    /// <returns>
    /// <see cref="QueueAction.Play"/> when the removal displaced the playing track and something
    /// else must start; <see cref="QueueAction.Stop"/> when the queue is now empty; otherwise
    /// <see cref="QueueAction.None"/> and the current track is unaffected.
    /// </returns>
    public QueueMove Remove(int position)
    {
        if (position < 0 || position >= _tracks.Count) return QueueMove.None;

        var wasCurrent = position == Index;
        _tracks.RemoveAt(position);
        Version++;

        if (position < Index)
        {
            Index--;
            if (Shuffled) SyncShufflePointer();
            return QueueMove.None;
        }

        if (!wasCurrent)
        {
            if (Shuffled) SyncShufflePointer();
            return QueueMove.None;
        }

        if (_tracks.Count == 0)
        {
            Index = 0;
            _shuffleOrder = [];
            return QueueMove.Stop;
        }

        // The following track shifted into this slot - unless the removed one was last, in which
        // case the queue falls back to its new end.
        Index = Math.Min(position, _tracks.Count - 1);
        if (Shuffled) SyncShufflePointer();
        return QueueMove.Play(Index);
    }

    /// <summary>
    /// Reorder the queue, keeping the playing track selected - the queue panel's drag handle.
    /// Ported from <c>player.ts</c>'s <c>moveInQueue</c>, including the part that matters: the
    /// index follows the track it pointed at rather than staying on a slot whose occupant just
    /// changed.
    /// </summary>
    /// <returns>
    /// Always <see cref="QueueAction.None"/>. Reordering never interrupts playback; the track
    /// that is playing carries on wherever it has moved to.
    /// </returns>
    public QueueMove Move(int from, int to)
    {
        if (from < 0 || from >= _tracks.Count || to < 0 || to >= _tracks.Count || from == to)
            return QueueMove.None;

        var current = CurrentTrackId;
        var moved = _tracks[from];
        _tracks.RemoveAt(from);
        _tracks.Insert(to, moved);
        Version++;

        if (current is { } id)
        {
            var at = _tracks.IndexOf(id);
            if (at != -1) Index = at;
        }

        // The permutation indexes slots, and the slots have just been renumbered, so it no longer
        // describes the order the user is hearing. Redrawing it is the honest repair.
        if (Shuffled) BuildShuffleOrder(Index);

        return QueueMove.None;
    }

    /// <summary>
    /// Jump to a queue position and play it. An index the queue doesn't have is ignored rather
    /// than treated as the end of the queue: it means a client acted on a list that has since
    /// changed underneath it, and silencing the speaker is not the right answer to a stale click.
    /// </summary>
    public QueueMove PlayAt(int index)
    {
        if (index < 0 || index >= _tracks.Count) return QueueMove.None;
        Index = index;
        if (Shuffled) SyncShufflePointer();
        return QueueMove.Play(Index);
    }

    /// <summary>
    /// Drop played entries off the front, keeping at most <paramref name="keepBehind"/> of them.
    /// A queue that tops itself up grows for as long as it runs, and every entry is a row written
    /// back on the next persist and a line in the queue panel; the tail of a six-hour session is
    /// neither of those things usefully. The cost is that "previous" cannot walk back forever.
    /// </summary>
    /// <returns>The ids dropped, oldest first. Empty when nothing was.</returns>
    public IReadOnlyList<Guid> TrimHistory(int keepBehind)
    {
        var drop = Index - Math.Max(0, keepBehind);
        if (drop <= 0) return [];

        var removed = _tracks.GetRange(0, drop);
        _tracks.RemoveRange(0, drop);
        Index -= drop;
        Version++;

        // Trimming renumbers every slot, but unlike a reorder it does so predictably: drop the
        // entries that no longer exist and shift the rest down. Redrawing instead would put the
        // played tracks that survived the trim back into this pass, which is the bug the trim is
        // supposed to be helping with.
        if (Shuffled)
        {
            _shuffleOrder = [.. _shuffleOrder.Where(i => i >= drop).Select(i => i - drop)];
            SyncShufflePointer();
        }

        return removed;
    }

    public void SetShuffled(bool shuffled)
    {
        if (Shuffled == shuffled) return;
        Shuffled = shuffled;
        if (shuffled) BuildShuffleOrder(Index);
        else _shuffleOrder = [];
    }

    // ---- Advance ----

    public QueueMove Next()
    {
        if (_tracks.Count == 0) return QueueMove.Stop;

        if (Shuffled)
        {
            var nextIdx = NextShuffleIndex();
            if (nextIdx is null) return QueueMove.Stop;
            Index = nextIdx.Value;
            SyncShufflePointer();
            return QueueMove.Play(Index);
        }

        var sequential = (Index + 1) % _tracks.Count;
        if (sequential == 0 && RepeatMode == PlaybackRepeatMode.Off) return QueueMove.Stop;
        Index = sequential;
        return QueueMove.Play(Index);
    }

    /// <param name="currentPosition">
    /// How far into the current track playback is. Past
    /// <see cref="PreviousRestartThreshold"/> this restarts rather than stepping back.
    /// </param>
    public QueueMove Previous(TimeSpan currentPosition)
    {
        if (_tracks.Count == 0 || currentPosition > PreviousRestartThreshold)
            return QueueMove.RestartCurrent;

        if (Shuffled)
        {
            // Step back through the permutation, so "previous" undoes the last shuffled advance.
            SyncShufflePointer();
            if (_shufflePos <= 0) return QueueMove.RestartCurrent;
            Index = _shuffleOrder[_shufflePos - 1];
            SyncShufflePointer();
            return QueueMove.Play(Index);
        }

        var prevIdx = Index - 1;
        if (prevIdx < 0) return QueueMove.RestartCurrent;
        Index = prevIdx;
        return QueueMove.Play(Index);
    }

    /// <summary>
    /// What to do when the device reports the track finished. Repeat-one is handled here rather
    /// than in <see cref="Next"/>, so an explicit "next" still advances under repeat-one - same
    /// as the browser.
    /// </summary>
    public QueueMove TrackEnded()
    {
        if (RepeatMode == PlaybackRepeatMode.One && CurrentTrackId is not null)
            return QueueMove.RestartCurrent;

        return Next();
    }

    /// <summary>Restore persisted state on reconnect without re-running the advance rules.</summary>
    public void Restore(IReadOnlyList<Guid> trackIds, int index, bool shuffled, PlaybackRepeatMode repeat)
    {
        _tracks.Clear();
        _tracks.AddRange(trackIds);
        Index = _tracks.Count == 0 ? 0 : Math.Clamp(index, 0, _tracks.Count - 1);
        RepeatMode = repeat;
        Shuffled = shuffled;
        _shuffleOrder = [];
        if (shuffled) BuildShuffleOrder(Index);
        Version++;
    }
}
