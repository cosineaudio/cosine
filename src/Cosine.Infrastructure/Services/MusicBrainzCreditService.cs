using System.Globalization;
using System.Text.Json;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Fills in per-track artist credits from MusicBrainz.
/// <para>
/// This exists because the credit is usually not in the files. Taggers built around an
/// artist→album→track model - Lidarr's, most obviously - have nowhere to put a featured artist,
/// so they write the *release* artist into every track's ARTIST frame and the guest is gone
/// before Cosine ever opens the file. <see cref="ArtistCreditParsing"/> recovers what the tags
/// do carry; this recovers what they do not.
/// </para>
/// <para>
/// Keyed on the album's release MBID rather than each track's recording MBID: one release
/// lookup returns the credit for every track on it, which turns a 6 000-request job into a
/// 500-request one - the difference between two hours and nine minutes at MusicBrainz's
/// 1 req/sec anonymous limit.
/// </para>
/// <para>
/// Opt-in, like <see cref="ArtistImageService"/> and for the same reason: enabling it tells a
/// third party what is in the library, which is not a thing a self-hosted server should do
/// because it was installed.
/// </para>
/// </summary>
public class MusicBrainzCreditService : IMusicBrainzCreditService
{
    /// <summary>MusicBrainz allows 1 request/second for anonymous clients.</summary>
    public const int DefaultRateLimitMs = 1100;

    public const string DefaultBaseUrl = "https://musicbrainz.org";

    /// <summary>
    /// Upper bound on releases looked up per run. At the default delay that is ~9 minutes;
    /// progress carries across runs because a written credit is never re-fetched.
    /// </summary>
    public const int MaxReleasesPerRun = 500;

    /// <summary>How long a release MusicBrainz does not know is left alone before a retry.</summary>
    private static readonly TimeSpan MissRetryAfter = TimeSpan.FromDays(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDistributedCache _cache;
    private readonly ILogger<MusicBrainzCreditService> _logger;

    public MusicBrainzCreditService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IDistributedCache cache,
        ILogger<MusicBrainzCreditService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Negative-cache key for a release MusicBrainz could not resolve.</summary>
    public static string MissKey(Guid albumId) => $"cosine:mb-credits:miss:{albumId}";

    public async Task<int> EnrichMissingCreditsAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        if (await settings.GetAsync(IMusicBrainzCreditService.EnabledSettingKey, ct) != "true")
        {
            _logger.LogInformation("Track credits: MusicBrainz enrichment is disabled in settings, skipping");
            return 0;
        }

        var db = scope.ServiceProvider.GetRequiredService<Data.CosineDbContext>();

        // Albums with a release MBID that still have at least one track whose credit has not
        // come from MusicBrainz. Once every track on a release is enriched the album drops out
        // of this set for good, so a completed library costs one query and no requests.
        var albums = await db.Albums
            .AsNoTracking()
            .Where(a => a.MusicBrainzId != null
                && a.Tracks.Any(t => !t.TrackArtists.Any(ta => ta.Source == TrackArtistSource.MusicBrainz)))
            .OrderBy(a => a.SortName)
            .Select(a => new { a.Id, a.Name, MusicBrainzId = a.MusicBrainzId! })
            .ToListAsync(ct);

        if (albums.Count == 0)
        {
            _logger.LogInformation("Track credits: nothing to enrich");
            return 0;
        }

        var candidates = new List<(Guid Id, string Name, string MusicBrainzId)>();
        var truncated = false;
        foreach (var album in albums)
        {
            if (candidates.Count >= MaxReleasesPerRun) { truncated = true; break; }
            if (await _cache.GetAsync(MissKey(album.Id), ct) is null)
                candidates.Add((album.Id, album.Name, album.MusicBrainzId));
        }

        var baseUrl = (await settings.GetAsync(IMusicBrainzCreditService.BaseUrlSettingKey, ct))?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = DefaultBaseUrl;
        var delay = TimeSpan.FromMilliseconds(await ReadRateLimitAsync(settings, ct));
        var contact = (await settings.GetAsync(IMusicBrainzCreditService.ContactSettingKey, ct))?.Trim();

        _logger.LogInformation(
            "Track credits: {Total} releases missing credits, {Candidates} to try this run{More} against {BaseUrl}",
            albums.Count, candidates.Count, truncated ? " (capped)" : "", baseUrl);

        var http = _httpClientFactory.CreateClient("musicbrainz");
        ApplyContact(http, contact);

        var written = 0;
        foreach (var album in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await Task.Delay(delay, ct);

                var release = await FetchReleaseAsync(http, baseUrl!, album.MusicBrainzId, ct);
                if (release is null)
                {
                    await RememberMissAsync(album.Id, ct);
                    continue;
                }

                using (release)
                {
                    var applied = await ApplyReleaseAsync(db, album.Id, release.RootElement, ct);
                    if (applied == 0)
                    {
                        // The release resolved but matched no track - a mis-tagged album id.
                        // Worth a negative marker: retrying it every run never gets further.
                        await RememberMissAsync(album.Id, ct);
                        continue;
                    }

                    written += applied;
                    _logger.LogInformation(
                        "Track credits: {Applied} tracks credited on {Album}", applied, album.Name);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Track credits: failed for {Album} - {Message}", album.Name, ex.Message);
            }
        }

        _logger.LogInformation("Track credits: wrote credits for {Written} tracks", written);
        return written;
    }

