using Cosine.Core.Interfaces;

namespace Cosine.Tests.Unit;

/// <summary>
/// Stands in for content-based similarity in the InMemory unit suite, which cannot host the
/// pgvector table at all (see <see cref="TestCosineDbContext"/>).
/// <para>
/// <see cref="Unavailable"/> is the important one: it is exactly what every consumer sees on a
/// server without pgvector, or before the first analysis has finished. The existing
/// <c>SimilarityServiceDbTests</c> run against it, so they double as the assertion that the
/// metadata-only path is unchanged by any of the audio work.
/// </para>
/// </summary>
public sealed class StubAudioSimilarityService : IAudioSimilarityService
{
    private readonly IReadOnlyList<(Guid TrackId, double Score)> _tracks;
    private readonly IReadOnlyDictionary<Guid, double> _artistScores;
    private readonly bool _available;

    public StubAudioSimilarityService(
        bool available = false,
        IReadOnlyList<(Guid TrackId, double Score)>? tracks = null,
        IReadOnlyDictionary<Guid, double>? artistScores = null)
    {
        _available = available;
        _tracks = tracks ?? [];
        _artistScores = artistScores ?? new Dictionary<Guid, double>();
    }

    /// <summary>No analysis available - the state every existing test implicitly assumed.</summary>
    public static StubAudioSimilarityService Unavailable => new();

    /// <summary>Records what the last call was asked to exclude, so tests can assert on it.</summary>
    public IReadOnlyCollection<Guid>? LastExclusions { get; private set; }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(_available);

    public Task<IReadOnlyList<(Guid TrackId, double Score)>> GetSimilarTracksAsync(
        IReadOnlyList<Guid> seedTrackIds,
        IReadOnlyCollection<Guid> excludeTrackIds,
        int count,
        CancellationToken ct = default)
    {
        LastExclusions = excludeTrackIds;

        if (!_available) return Task.FromResult<IReadOnlyList<(Guid, double)>>([]);

        var excluded = excludeTrackIds as HashSet<Guid> ?? [.. excludeTrackIds];
        IReadOnlyList<(Guid, double)> result = _tracks
            .Where(t => !excluded.Contains(t.TrackId))
            .Take(count)
            .ToList();

        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<Guid, double>> GetArtistAudioScoresAsync(
        Guid seedArtistId,
        IReadOnlyCollection<Guid> candidateArtistIds,
        CancellationToken ct = default)
        => Task.FromResult(_available
            ? _artistScores
            : new Dictionary<Guid, double>());
}
