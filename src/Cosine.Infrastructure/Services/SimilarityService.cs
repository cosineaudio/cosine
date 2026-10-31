using Cosine.Core.Entities;
using Cosine.Infrastructure.Data;
using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Local artist/track similarity scored entirely from signals already in the DB:
/// shared genres (Jaccard over each artist's track genres, <see cref="TrackGenre"/>),
/// collaborations (<see cref="TrackArtist"/>), playlist co-occurrence, and popularity
/// (<see cref="Artist.StarCount"/>). No external services are contacted - this is
/// the offline baseline the deferred Last.fm/ListenBrainz cache would layer over.
/// </summary>
public class SimilarityService : ISimilarityService
{
    // A radio pulls popular tracks from up to this many similar artists.
    private const int RadioArtistFanout = 25;

    /// <summary>
    /// How many distinct artists from the recent seeds an auto-play batch blends. Three is enough
    /// for a queue that has drifted to continue from the drift rather than the start, while the
    /// fan-out below still stays inside <see cref="RadioArtistFanout"/> in total: every extra seed
    /// is another artist's whole track list loaded to rank it.
    /// </summary>
    private const int AutoPlaySeedArtists = 3;

    /// <summary>
    /// Similar artists per seed, however many seeds there are. Splitting the fan-out evenly is
    /// what keeps three seeds from costing three times one seed's queries.
    /// </summary>
    private const int MinAutoPlayFanout = 8;

    /// <summary>
    /// How many shared-genre artists are scored. One common genre ("Rock") used to make the
    /// candidate pool the whole artist table, and the follow-up query then pulled every
    /// (artist, genre) pair for all of them into memory. The pool is now the artists sharing the
    /// *most* genres with the seed, which is the same order Jaccard ranks by - the tail this
    /// drops could not have placed in a top-100 result anyway.
    /// </summary>
    private const int CandidatePoolSize = 500;

    /// <summary>
    /// Share of an auto-play batch filled by content-based similarity, after the seed artists'
    /// own budget. The rest still goes through the artist neighbourhood, because audio is blind
    /// to era, scene and lineage - the two halves fail in different directions, and a queue built
    /// from both drifts more sensibly than one built from either.
    /// </summary>
    private const double AudioShareOfAutoPlay = 0.4;

    private readonly Data.CosineDbContext _db;
    private readonly IAudioSimilarityService _audio;

    public SimilarityService(Data.CosineDbContext db, IAudioSimilarityService audio)
    {
        _db = db;
        _audio = audio;
    }

    /// <summary>
    /// The artists sharing the most genres with the seed, capped at <paramref name="poolSize"/>.
    /// The cap has to be applied in SQL - the point is not to materialise a row per artist of a
    /// library where one genre is on everything. Ordering by shared-genre count is the same
    /// direction Jaccard scores in, so the cut falls on the artists that could not have ranked.
    /// </summary>
    public static IQueryable<Guid> SharedGenreCandidatesQuery(
        IQueryable<TrackGenre> trackGenres, Guid artistId, IReadOnlyList<Guid> seedGenreIds, int poolSize)
    {
        return trackGenres
            .Where(tg => tg.Track.ArtistId != artistId && seedGenreIds.Contains(tg.GenreId))
            .Select(tg => new { tg.Track.ArtistId, tg.GenreId })
            .Distinct()
            .GroupBy(x => x.ArtistId)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(poolSize);
    }

    public async Task<IReadOnlyList<(Artist Artist, double Score)>> GetSimilarArtistsAsync(
        Guid artistId, int count, CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, 100);

        // Artist genres are derived from their tracks' genres (TrackGenre is what the
        // scanner actually populates; the ArtistGenre roll-up table is not written).
        var seedGenreIds = await _db.TrackGenres
            .Where(tg => tg.Track.ArtistId == artistId)
            .Select(tg => tg.GenreId)
            .Distinct()
            .ToListAsync(ct);