    private static async Task<int> ReadRateLimitAsync(ISettingsService settings, CancellationToken ct)
    {
        var raw = await settings.GetAsync(IMusicBrainzCreditService.RateLimitSettingKey, ct);
        // A blank or nonsense value must not become a zero delay pointed at musicbrainz.org.
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms >= 0
            ? ms
            : DefaultRateLimitMs;
    }

    /// <summary>
    /// MusicBrainz's rate limiting keys on the User-Agent, and its policy is to block clients
    /// that do not identify themselves. The base client already sends a generic cosine agent;
    /// an operator-supplied contact is appended to it.
    /// </summary>
    private static void ApplyContact(HttpClient http, string? contact)
    {
        if (string.IsNullOrWhiteSpace(contact))
            return;

        // Parentheses are the User-Agent comment syntax, so a contact containing one would
        // produce a header the parser rejects and take the whole run down.
        var sanitized = contact.Replace('(', ' ').Replace(')', ' ').Trim();
        if (sanitized.Length > 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"({sanitized})");
    }

    private async Task<JsonDocument?> FetchReleaseAsync(
        HttpClient http, string baseUrl, string releaseMbid, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/ws/2/release/{Uri.EscapeDataString(releaseMbid)}"
            + "?inc=recordings+artist-credits&fmt=json";

        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Track credits: {Status} for release {Mbid}", (int)response.StatusCode, releaseMbid);
            return null;
        }

        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    /// <summary>
    /// Matches a release's tracks onto the album's rows and writes their credits. Returns the
    /// number of tracks written.
    /// </summary>
    private async Task<int> ApplyReleaseAsync(
        Data.CosineDbContext db, Guid albumId, JsonElement release, CancellationToken ct)
    {
        var releaseTracks = ReadReleaseTracks(release);
        if (releaseTracks.Count == 0)
            return 0;

        var tracks = await db.Tracks
            .Include(t => t.TrackArtists)
            .Where(t => t.AlbumId == albumId)
            .ToListAsync(ct);

        // Three independent indexes, tried strongest first. Recording id is exact; release-track
        // id is exact but only present on Picard-style tags; position is a guess that holds for
        // any album whose file set matches the release it claims to be.
        var byRecording = Index(releaseTracks, t => t.RecordingId);
        var byReleaseTrack = Index(releaseTracks, t => t.TrackId);
        var byPosition = releaseTracks
            .GroupBy(t => (t.MediumPosition, t.Position))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());

        var now = DateTimeOffset.UtcNow;
        var written = 0;

        foreach (var track in tracks)
        {
            if (track.TrackArtists.Any(ta => ta.Source == TrackArtistSource.MusicBrainz))
                continue;

            var match = Lookup(byRecording, track.MusicBrainzRecordingId)
                ?? Lookup(byReleaseTrack, track.MusicBrainzId)
                ?? (byPosition.TryGetValue((track.DiscNumber ?? 1, track.TrackNumber ?? 0), out var byPos) ? byPos : null);

            if (match is null || match.Credits.Count == 0)
                continue;

            await WriteCreditAsync(db, track, match.Credits, now, ct);
            written++;
        }

        if (written > 0)
            await db.SaveChangesAsync(ct);

        return written;

