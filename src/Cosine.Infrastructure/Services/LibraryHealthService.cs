using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Core.Security;
using Cosine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cosine.Infrastructure.Services;

public sealed class LibraryHealthService(CosineDbContext db, IOptions<MediaOptions> media) : ILibraryHealthService
{
    private const int SampleLimit = 50;

    public async Task<LibraryHealthReport> GetReportAsync(CancellationToken ct = default)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var totalTracks = await db.Tracks.CountAsync(ct);
        var totalAlbums = await db.Albums.CountAsync(ct);
        var categories = new List<LibraryHealthCategory>();

        var noArt = db.Albums.AsNoTracking().Where(a => a.CoverBlob == null || a.CoverBlob.Length == 0);
        categories.Add(new("artwork", "Missing artwork", "Albums without stored cover art.",
            await noArt.CountAsync(ct),
            await noArt.OrderBy(a => a.Name).ThenBy(a => a.Id).Take(SampleLimit)
                .Select(a => new LibraryHealthItem(a.Id, "album", a.Name, a.Artist.Name)).ToListAsync(ct)));

        var metadata = db.Tracks.AsNoTracking().Where(t =>
            t.Title.Trim() == "" || t.Artist.Name.Trim() == "" || t.Artist.Name.ToLower() == "unknown artist" ||
            t.Album.Name.Trim() == "" || t.Album.Name.ToLower() == "unknown album" ||
            t.TrackNumber == null || t.TrackNumber <= 0 || t.Year == null || t.Year <= 0);
        var metadataRows = await metadata.OrderBy(t => t.Title).ThenBy(t => t.Id).Take(SampleLimit)
            .Select(t => new { t.AlbumId, t.Title, Artist = t.Artist.Name, Album = t.Album.Name, t.TrackNumber, t.Year, t.FilePath }).ToListAsync(ct);
        categories.Add(new("metadata", "Incomplete metadata", "Tracks with missing title, artist, album, track number, or year.",
            await metadata.CountAsync(ct), metadataRows.Select(t =>
            {
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(t.Title)) missing.Add("title");
                if (string.IsNullOrWhiteSpace(t.Artist) || t.Artist.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase)) missing.Add("artist");
                if (string.IsNullOrWhiteSpace(t.Album) || t.Album.Equals("Unknown Album", StringComparison.OrdinalIgnoreCase)) missing.Add("album");
                if (t.TrackNumber is null or <= 0) missing.Add("track number");
                if (t.Year is null or <= 0) missing.Add("year");
                return new LibraryHealthItem(t.AlbumId, "album", t.Title, $"Missing {string.Join(", ", missing)} · {t.FilePath}");
            }).ToList()));

        // Conservative candidates within one release. Matching tags are a reason to inspect,
        // not proof that two files contain identical audio.
        var duplicates = db.Tracks.AsNoTracking()
            .Where(t => t.Title.Trim() != "" && t.DurationMs > 0)
            .GroupBy(t => new { t.AlbumId, t.ArtistId, Title = t.Title.Trim().ToLower(), t.DurationMs })
            .Where(g => g.Count() > 1);
        var duplicateRows = await duplicates.OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Title)
            .Take(SampleLimit).Select(g => new
            {
                g.Key.AlbumId, g.Key.Title, Count = g.Count(), FirstPath = g.Min(t => t.FilePath), LastPath = g.Max(t => t.FilePath)
            }).ToListAsync(ct);
        categories.Add(new("duplicates", "Suspected duplicates", "Groups matching artist, album, normalized title, and exact duration.",
            await duplicates.CountAsync(ct), duplicateRows.Select(g => new LibraryHealthItem(g.AlbumId, "album", g.Title,
                $"{g.Count} copies · {g.FirstPath} · {g.LastPath}")).ToList()));

        var variants = db.Artists.AsNoTracking().GroupBy(a => a.Name.Trim().ToLower()).Where(g => g.Count() > 1);
        var variantKeys = await variants.OrderBy(g => g.Key).Select(g => g.Key).Take(SampleLimit).ToListAsync(ct);
        var variantRows = await db.Artists.AsNoTracking().Where(a => variantKeys.Contains(a.Name.Trim().ToLower()))
            .OrderBy(a => a.Name).Select(a => new { a.Id, a.Name }).ToListAsync(ct);
        categories.Add(new("artists", "Artist name variations", "Separate artist entries differing only in capitalization or surrounding spaces.",
            await variants.CountAsync(ct), variantRows.GroupBy(a => a.Name.Trim().ToLowerInvariant())
                .Select(g => new LibraryHealthItem(g.First().Id, "artist", g.First().Name, string.Join(" · ", g.Select(a => $"“{a.Name}”")))).ToList()));

        var rootAvailable = Directory.Exists(media.Value.RootPath);
        var unavailable = new List<LibraryHealthItem>();
        var unavailableCount = 0;
        if (rootAvailable)
        {
            // Only paths and labels are read, in bounded batches; no cover blobs or tracked
            // entities. Requests can be cancelled while inspecting a large mounted library.
            for (var offset = 0; ; offset += 1000)
            {
                var rows = await db.Tracks.AsNoTracking().OrderBy(t => t.Id).Skip(offset).Take(1000)
                    .Select(t => new { t.AlbumId, t.Title, t.FilePath }).ToListAsync(ct);
                foreach (var track in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = MediaPath.Resolve(media.Value.RootPath, track.FilePath);
                    if (path != null && File.Exists(path)) continue;
                    unavailableCount++;
                    if (unavailable.Count < SampleLimit)
                        unavailable.Add(new(track.AlbumId, "album", track.Title, track.FilePath));
                }
                if (rows.Count < 1000) break;
            }
        }
        categories.Add(new("files", "Unavailable files", "Indexed tracks whose files are missing, inaccessible, or outside the configured media root.",
            unavailableCount, unavailable));

        return new(checkedAt, totalTracks, totalAlbums, rootAvailable, categories);
    }
}
