namespace Cosine.Core.Interfaces;

public interface IListeningInsightsService
{
    Task<ListeningInsights> GetAsync(Guid userId, int year, int month, CancellationToken ct = default);
}

public record ListeningInsights(
    DateTimeOffset From, DateTimeOffset Until, RecapDto Recap, long PreviousMonthPlays,
    IReadOnlyList<RecapEntry> PreviousMonthGenres, int LibraryTracks, int ExploredTracks);