        static Dictionary<string, ReleaseTrack> Index(List<ReleaseTrack> source, Func<ReleaseTrack, string?> key)
            => source
                .Where(t => !string.IsNullOrEmpty(key(t)))
                .GroupBy(key!)
                // A release can list the same recording twice; an ambiguous key is no key.
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key!, g => g.Single(), StringComparer.OrdinalIgnoreCase);

        static ReleaseTrack? Lookup(Dictionary<string, ReleaseTrack> index, string? key)
            => key is not null && index.TryGetValue(key, out var found) ? found : null;
    }

    /// <summary>
    /// Replaces a track's credit with the MusicBrainz one. The primary artist
    /// (<see cref="Track.ArtistId"/>) is left alone deliberately: it is what album grouping,
    /// the artist index and every existing star and playlist reference hang off, and a release
    /// whose credit disagrees with the library's own grouping should not silently re-home the
    /// track. The credit is additive - it says who else is on it.
    /// </summary>
    private static async Task WriteCreditAsync(
        Data.CosineDbContext db, Track track, IReadOnlyList<MbCredit> credits,
        DateTimeOffset now, CancellationToken ct)
    {
        foreach (var stale in track.TrackArtists.ToList())
            db.TrackArtists.Remove(stale);
        track.TrackArtists.Clear();

        var seen = new HashSet<Guid>();
        var ordinal = 0;
        foreach (var credit in credits)
        {
            var artist = await GetOrCreateArtistAsync(db, credit.ArtistName, credit.ArtistMbid, now, ct);
            if (!seen.Add(artist.Id))
                continue;

            db.TrackArtists.Add(new TrackArtist
            {
                TrackId = track.Id,
                ArtistId = artist.Id,
                Ordinal = ordinal++,
                CreditedAs = credit.CreditedAs,
                JoinPhrase = credit.JoinPhrase,
                Source = TrackArtistSource.MusicBrainz
            });
        }
    }

    /// <summary>
    /// Resolves a credited artist, matching on MusicBrainz id first so two artists sharing a
    /// name stay apart. Mirrors the scanner's rule that a name-only match may not claim an
    /// artist that already carries a different id.
    /// </summary>
    private static async Task<Artist> GetOrCreateArtistAsync(
        Data.CosineDbContext db, string name, string? mbid, DateTimeOffset now, CancellationToken ct)
    {
        Artist? artist = null;
        if (mbid is not null)
        {
            artist = db.Artists.Local.FirstOrDefault(a => a.MusicBrainzId == mbid)
                ?? await db.Artists.FirstOrDefaultAsync(a => a.MusicBrainzId == mbid, ct);
        }

        artist ??= db.Artists.Local.FirstOrDefault(a => a.Name == name && (mbid is null || a.MusicBrainzId is null))
            ?? await db.Artists.FirstOrDefaultAsync(a => a.Name == name && (mbid == null || a.MusicBrainzId == null), ct);

        if (artist is not null)
        {
            // A known artist that was never tagged with an id gets one; the name is left alone,
            // because the library's spelling is the one its albums and stars already use.
            if (mbid is not null && artist.MusicBrainzId != mbid)
            {
                artist.MusicBrainzId = mbid;
                artist.UpdatedAt = now;
            }
            return artist;
        }

        artist = new Artist
        {
            Name = name,
            SortName = name,
            MusicBrainzId = mbid,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Artists.Add(artist);
        return artist;
    }

    private Task RememberMissAsync(Guid albumId, CancellationToken ct)
        => _cache.SetAsync(
            MissKey(albumId), [1],
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = MissRetryAfter },
            ct);

    // ---- release JSON ------------------------------------------------------------------

    internal sealed record MbCredit(string CreditedAs, string JoinPhrase, string ArtistName, string? ArtistMbid);

    internal sealed record ReleaseTrack(
        string? TrackId, string? RecordingId, int MediumPosition, int Position, IReadOnlyList<MbCredit> Credits);

    /// <summary>
    /// Flattens <c>media[].tracks[]</c> out of a release response. Track-level artist credits
    /// take precedence over the recording's: a recording shared across releases carries the
    /// credit it was first entered with, while the track states what *this* release printed.
    /// </summary>
    internal static List<ReleaseTrack> ReadReleaseTracks(JsonElement release)
    {
        var result = new List<ReleaseTrack>();
        if (!release.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
            return result;

        var mediumPosition = 0;
        foreach (var medium in media.EnumerateArray())
        {
            mediumPosition = medium.TryGetProperty("position", out var mp) && mp.TryGetInt32(out var mpv)
                ? mpv
                : mediumPosition + 1;

            if (!medium.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
                continue;

            var position = 0;
            foreach (var track in tracks.EnumerateArray())
            {
                position = track.TryGetProperty("position", out var p) && p.TryGetInt32(out var pv)
                    ? pv
                    : position + 1;

                var recording = track.TryGetProperty("recording", out var r) && r.ValueKind == JsonValueKind.Object
                    ? r
                    : default;

                var credits = ReadCredits(track);
                if (credits.Count == 0 && recording.ValueKind == JsonValueKind.Object)
                    credits = ReadCredits(recording);

                result.Add(new ReleaseTrack(
                    GetString(track, "id"),
                    recording.ValueKind == JsonValueKind.Object ? GetString(recording, "id") : null,
                    mediumPosition,
                    position,
                    credits));
            }
        }

        return result;
    }

    private static List<MbCredit> ReadCredits(JsonElement owner)
    {
        var credits = new List<MbCredit>();
        if (!owner.TryGetProperty("artist-credit", out var list) || list.ValueKind != JsonValueKind.Array)
            return credits;

        foreach (var entry in list.EnumerateArray())
        {
            var artist = entry.TryGetProperty("artist", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;

            // "name" is how this release printed it; the artist's own name is the fallback.
            var creditedAs = GetString(entry, "name")
                ?? (artist.ValueKind == JsonValueKind.Object ? GetString(artist, "name") : null);
            if (creditedAs is null)
                continue;

            var artistName = artist.ValueKind == JsonValueKind.Object
                ? GetString(artist, "name") ?? creditedAs
                : creditedAs;

            credits.Add(new MbCredit(
                creditedAs,
                GetString(entry, "joinphrase") ?? string.Empty,
                artistName,
                artist.ValueKind == JsonValueKind.Object ? GetString(artist, "id") : null));
        }

        return credits;
    }

    private static string? GetString(JsonElement owner, string property)
        => owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } s ? s : null
            : null;
}