        // Genre overlap: full genre sets for every artist sharing ≥1 genre with the seed.
        var genreSets = new Dictionary<Guid, List<Guid>>();
        if (seedGenreIds.Count > 0)
        {
            var candidateIds = await SharedGenreCandidatesQuery(
                _db.TrackGenres, artistId, seedGenreIds, CandidatePoolSize).ToListAsync(ct);

            if (candidateIds.Count > 0)
            {
                var rows = await _db.TrackGenres
                    .Where(tg => candidateIds.Contains(tg.Track.ArtistId))
                    .Select(tg => new { ArtistId = tg.Track.ArtistId, tg.GenreId })
                    .Distinct()
                    .ToListAsync(ct);

                foreach (var row in rows)
                {
                    if (!genreSets.TryGetValue(row.ArtistId, out var set))
                        genreSets[row.ArtistId] = set = new List<Guid>();
                    set.Add(row.GenreId);
                }
            }
        }

        // Collaboration co-occurrence: other artists credited on the seed's tracks
        // (both the primary ArtistId and TrackArtist credits).
        var creditedTrackIds = await _db.TrackArtists
            .Where(ta => ta.ArtistId == artistId).Select(ta => ta.TrackId).ToListAsync(ct);
        var primaryTrackIds = await _db.Tracks
            .Where(t => t.ArtistId == artistId).Select(t => t.Id).ToListAsync(ct);
        var seedTrackIds = creditedTrackIds.Concat(primaryTrackIds).Distinct().ToList();

        var collab = seedTrackIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await _db.TrackArtists
                .Where(ta => seedTrackIds.Contains(ta.TrackId) && ta.ArtistId != artistId)
                .GroupBy(ta => ta.ArtistId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        // Playlist co-occurrence: artists whose tracks share a playlist with the seed's.
        var seedPlaylistIds = await _db.PlaylistTracks
            .Where(pt => pt.Track.ArtistId == artistId)
            .Select(pt => pt.PlaylistId)
            .Distinct()
            .ToListAsync(ct);

        var playlistCooccur = seedPlaylistIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await _db.PlaylistTracks
                .Where(pt => seedPlaylistIds.Contains(pt.PlaylistId) && pt.Track.ArtistId != artistId)
                .GroupBy(pt => pt.Track.ArtistId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var candidateSet = new HashSet<Guid>(genreSets.Keys);
        candidateSet.UnionWith(collab.Keys);
        candidateSet.UnionWith(playlistCooccur.Keys);
        candidateSet.Remove(artistId);
        if (candidateSet.Count == 0) return [];

        // Only artists with a record of their own. The collaboration signal above is built
        // from TrackArtist credits, so featured/guest artists - who exist as an Artist row
        // and nothing else - are its strongest hits by construction, and "similar artists"
        // filled up with names that have no art, no discography and nowhere to go. Dropping
        // them here rather than after ranking means the caller still gets `count` results.
        var artists = await _db.Artists
            .Where(a => candidateSet.Contains(a.Id) && a.Albums.Any())
            .ToDictionaryAsync(a => a.Id, ct);

        // What the candidates actually sound like. Absent for artists with no analysed tracks,
        // which GetValueOrDefault turns into a neutral zero - the same score they had before this
        // signal existed, rather than a penalty for not having been analysed yet.
        var audioScores = await _audio.GetArtistAudioScoresAsync(
            artistId, artists.Keys.ToList(), ct);

        var signals = candidateSet
            .Where(artists.ContainsKey)
            .Select(id => new SimilarityScoring.ArtistSignals(
                id,
                SimilarityScoring.Jaccard(seedGenreIds, genreSets.GetValueOrDefault(id) ?? (IReadOnlyCollection<Guid>)Array.Empty<Guid>()),
                collab.GetValueOrDefault(id),
                playlistCooccur.GetValueOrDefault(id),
                artists[id].StarCount,
                audioScores.GetValueOrDefault(id)));

        var ranked = SimilarityScoring.Rank(signals).Take(count).ToList();

        // Album counts for the final ranked set only (≤ count, so ≤100 artists) - the
        // similarity math above needs no album data, so there is no point loading it for
        // the full candidate pool. Album id stubs mirror MediaLibraryService.GetArtistsAsync
        // so ArtistID3.AlbumCount (which reads Artist.Albums.Count) is correct downstream.
        var rankedIds = ranked.Select(r => r.ArtistId).ToList();
        var albumStubs = (await _db.Albums
                .Where(al => rankedIds.Contains(al.ArtistId))
                .Select(al => new { al.ArtistId, al.Id })
                .ToListAsync(ct))
            .GroupBy(x => x.ArtistId)
            .ToDictionary(g => g.Key, g => g.Select(x => new Album { Id = x.Id }).ToList());

        foreach (var id in rankedIds)
            artists[id].Albums = albumStubs.GetValueOrDefault(id) ?? new List<Album>();

        return ranked
            .Select(r => (artists[r.ArtistId], r.Score))
            .ToList();
    }

    public async Task<IReadOnlyList<Track>> GetArtistRadioAsync(
        Guid seedArtistId, int count, CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, 500);

        var queue = new List<Track>();
        var seen = new HashSet<Guid>();

        void AddTracks(IEnumerable<Track> tracks)
        {
            foreach (var track in tracks)
            {
                if (queue.Count >= count) break;
                if (seen.Add(track.Id)) queue.Add(track);
            }
        }

        // Spread the queue across the seed artist and up to RadioArtistFanout similar ones. The
        // per-artist top songs used to be one pair of queries each - every track of the artist
        // plus a PlayHistory group-by - so a single getSimilarSongs2 was 25+ round trips over
        // most of the library. One batch covers all of them.
        var similar = await GetSimilarArtistsAsync(seedArtistId, RadioArtistFanout, ct);
        var topSongs = await GetTopSongsForArtistsAsync(
            [seedArtistId, .. similar.Select(s => s.Artist.Id)], count, null, ct);

        List<Track> TopSongs(Guid artistId, int take) =>
            topSongs.TryGetValue(artistId, out var t) ? t.Take(take).ToList() : [];

        // Up to ~30% of the queue is the seed artist's own top songs.
        AddTracks(TopSongs(seedArtistId, SimilarityScoring.SeedTrackBudget(count)));

        if (similar.Count > 0)
        {
            var remaining = Math.Max(0, count - queue.Count);
            var totalScore = similar.Sum(s => s.Score);
            foreach (var (artist, score) in similar)
            {
                if (queue.Count >= count) break;
                var share = totalScore > 0 ? score / totalScore : 1.0 / similar.Count;
                var take = Math.Max(1, (int)Math.Ceiling(remaining * share));
                AddTracks(TopSongs(artist.Id, take));
            }
        }

        // Small/obscure libraries may leave room; backfill with more seed tracks.
        if (queue.Count < count)
            AddTracks(TopSongs(seedArtistId, count));

        Shuffle(queue);
        return queue;
    }

