using System.Text;
using System.Text.Json;
using Cosine.Core.Dtos;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Cosine.Infrastructure.Services;

public class LyricsService : ILyricsService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Data.CosineDbContext _db;
    private readonly IDistributedCache _cache;

    public LyricsService(Data.CosineDbContext db, IDistributedCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<IReadOnlyList<StructuredLyricDto>> GetStructuredLyricsAsync(Guid trackId, CancellationToken ct = default)
    {
        var key = $"cosine:lyrics:song:{trackId}";
        var cached = await _cache.GetAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<StructuredLyricDto>>(cached, Json) ?? [];

        var rows = await _db.Lyrics
            .AsNoTracking()
            .Where(l => l.TrackId == trackId)
            // Synced sets first so clients that want timing pick them without scanning.
            .OrderByDescending(l => l.Synced)
            .ThenBy(l => l.Source)
            .ToListAsync(ct);

        var result = rows.Select(Map).ToList();

        await _cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(result, Json), ct);
        return result;
    }

    public async Task<PlainLyricDto?> GetPlainLyricsAsync(string? artist, string? title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var key = $"cosine:lyrics:plain:{(artist ?? "").ToLowerInvariant()}|{title.ToLowerInvariant()}";
        var cached = await _cache.GetAsync(key, ct);
        if (cached is not null)
        {
            var value = Encoding.UTF8.GetString(cached);
            return value.Length == 0 ? null : new PlainLyricDto(artist, title, value);
        }

        var query = _db.Tracks.AsNoTracking().Where(t => t.Title.ToLower() == title.ToLower());
        if (!string.IsNullOrWhiteSpace(artist))
            query = query.Where(t => t.Artist.Name.ToLower() == artist.ToLower());

        // Prefer whichever matching track actually has lyrics.
        var plain = await query
            .SelectMany(t => _db.Lyrics.Where(l => l.TrackId == t.Id))
            .OrderBy(l => l.Synced) // unsynced text is a cleaner plain rendering than a synced line dump
            .Select(l => l.PlainText)
            .FirstOrDefaultAsync(ct);

        await _cache.SetAsync(key, Encoding.UTF8.GetBytes(plain ?? ""), ct);
        return string.IsNullOrEmpty(plain) ? null : new PlainLyricDto(artist, title, plain);
    }

    private static StructuredLyricDto Map(Lyric row)
    {
        var lines = LyricsParsing.DeserializeLines(row.LinesJson);
        return new StructuredLyricDto(
            row.Lang,
            row.Synced,
            row.OffsetMs,
            row.DisplayArtist,
            row.DisplayTitle,
            lines);
    }
}
