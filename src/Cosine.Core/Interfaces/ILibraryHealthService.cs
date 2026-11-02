namespace Cosine.Core.Interfaces;

public interface ILibraryHealthService
{
    Task<LibraryHealthReport> GetReportAsync(CancellationToken ct = default);
}

public record LibraryHealthReport(
    DateTimeOffset CheckedAt, int TotalTracks, int TotalAlbums, bool MediaRootAvailable,
    IReadOnlyList<LibraryHealthCategory> Categories);

public record LibraryHealthCategory(
    string Key, string Title, string Description, int Count,
    IReadOnlyList<LibraryHealthItem> Items);

public record LibraryHealthItem(Guid Id, string Kind, string Name, string Detail);