    public async Task<IReadOnlyList<Track>> GetTopSongsAsync(
        Guid artistId, int count, CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, 500);
        var byArtist = await GetTopSongsForArtistsAsync([artistId], count, null, ct);
        return byArtist.GetValueOrDefault(artistId, []);
    }

    public async Task<IReadOnlyList<Track>> GetAutoPlayQueueAsync(
        IReadOnlyList<Guid> seedTrackIds, IReadOnlyCollection<Guid> excludeTrackIds, int count,
        CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, 500);
        if (seedTrackIds.Count == 0) return [];

        var seedArtists = await ResolveSeedArtistsAsync(seedTrackIds, ct);
        if (seedArtists.Count == 0) return [];

        // Every seed contributes its own artist and that artist's neighbourhood, weighted by how
        // recently it was heard. Scores are *summed* where two seeds reach the same artist: one
        // that is similar to two of the last three things you played is a better pick than one
        // similar to either alone, and this is the only place that can show up.
        var fanout = Math.Max(RadioArtistFanout / seedArtists.Count, MinAutoPlayFanout);
        var seedWeights = new Dictionary<Guid, double>();
        var similarWeights = new Dictionary<Guid, double>();

        for (var i = 0; i < seedArtists.Count; i++)
        {
            var seedId = seedArtists[i];
            // 1, ½, ⅓ - the track that just played matters most, and the one three back still counts.
            var recency = 1.0 / (i + 1);
            seedWeights[seedId] = seedWeights.GetValueOrDefault(seedId) + recency;

            foreach (var (artist, score) in await GetSimilarArtistsAsync(seedId, fanout, ct))
            {
                if (seedWeights.ContainsKey(artist.Id)) continue;
                similarWeights[artist.Id] = similarWeights.GetValueOrDefault(artist.Id) + recency * score;
            }
        }

        // A seed reached as a neighbour of an earlier seed is a seed, not a neighbour.
        foreach (var seedId in seedWeights.Keys) similarWeights.Remove(seedId);

        var exclude = excludeTrackIds as HashSet<Guid> ?? [.. excludeTrackIds];
        var topSongs = await GetTopSongsForArtistsAsync(
            [.. seedWeights.Keys, .. similarWeights.Keys], count, exclude, ct);

        var queue = new List<Track>();
        var seen = new HashSet<Guid>();

        int Fill(Guid artistId, int take, int ceiling)
        {
            if (!topSongs.TryGetValue(artistId, out var tracks)) return 0;
            var added = 0;
            foreach (var track in tracks.Take(take))
            {
                if (queue.Count >= ceiling) break;
                if (!seen.Add(track.Id)) continue;
                queue.Add(track);
                added++;
            }
            return added;
        }

        // Roughly a third of the batch stays with the artists actually being listened to, split
        // between them by recency - the same seed budget a single-artist radio uses.
        var seedBudget = Math.Min(count, SimilarityScoring.SeedTrackBudget(count));
        Distribute(seedWeights, seedBudget, seedBudget, guaranteeEach: true, Fill);

        // Content-based picks, chosen by what the recent tracks actually sound like rather than by
        // who made them. This is the one source that can cross artist and genre boundaries - it
        // will happily follow a quiet acoustic run into a quiet acoustic track by someone sharing
        // no tag, collaborator or playlist with anything played so far.
        var audioBudget = (int)Math.Round((count - queue.Count) * AudioShareOfAutoPlay);
        if (audioBudget > 0)
        {
            var audioTracks = await GetAudioSimilarTracksAsync(
                seedTrackIds, exclude, seen, audioBudget, ct);

            foreach (var track in audioTracks)
            {
                if (queue.Count >= count) break;
                if (seen.Add(track.Id)) queue.Add(track);
            }
        }

        // The rest goes to the neighbourhood, weighted by similarity. No guarantee here: there can
        // be dozens of neighbours and a slot each would flatten the ranking into a round-robin.
        Distribute(similarWeights, count - queue.Count, count, guaranteeEach: false, Fill);

        // A thin neighbourhood leaves room; the seeds' own deeper cuts fill it before we give up.
        foreach (var seedId in seedWeights.Keys) Fill(seedId, count, count);

        Shuffle(queue);
        return queue;
    }

    /// <summary>
    /// Hand each artist a share of <paramref name="budget"/> proportional to its weight, heaviest
    /// first. <paramref name="ceiling"/> is the total the queue may reach, so an artist whose
    /// share overruns it stops there rather than the next artist being skipped entirely.
    /// </summary>
    /// <param name="guaranteeEach">
    /// Hold a slot back for each artist still to come, so a heavy first share cannot use up a small
    /// budget and shut the rest out. Two seeds and a budget of two used to mean the older seed
    /// contributed nothing at all - a queue that had drifted from one artist to another then
    /// continued as though only the second had ever been played.
    /// </param>
    private static void Distribute(
        IReadOnlyDictionary<Guid, double> weights, int budget, int ceiling, bool guaranteeEach,
        Func<Guid, int, int, int> fill)
    {
        if (weights.Count == 0 || budget <= 0) return;

        var ordered = weights.OrderByDescending(w => w.Value).ToList();
        var total = ordered.Sum(w => w.Value);
        var taken = 0;

        for (var i = 0; i < ordered.Count; i++)
        {
            var room = budget - taken;
            if (room <= 0) return;
            if (guaranteeEach) room = Math.Max(1, room - (ordered.Count - i - 1));

            var share = total > 0 ? ordered[i].Value / total : 1.0 / ordered.Count;
            taken += fill(ordered[i].Key, Math.Clamp((int)Math.Ceiling(budget * share), 1, room), ceiling);
        }
    }

    /// <summary>
    /// Tracks that sound like the recent seeds, hydrated and ordered best-first.
    /// <para>
    /// Returns empty whenever content-based similarity cannot answer - no pgvector, nothing
    /// analysed yet, or seeds whose own files were never analysed. The caller's remaining budget
    /// then falls through to the artist neighbourhood exactly as it did before, so a library
    /// mid-analysis degrades smoothly instead of returning short queues.
    /// </para>
    /// </summary>
    private async Task<List<Track>> GetAudioSimilarTracksAsync(
        IReadOnlyList<Guid> seedTrackIds,
        IReadOnlyCollection<Guid> exclude,
        IReadOnlyCollection<Guid> alreadyQueued,
        int count,
        CancellationToken ct)
    {
        var excludeAll = new HashSet<Guid>(exclude);
        excludeAll.UnionWith(alreadyQueued);

        var ranked = await _audio.GetSimilarTracksAsync(seedTrackIds, excludeAll, count, ct);
        if (ranked.Count == 0) return [];

        var ids = ranked.Select(r => r.TrackId).ToList();
        var tracks = await _db.Tracks
            .Include(t => t.Album)
            .Include(t => t.Artist)
            .IncludeCredits()
            .Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        // Re-impose the similarity order the dictionary lookup lost.
        return ranked
            .Select(r => tracks.GetValueOrDefault(r.TrackId))
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();
    }

    /// <summary>
    /// The distinct artists behind the seed tracks, in the order they were heard and capped at
    /// <see cref="AutoPlaySeedArtists"/>. Unknown ids are skipped rather than failing the batch:
    /// a queue holding a track that has since been rescanned away should still continue.
    /// </summary>
    private async Task<List<Guid>> ResolveSeedArtistsAsync(
        IReadOnlyList<Guid> seedTrackIds, CancellationToken ct)
    {
        var ids = seedTrackIds.Distinct().ToList();
        var artistByTrack = await _db.Tracks
            .Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.ArtistId })
            .ToDictionaryAsync(x => x.Id, x => x.ArtistId, ct);

        var artists = new List<Guid>();
        foreach (var trackId in seedTrackIds)
        {
            if (!artistByTrack.TryGetValue(trackId, out var artistId)) continue;
            if (artists.Contains(artistId)) continue;
            artists.Add(artistId);
            if (artists.Count == AutoPlaySeedArtists) break;
        }

        return artists;
    }

    /// <summary>
    /// Top <paramref name="perArtist"/> songs for each of <paramref name="artistIds"/>, in two
    /// queries regardless of how many artists are asked for. Ranking is by play count, then stars,
    /// then disc/track order - the play counts are a single grouped query over the whole set.
    /// </summary>
    /// <param name="exclude">
    /// Tracks to leave out, applied <em>before</em> the per-artist cut so what is dropped is
    /// replaced rather than simply missing - which is what lets an auto-play queue that has
    /// already used an artist's top songs move on to the next ones down.
    /// <para>
    /// Filtered in memory rather than in SQL on purpose: the caller's exclusion list is its whole
    /// queue, and an <c>IN</c> clause of that length is a parameter-limit failure waiting to
    /// happen. The rows are being materialised for the ranking anyway.
    /// </para>
    /// </param>
    private async Task<Dictionary<Guid, List<Track>>> GetTopSongsForArtistsAsync(
        IReadOnlyCollection<Guid> artistIds, int perArtist, IReadOnlyCollection<Guid>? exclude,
        CancellationToken ct)
    {
        perArtist = Math.Clamp(perArtist, 1, 500);
        if (artistIds.Count == 0) return [];

        var ids = artistIds.Distinct().ToList();

        var tracks = await _db.Tracks
            .Include(t => t.Album)
            .Include(t => t.Artist)
            .IncludeCredits()
            .Where(t => ids.Contains(t.ArtistId))
            .ToListAsync(ct);

        if (exclude is { Count: > 0 })
        {
            var excluded = exclude as HashSet<Guid> ?? [.. exclude];
            tracks = tracks.Where(t => !excluded.Contains(t.Id)).ToList();
        }

        if (tracks.Count == 0) return [];

        var trackIds = tracks.Select(t => t.Id).ToList();
        var playCounts = await _db.PlayHistory
            .Where(ph => trackIds.Contains(ph.TrackId))
            .GroupBy(ph => ph.TrackId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return tracks
            .GroupBy(t => t.ArtistId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderByDescending(t => playCounts.GetValueOrDefault(t.Id))
                    .ThenByDescending(t => t.StarCount)
                    .ThenBy(t => t.DiscNumber ?? 0)
                    .ThenBy(t => t.TrackNumber ?? 0)
                    .Take(perArtist)
                    .ToList());
    }

    // Fisher-Yates. Random.Shared is fine here - radio order is intentionally
    // non-deterministic and never persisted.
    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
