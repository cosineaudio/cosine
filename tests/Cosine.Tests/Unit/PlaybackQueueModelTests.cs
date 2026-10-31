using Cosine.Core.Entities;
using Cosine.Infrastructure.Playback;

namespace Cosine.Tests.Unit;

/// <summary>
/// The queue model is a port of <c>stores/player.ts</c> (plan §4.5), so these mirror the cases
/// in <c>stores/__tests__/player.test.ts</c>. If the two ever disagree, that is the divergence
/// plan §9 says to watch for - not a test to loosen.
/// </summary>
public class PlaybackQueueModelTests
{
    private static readonly Guid[] Five =
        [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

    /// <summary>Seeded so a failing shuffle case is reproducible rather than a flake.</summary>
    private static PlaybackQueueModel Queue(int seed = 1234, int take = 5)
    {
        var q = new PlaybackQueueModel(new Random(seed));
        q.SetQueue(Five.Take(take).ToList(), 0);
        return q;
    }

    [Fact]
    public void SetQueue_ClampsStartIndex_AndPlaysIt()
    {
        var q = new PlaybackQueueModel(new Random(1));
        var move = q.SetQueue(Five, 99);

        Assert.Equal(QueueAction.Play, move.Action);
        Assert.Equal(4, q.Index);
        Assert.Equal(Five[4], q.CurrentTrackId);
    }

    [Fact]
    public void SetQueue_Empty_Stops()
    {
        var q = Queue();
        var move = q.SetQueue([], 0);

        Assert.Equal(QueueAction.Stop, move.Action);
        Assert.Null(q.CurrentTrackId);
    }

    // ---- Sequential advance ----

    [Fact]
    public void Next_StopsAtTheEnd_WhenRepeatIsOff()
    {
        var q = Queue();
        q.PlayAt(4);

        Assert.Equal(QueueAction.Stop, q.Next().Action);
    }

    [Fact]
    public void Next_WrapsToStart_WhenRepeatIsAll()
    {
        var q = Queue();
        q.PlayAt(4);
        q.RepeatMode = PlaybackRepeatMode.All;

        var move = q.Next();

        Assert.Equal(QueueAction.Play, move.Action);
        Assert.Equal(0, move.Index);
    }

    [Fact]
    public void TrackEnded_RestartsTheSameTrack_UnderRepeatOne()
    {
        var q = Queue();
        q.PlayAt(2);
        q.RepeatMode = PlaybackRepeatMode.One;

        Assert.Equal(QueueAction.RestartCurrent, q.TrackEnded().Action);
        Assert.Equal(2, q.Index);
    }

    [Fact]
    public void Next_StillAdvances_UnderRepeatOne()
    {
        // Repeat-one is an end-of-track rule, not a "next is disabled" rule - same as the browser.
        var q = Queue();
        q.PlayAt(2);
        q.RepeatMode = PlaybackRepeatMode.One;

        var move = q.Next();

        Assert.Equal(QueueAction.Play, move.Action);
        Assert.Equal(3, move.Index);
    }

    // ---- Previous ----

    [Theory]
    [InlineData(4)]
    [InlineData(30)]
    public void Previous_RestartsTheTrack_PastTheThreshold(int seconds)
    {
        var q = Queue();
        q.PlayAt(3);

        var move = q.Previous(TimeSpan.FromSeconds(seconds));

        Assert.Equal(QueueAction.RestartCurrent, move.Action);
        Assert.Equal(3, q.Index);
    }

    [Fact]
    public void Previous_StepsBack_EarlyInTheTrack()
    {
        var q = Queue();
        q.PlayAt(3);

        var move = q.Previous(TimeSpan.FromSeconds(1));

        Assert.Equal(QueueAction.Play, move.Action);
        Assert.Equal(2, move.Index);
    }

    [Fact]
    public void Previous_RestartsAtTheStartOfTheQueue()
    {
        var q = Queue();

        Assert.Equal(QueueAction.RestartCurrent, q.Previous(TimeSpan.Zero).Action);
        Assert.Equal(0, q.Index);
    }

    // ---- Shuffle ----

    [Fact]
    public void Shuffle_PlaysEveryTrackOnceBeforeRepeatingAny()
    {
        var q = Queue();
        q.SetShuffled(true);

        var seen = new List<Guid> { q.CurrentTrackId!.Value };
        for (var i = 0; i < 4; i++)
        {
            var move = q.Next();
            Assert.Equal(QueueAction.Play, move.Action);
            seen.Add(q.CurrentTrackId!.Value);
        }

        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public void Shuffle_StopsAtTheEndOfThePass_WhenRepeatIsOff()
    {
        var q = Queue();
        q.SetShuffled(true);
        for (var i = 0; i < 4; i++) Assert.Equal(QueueAction.Play, q.Next().Action);

        Assert.Equal(QueueAction.Stop, q.Next().Action);
    }

    [Fact]
    public void Shuffle_DoesNotReplayTheTrackThatJustFinished_WhenThePassWraps()
    {
        var q = Queue();
        q.SetShuffled(true);
        q.RepeatMode = PlaybackRepeatMode.All;
        for (var i = 0; i < 4; i++) q.Next();

        var lastOfPass = q.CurrentTrackId;
        var move = q.Next();

        Assert.Equal(QueueAction.Play, move.Action);
        Assert.NotEqual(lastOfPass, q.CurrentTrackId);
    }

    [Fact]
    public void Shuffle_StepsBackThroughThePass_OnPrevious()
    {
        var q = Queue();
        q.SetShuffled(true);
        var first = q.CurrentTrackId;
        q.Next();
        var second = q.CurrentTrackId;

        var move = q.Previous(TimeSpan.Zero);

        Assert.Equal(QueueAction.Play, move.Action);
        Assert.Equal(first, q.CurrentTrackId);
        Assert.NotEqual(second, q.CurrentTrackId);
    }

    [Fact]
    public void Shuffle_KeepsThePassConsistent_WhenATrackIsPickedByHand()
    {
        var q = Queue();
        q.SetShuffled(true);
        q.PlayAt(3);

        // Picking by hand must resync the pointer, not desync it: the next advance still moves
        // to a track other than the one that was picked.
        var move = q.Next();

        Assert.Equal(QueueAction.Play, move.Action);
        Assert.NotEqual(3, q.Index);
    }

    [Fact]
    public void Shuffle_SingleTrackQueue_RepeatsItselfUnderRepeatAll()
    {
        var q = Queue(take: 1);
        q.SetShuffled(true);
        q.RepeatMode = PlaybackRepeatMode.All;

        var move = q.Next();

        Assert.Equal(QueueAction.Play, move.Action);
        Assert.Equal(0, move.Index);
    }

    // ---- Queue edits ----

    [Fact]
    public void Remove_AdjustsTheIndex_WhenRemovingBeforeTheCurrentTrack()
    {
        var q = Queue();
        q.PlayAt(3);

        var move = q.Remove(1);

        Assert.Equal(QueueAction.None, move.Action);
        Assert.Equal(2, q.Index);
        Assert.Equal(Five[3], q.CurrentTrackId);
    }

    [Fact]
    public void Remove_LeavesALaterRemovalAlone()
    {
        var q = Queue();
        q.PlayAt(1);

        var move = q.Remove(3);

        Assert.Equal(QueueAction.None, move.Action);
        Assert.Equal(1, q.Index);
        Assert.Equal(Five[1], q.CurrentTrackId);
    }

    [Fact]
    public void Remove_HandsOverToTheTrackThatShiftsIntoTheSlot()
    {
        var q = Queue();
        q.PlayAt(2);

        var move = q.Remove(2);

        Assert.Equal(QueueAction.Play, move.Action);
        Assert.Equal(2, q.Index);
        Assert.Equal(Five[3], q.CurrentTrackId);
    }

    [Fact]
    public void Remove_FallsBackToTheNewLastTrack_WhenThePlayingEntryWasLast()
    {
        var q = Queue();
        q.PlayAt(4);

        var move = q.Remove(4);

        Assert.Equal(QueueAction.Play, move.Action);
        Assert.Equal(3, q.Index);
        Assert.Equal(Five[3], q.CurrentTrackId);
    }

    [Fact]
    public void Remove_StopsWhenTheLastRemainingEntryGoes()
    {
        var q = Queue(take: 1);

        var move = q.Remove(0);

        Assert.Equal(QueueAction.Stop, move.Action);
        Assert.Null(q.CurrentTrackId);
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void Add_ToAnEmptyQueue_StartsPlaying()
    {
        var q = new PlaybackQueueModel(new Random(1));

        var move = q.Add(Five.Take(2).ToList(), null);

        Assert.Equal(QueueAction.Play, move.Action);
        Assert.Equal(0, move.Index);
    }

    [Fact]
    public void Add_DoesNotInterruptWhatIsPlaying()
    {
        var q = Queue();
        q.PlayAt(2);

        var move = q.Add([Guid.NewGuid()], null);

        Assert.Equal(QueueAction.None, move.Action);
        Assert.Equal(Five[2], q.CurrentTrackId);
    }

    [Fact]
    public void Add_InsertingAheadOfTheCurrentTrack_KeepsItPlaying()
    {
        var q = Queue();
        q.PlayAt(2);

        q.Add([Guid.NewGuid(), Guid.NewGuid()], insertAt: 0);

        Assert.Equal(4, q.Index);
        Assert.Equal(Five[2], q.CurrentTrackId);
    }

    [Fact]
    public void Version_ChangesOnlyOnQueueMutation()
    {
        var q = Queue();
        var v = q.Version;

        q.Next();
        q.Previous(TimeSpan.Zero);
        q.SetShuffled(true);
        q.RepeatMode = PlaybackRepeatMode.All;
        Assert.Equal(v, q.Version);

        q.Add([Guid.NewGuid()], null);
        Assert.NotEqual(v, q.Version);
    }

    // ---- Preload hint ----

    [Fact]
    public void Upcoming_IsNullAtTheEndOfAShufflePass_EvenUnderRepeat()
    {
        // The next permutation isn't drawn yet, so hinting would name a track we might not pick.
        var q = Queue();
        q.SetShuffled(true);
        q.RepeatMode = PlaybackRepeatMode.All;
        for (var i = 0; i < 4; i++) q.Next();

        Assert.Null(q.Upcoming());
    }

    [Fact]
    public void Upcoming_WrapsToTheFirstTrack_UnderSequentialRepeatAll()
    {
        var q = Queue();
        q.PlayAt(4);
        q.RepeatMode = PlaybackRepeatMode.All;

        Assert.Equal(Five[0], q.Upcoming());
    }

    [Fact]
    public void Upcoming_IsNullAtTheEnd_WhenRepeatIsOff()
    {
        var q = Queue();
        q.PlayAt(4);

        Assert.Null(q.Upcoming());
    }

    // ---- Reorder and jump ----
    //
    // These mirror the moveInQueue / playAt cases in player.test.ts. The queue panel's drag handle
    // and a click on a queue row are the same gesture whichever output is playing, so they have to
    // mean the same thing on both sides of the wire.

    [Fact]
    public void Move_ReordersAndKeepsTheCurrentTrackSelected()
    {
        var q = Queue();
        Assert.Equal(0, q.Index);

        var move = q.Move(0, 2);

        Assert.Equal(QueueAction.None, move.Action);
        Assert.Equal([Five[1], Five[2], Five[0], Five[3], Five[4]], q.Tracks);
        Assert.Equal(2, q.Index);
        Assert.Equal(Five[0], q.CurrentTrackId);
    }

    [Fact]
    public void Move_FollowsTheCurrentTrack_WhenSomethingMovesPastIt()
    {
        var q = Queue();
        q.PlayAt(2);

        q.Move(4, 0);

        Assert.Equal(3, q.Index);
        Assert.Equal(Five[2], q.CurrentTrackId);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 1)]
    [InlineData(1, -1)]
    public void Move_IgnoresNoOpAndOutOfRange(int from, int to)
    {
        var q = Queue();
        var version = q.Version;

        Assert.Equal(QueueAction.None, q.Move(from, to).Action);
        Assert.Equal(Five, q.Tracks);
        Assert.Equal(version, q.Version);
    }

    [Fact]
    public void PlayAt_JumpsToTheGivenPosition()
    {
        var q = Queue();
        var move = q.PlayAt(3);

        Assert.Equal(QueueAction.Play, move.Action);
        Assert.Equal(3, move.Index);
        Assert.Equal(Five[3], q.CurrentTrackId);
    }

    // A stale click from a client whose list has moved on must not silence the speaker.
    [Fact]
    public void PlayAt_IgnoresAPositionTheQueueDoesNotHave()
    {
        var q = Queue();
        q.PlayAt(2);

        var move = q.PlayAt(17);

        Assert.Equal(QueueAction.None, move.Action);
        Assert.Equal(2, q.Index);
    }

    // ---- Appending under shuffle, and trimming ----
    //
    // Both exist for the endless queue: it appends to a queue that is already being walked, and it
    // would otherwise grow for as long as it plays.

    /// <summary>
    /// Appending used to redraw the permutation, because the queue's length no longer matched it.
    /// Every track already heard this pass went back into play - so an auto-playing queue would
    /// have replayed its way through the last hour rather than moving on.
    /// </summary>
    [Fact]
    public void Add_UnderShuffle_DoesNotPutAlreadyPlayedTracksBackIntoThePass()
    {
        var q = Queue();
        q.SetShuffled(true);

        var heard = new List<Guid> { q.CurrentTrackId!.Value };
        for (var i = 0; i < 2; i++)
        {
            q.Next();
            heard.Add(q.CurrentTrackId!.Value);
        }

        q.Add([Guid.NewGuid(), Guid.NewGuid()], null);

        // The rest of the pass: the two remaining originals and the two new ones, in some order,
        // and nothing that has already been heard.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(QueueAction.Play, q.Next().Action);
            Assert.DoesNotContain(q.CurrentTrackId!.Value, heard);
            heard.Add(q.CurrentTrackId!.Value);
        }

        Assert.Equal(QueueAction.Stop, q.Next().Action);
    }

    [Fact]
    public void Add_UnderShuffle_PlaysTheAppendedTracksInThisPass()
    {
        var q = Queue(take: 2);
        q.SetShuffled(true);

        var appended = new[] { Guid.NewGuid(), Guid.NewGuid() };
        q.Add(appended, null);

        var rest = new List<Guid>();
        while (q.Next().Action == QueueAction.Play) rest.Add(q.CurrentTrackId!.Value);

        Assert.All(appended, id => Assert.Contains(id, rest));
    }

    [Fact]
    public void TrimHistory_DropsPlayedEntriesAndKeepsTheCurrentTrack()
    {
        var q = Queue();
        q.PlayAt(4);

        var dropped = q.TrimHistory(keepBehind: 1);

        Assert.Equal([Five[0], Five[1], Five[2]], dropped);
        Assert.Equal(2, q.Count);
        Assert.Equal(1, q.Index);
        Assert.Equal(Five[4], q.CurrentTrackId);
    }

    [Fact]
    public void TrimHistory_WithNothingToDrop_ChangesNothing()
    {
        var q = Queue();
        q.PlayAt(2);
        var version = q.Version;

        Assert.Empty(q.TrimHistory(keepBehind: 50));
        Assert.Equal(5, q.Count);
        Assert.Equal(version, q.Version);
    }

    /// <summary>
    /// Trimming renumbers every slot. The permutation has to be renumbered with it rather than
    /// redrawn, or the played tracks that survived the trim rejoin the pass - the same bug the
    /// append case exists for.
    /// </summary>
    [Fact]
    public void TrimHistory_UnderShuffle_KeepsTheRestOfThePassIntact()
    {
        var q = Queue();
        q.SetShuffled(true);

        var heard = new List<Guid> { q.CurrentTrackId!.Value };
        for (var i = 0; i < 2; i++)
        {
            q.Next();
            heard.Add(q.CurrentTrackId!.Value);
        }

        var current = q.CurrentTrackId;
        q.TrimHistory(keepBehind: 0);

        Assert.Equal(current, q.CurrentTrackId);
        while (q.Next().Action == QueueAction.Play)
            Assert.DoesNotContain(q.CurrentTrackId!.Value, heard);
    }

    [Fact]
    public void Restore_RebuildsWithoutAdvancing()
    {
        var q = new PlaybackQueueModel(new Random(7));
        q.Restore(Five, index: 3, shuffled: true, repeat: PlaybackRepeatMode.All);

        Assert.Equal(3, q.Index);
        Assert.Equal(Five[3], q.CurrentTrackId);
        Assert.True(q.Shuffled);
        Assert.Equal(PlaybackRepeatMode.All, q.RepeatMode);
    }
}
