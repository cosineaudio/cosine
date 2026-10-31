using Cosine.Core.Entities;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

/// <summary>
/// How the content-based signal folds into the existing metadata scoring - the seam between
/// <c>Cosine.Audio</c> and the radio, driven through <see cref="StubAudioSimilarityService"/> so
/// it can be exercised without pgvector.
/// </summary>
public class AudioSimilarityScoringTests
{
    private static Guid G(int n) => new($"{n:D8}-0000-0000-0000-000000000000");

    [Fact]
    public void An_artist_that_sounds_alike_outranks_one_that_only_shares_a_tag()
    {
        var soundsAlike = new SimilarityScoring.ArtistSignals(
            G(1), GenreJaccard: 0.2, CollabCount: 0, PlaylistCount: 0, StarCount: 0,
            AudioSimilarity: 1.0);

        var tagOnly = new SimilarityScoring.ArtistSignals(
            G(2), GenreJaccard: 0.5, CollabCount: 0, PlaylistCount: 0, StarCount: 0,
            AudioSimilarity: 0.0);

        var ranked = SimilarityScoring.Rank([soundsAlike, tagOnly]);

        Assert.Equal(G(1), ranked[0].ArtistId);
    }

    /// <summary>
    /// The signal is additive, not a replacement. An artist that shares tags <i>and</i> sounds
    /// alike must beat one that only does the latter - audio is blind to era, scene and lineage,
    /// which is exactly what the tag signal carries.
    /// </summary>
    [Fact]
    public void Audio_and_tag_evidence_compound()
    {
        var both = new SimilarityScoring.ArtistSignals(G(1), 1.0, 0, 0, 0, AudioSimilarity: 1.0);
        var audioOnly = new SimilarityScoring.ArtistSignals(G(2), 0.0, 0, 0, 0, AudioSimilarity: 1.0);
        var tagOnly = new SimilarityScoring.ArtistSignals(G(3), 1.0, 0, 0, 0, AudioSimilarity: 0.0);

        var ranked = SimilarityScoring.Rank([audioOnly, tagOnly, both]);

        Assert.Equal(G(1), ranked[0].ArtistId);
        Assert.True(ranked[0].Score > ranked[1].Score);
    }

    /// <summary>
    /// The compatibility guarantee: a library with no analysis - no pgvector, or analysis still
    /// running - ranks exactly as it did before the audio signal existed.
    /// </summary>
    [Fact]
    public void An_unanalysed_library_ranks_exactly_as_before()
    {
        SimilarityScoring.ArtistSignals[] withoutAudio =
        [
            new(G(1), 0.8, 3, 1, 10),
            new(G(2), 0.4, 1, 0, 50),
            new(G(3), 0.1, 0, 2, 5),
        ];

        // The same signals with the audio term explicitly zero, which is what an artist with no
        // analysed tracks contributes.
        var withZeroAudio = withoutAudio
            .Select(s => s with { AudioSimilarity = 0.0 })
            .ToList();

        var a = SimilarityScoring.Rank(withoutAudio);
        var b = SimilarityScoring.Rank(withZeroAudio);

        Assert.Equal(a.Select(x => x.ArtistId), b.Select(x => x.ArtistId));
        for (var i = 0; i < a.Count; i++) Assert.Equal(a[i].Score, b[i].Score, 10);
    }

    [Fact]
    public void Audio_similarity_carries_the_same_weight_as_genre()
    {
        Assert.Equal(SimilarityScoring.GenreWeight, SimilarityScoring.AudioWeight);
    }
}

