using Cosine.Core.Interfaces;

namespace Cosine.Tests.Unit;

/// <summary>
/// In-memory <see cref="IRatingService"/> for controller tests. Records writes so the
/// star-decoupling can be asserted, and serves seeded values so the per-user overlay can be.
/// </summary>
internal sealed class FakeRatingService : IRatingService
{
    public List<(Guid ItemId, int Value)> Calls { get; } = [];

    /// <summary>Ratings the overlay will find, keyed by (itemType, itemId).</summary>
    public Dictionary<(string ItemType, Guid ItemId), int> Seeded { get; } = [];

    /// <summary>Every (itemType, ids) lookup the overlay issued - one entry per query.</summary>
    public List<(string ItemType, IReadOnlyCollection<Guid> Ids)> Lookups { get; } = [];

    /// <summary>Whether ids resolve to a library item at all.</summary>
    public bool Resolves { get; set; } = true;

    public FakeRatingService Seed(string itemType, Guid itemId, int value)
    {
        Seeded[(itemType, itemId)] = value;
        return this;
    }

    public Task<string?> SetRatingAsync(Guid userId, Guid itemId, int value, CancellationToken ct = default)
    {
        if (!Resolves)
            return Task.FromResult<string?>(null);

        Calls.Add((itemId, value));
        return Task.FromResult<string?>(IRatingService.Track);
    }

    public Task<int?> GetRatingAsync(Guid userId, string itemType, Guid itemId, CancellationToken ct = default)
        => Task.FromResult(Seeded.TryGetValue((itemType, itemId), out var v) ? v : (int?)null);

    public Task<IReadOnlyDictionary<Guid, int>> GetRatingsAsync(
        Guid userId, string itemType, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        Lookups.Add((itemType, itemIds));

        var found = itemIds
            .Where(id => Seeded.ContainsKey((itemType, id)))
            .Distinct()
            .ToDictionary(id => id, id => Seeded[(itemType, id)]);

        return Task.FromResult<IReadOnlyDictionary<Guid, int>>(found);
    }
}
