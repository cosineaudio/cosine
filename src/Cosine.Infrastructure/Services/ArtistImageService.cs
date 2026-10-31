using System.Text.Json;
using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Fetches artist pictures. Match quality comes first: for artists tagged with a
/// MusicBrainz id, MusicBrainz's own URL relations identify the artist's official
/// Deezer page, and Deezer serves a keyless 1000x1000 picture - no name guessing.
/// Artists without a usable MusicBrainz link fall back to a Deezer name search that
/// only accepts an exact (case-insensitive) name match.
/// </summary>
public class ArtistImageService : IArtistImageService
{
    // MusicBrainz allows 1 request/second for anonymous clients.
    private static readonly TimeSpan MusicBrainzDelay = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan DeezerDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>How long an unmatchable artist is left alone before both APIs are tried again.</summary>
    private static readonly TimeSpan MissRetryAfter = TimeSpan.FromDays(30);

    /// <summary>Upper bound on artists contacted per run - ~1.35 s each, so ~11 min.</summary>
    public const int MaxArtistsPerRun = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDistributedCache _cache;
    private readonly ILogger<ArtistImageService> _logger;

    public ArtistImageService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IDistributedCache cache,
        ILogger<ArtistImageService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<int> FetchMissingImagesAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        if (await settings.GetAsync("artwork.fetch_artist_images", ct) != "true")
        {
            _logger.LogInformation("Artist images: external fetching is disabled in settings, skipping");
            return 0;
        }

        var db = scope.ServiceProvider.GetRequiredService<Data.CosineDbContext>();

        var artists = await db.Artists
            .Where(a => a.ImageBlob == null)
            .OrderBy(a => a.SortName)
            .ToListAsync(ct);

        if (artists.Count == 0)
            return 0;

        // Artists that neither service could match are skipped until the marker expires. This job
        // runs after every scan and each artist costs ~1.35 s of deliberate rate-limit delay, so
        // without it a library's permanently-unmatchable artists were re-queried against both
        // APIs forever - 5 000 of them is a two-hour run, repeated on every scan.
        //
        // The run is also capped, so one scan cannot occupy the job for hours. Progress across
        // runs is still guaranteed: a hit is stored on the artist and a miss in the negative
        // cache, so neither is retried on the next pass.
        var candidates = new List<Core.Entities.Artist>();
        var truncated = false;
        foreach (var artist in artists)
        {
            if (candidates.Count >= MaxArtistsPerRun) { truncated = true; break; }
            if (await _cache.GetAsync(MissKey(artist.Id), ct) is null)
                candidates.Add(artist);
        }

        _logger.LogInformation(
            "Artist images: {Count} artists without an image, {Candidates} to try this run{More}",
            artists.Count, candidates.Count, truncated ? " (capped)" : "");

        var http = _httpClientFactory.CreateClient("artist-images");
        var fetched = 0;

        foreach (var artist in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var pictureUrl = await ResolvePictureUrlAsync(http, artist.MusicBrainzId, artist.Name, ct);
                if (pictureUrl is null)
                {
                    await RememberMissAsync(artist.Id, ct);
                    continue;
                }

                var bytes = await http.GetByteArrayAsync(pictureUrl, ct);
                if (bytes.Length == 0)
                {
                    await RememberMissAsync(artist.Id, ct);
                    continue;
                }

                artist.ImageBlob = bytes;
                artist.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);

                // Drop the serving cache so the new image is visible immediately.
                await _cache.RemoveAsync($"cosine:artwork:artist:{artist.Id}", ct);

                fetched++;
                _logger.LogInformation("Artist images: fetched image for {Name}", artist.Name);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Artist images: failed for {Name} - {Message}", artist.Name, ex.Message);
            }
        }

        _logger.LogInformation("Artist images: fetched {Fetched}/{Total}", fetched, artists.Count);
        return fetched;
    }

    /// <summary>Negative-cache key for an artist neither service could match.</summary>
    public static string MissKey(Guid artistId) => $"cosine:artist-image:miss:{artistId}";

    private Task RememberMissAsync(Guid artistId, CancellationToken ct)
        => _cache.SetAsync(
            MissKey(artistId), [1],
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = MissRetryAfter },
            ct);

    private async Task<string?> ResolvePictureUrlAsync(
        HttpClient http, string? musicBrainzId, string name, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(musicBrainzId))
        {
            var viaMusicBrainz = await ResolveViaMusicBrainzAsync(http, musicBrainzId, ct);
            if (viaMusicBrainz is not null)
                return viaMusicBrainz;
        }

        return await ResolveViaDeezerSearchAsync(http, name, ct);
    }

    private async Task<string?> ResolveViaMusicBrainzAsync(
        HttpClient http, string musicBrainzId, CancellationToken ct)
    {
        await Task.Delay(MusicBrainzDelay, ct);

        using var response = await http.GetAsync(
            $"https://musicbrainz.org/ws/2/artist/{musicBrainzId}?inc=url-rels&fmt=json", ct);
        if (!response.IsSuccessStatusCode)
            return null;

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!json.RootElement.TryGetProperty("relations", out var relations))
            return null;

        foreach (var relation in relations.EnumerateArray())
        {
            if (!relation.TryGetProperty("url", out var url)
                || !url.TryGetProperty("resource", out var resource))
                continue;

            var link = resource.GetString();
            if (link is null)
                continue;

            var deezerId = ExtractDeezerArtistId(link);
            if (deezerId is null)
                continue;

            var picture = await GetDeezerPictureAsync(http, deezerId, ct);
            if (picture is not null)
                return picture;
        }

        return null;
    }

    private static string? ExtractDeezerArtistId(string url)
    {
        const string marker = "deezer.com/artist/";
        var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var id = url[(idx + marker.Length)..].TrimEnd('/');
        return id.All(char.IsDigit) && id.Length > 0 ? id : null;
    }

    private async Task<string?> GetDeezerPictureAsync(HttpClient http, string deezerArtistId, CancellationToken ct)
    {
        await Task.Delay(DeezerDelay, ct);

        using var response = await http.GetAsync($"https://api.deezer.com/artist/{deezerArtistId}", ct);
        if (!response.IsSuccessStatusCode)
            return null;

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return PictureUrlFrom(json.RootElement);
    }

    private async Task<string?> ResolveViaDeezerSearchAsync(HttpClient http, string name, CancellationToken ct)
    {
        await Task.Delay(DeezerDelay, ct);

        var query = Uri.EscapeDataString($"\"{name}\"");
        using var response = await http.GetAsync($"https://api.deezer.com/search/artist?q={query}&limit=5", ct);
        if (!response.IsSuccessStatusCode)
            return null;

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!json.RootElement.TryGetProperty("data", out var data))
            return null;

        foreach (var candidate in data.EnumerateArray())
        {
            if (!candidate.TryGetProperty("name", out var candidateName)
                || !string.Equals(candidateName.GetString(), name, StringComparison.OrdinalIgnoreCase))
                continue;

            var picture = PictureUrlFrom(candidate);
            if (picture is not null)
                return picture;
        }

        return null;
    }

    private static string? PictureUrlFrom(JsonElement artistJson)
    {
        if (!artistJson.TryGetProperty("picture_xl", out var picture))
            return null;

        var url = picture.GetString();
        if (string.IsNullOrEmpty(url))
            return null;

        // Deezer's placeholder image lives under an empty md5 segment ("/artist//").
        if (url.Contains("/artist//", StringComparison.Ordinal))
            return null;

        return url;
    }
}