public class AutoPlayAudioBlendTests
{
    private static Artist AddArtist(CosineDbContext db, string name)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = name, SortName = name.ToLower() };
        db.Artists.Add(artist);
        return artist;
    }

    private static Track AddTrack(CosineDbContext db, Artist artist, string title)
    {
        var album = new Album
        {
            Id = Guid.NewGuid(), Name = $"{title} Album", SortName = title.ToLower(), ArtistId = artist.Id,
        };
        db.Albums.Add(album);

        var track = new Track
        {
            Id = Guid.NewGuid(), Title = title, SortTitle = title.ToLower(),
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = $"{Guid.NewGuid()}.mp3",
            DurationMs = 180_000,
        };
        db.Tracks.Add(track);
        return track;
    }

    /// <summary>
    /// The point of the whole feature: a track that shares no artist, tag, collaborator or
    /// playlist with anything played can still be queued, purely because it sounds alike.
    /// Nothing in the metadata scoring could ever surface it.
    /// </summary>
    [Fact]
    public async Task A_track_with_no_metadata_connection_is_queued_when_it_sounds_alike()
    {
        using var db = TestDbContextFactory.Create();

        var played = AddArtist(db, "Played");
        var seedTrack = AddTrack(db, played, "Seed");

        var stranger = AddArtist(db, "Stranger");
        var soundsAlike = AddTrack(db, stranger, "Sounds Alike");
        await db.SaveChangesAsync();

        var audio = new StubAudioSimilarityService(
            available: true,
            tracks: [(soundsAlike.Id, 0.95)]);

        var service = new SimilarityService(db, audio);
        var queue = await service.GetAutoPlayQueueAsync([seedTrack.Id], [seedTrack.Id], 10);

        Assert.Contains(queue, t => t.Id == soundsAlike.Id);
    }

    [Fact]
    public async Task Without_audio_analysis_the_queue_is_built_from_metadata_alone()
    {
        using var db = TestDbContextFactory.Create();

        var played = AddArtist(db, "Played");
        var seedTrack = AddTrack(db, played, "Seed");
        var deeperCut = AddTrack(db, played, "Deeper Cut");

        var stranger = AddArtist(db, "Stranger");
        var unreachable = AddTrack(db, stranger, "Unreachable");
        await db.SaveChangesAsync();

        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);
        var queue = await service.GetAutoPlayQueueAsync([seedTrack.Id], [seedTrack.Id], 10);

        Assert.Contains(queue, t => t.Id == deeperCut.Id);
        Assert.DoesNotContain(queue, t => t.Id == unreachable.Id);
    }

    /// <summary>
    /// The caller's queue is passed through to the audio search, not filtered afterwards -
    /// otherwise topping up repeatedly would keep proposing the same handful of nearest
    /// neighbours and throwing them away, and the queue would run dry.
    /// </summary>
    [Fact]
    public async Task Tracks_the_caller_already_has_are_excluded_before_the_audio_search()
    {
        using var db = TestDbContextFactory.Create();

        var played = AddArtist(db, "Played");
        var seedTrack = AddTrack(db, played, "Seed");

        var stranger = AddArtist(db, "Stranger");
        var alreadyQueued = AddTrack(db, stranger, "Already Queued");
        var fresh = AddTrack(db, stranger, "Fresh");
        await db.SaveChangesAsync();

        var audio = new StubAudioSimilarityService(
            available: true,
            tracks: [(alreadyQueued.Id, 0.99), (fresh.Id, 0.90)]);

        var service = new SimilarityService(db, audio);
        var queue = await service.GetAutoPlayQueueAsync(
            [seedTrack.Id], [seedTrack.Id, alreadyQueued.Id], 10);

        Assert.NotNull(audio.LastExclusions);
        Assert.Contains(alreadyQueued.Id, audio.LastExclusions!);
        Assert.DoesNotContain(queue, t => t.Id == alreadyQueued.Id);
        Assert.Contains(queue, t => t.Id == fresh.Id);
    }

    [Fact]
    public async Task Audio_picks_never_crowd_out_the_whole_batch()
    {
        using var db = TestDbContextFactory.Create();

        var played = AddArtist(db, "Played");
        var seedTrack = AddTrack(db, played, "Seed");
        var ownDeeperCut = AddTrack(db, played, "Own Deeper Cut");

        var stranger = AddArtist(db, "Stranger");
        var audioPicks = Enumerable.Range(0, 20)
            .Select(i => AddTrack(db, stranger, $"Audio {i}"))
            .ToList();
        await db.SaveChangesAsync();

        var audio = new StubAudioSimilarityService(
            available: true,
            tracks: audioPicks.Select(t => (t.Id, 0.9)).ToList());

        var service = new SimilarityService(db, audio);
        var queue = await service.GetAutoPlayQueueAsync([seedTrack.Id], [seedTrack.Id], 10);

        // The seed artist's own budget is spent before audio gets a look in.
        Assert.Contains(queue, t => t.Id == ownDeeperCut.Id);
        Assert.True(queue.Count <= 10);
        Assert.Equal(queue.Select(t => t.Id).Distinct().Count(), queue.Count);
    }

    [Fact]
    public async Task An_empty_seed_list_still_returns_nothing()
    {
        using var db = TestDbContextFactory.Create();
        var service = new SimilarityService(db, new StubAudioSimilarityService(available: true));

        Assert.Empty(await service.GetAutoPlayQueueAsync([], [], 10));
    }
}
