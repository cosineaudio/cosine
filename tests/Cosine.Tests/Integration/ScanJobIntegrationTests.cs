using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

using AtlTrack = ATL.Track;

namespace Cosine.Tests.Integration;

/// <summary>
/// End-to-end scan tests against real FLAC files and a real Postgres:
/// import, metadata upsert by MusicBrainz id, embedded art, encoding repair,
/// and deletion cleanup.
/// </summary>
public sealed class ScanJobIntegrationTests : IAsyncLifetime
{
    // Minimal valid 1x1 JPEG.
    private static readonly byte[] TinyJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0a" +
        "HBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAA" +
        "AAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AVN//2Q==");

    private const string ReleaseTrackId = "11111111-1111-1111-1111-111111111111";
    private const string AlbumMbid = "22222222-2222-2222-2222-222222222222";
    private const string ArtistMbid = "33333333-3333-3333-3333-333333333333";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("cosine_scan_test")
        .WithUsername("cosine")
        .WithPassword("cosine_test")
        .Build();

    private ServiceProvider _provider = null!;
    private string _mediaRoot = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _mediaRoot = Path.Combine(Path.GetTempPath(), $"cosine-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_mediaRoot);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CosineDbContext>(opts =>
            opts.UseNpgsql(_postgres.GetConnectionString(), o => o.UseVector()));
        services.AddSignalR();
        services.AddSingleton<ISearchService, NoopSearchService>();
        services.AddSingleton<ISettingsService, NoopSettingsService>();
        services.AddSingleton(Options.Create(new MediaOptions { RootPath = _mediaRoot }));
        services.AddTransient<ScanJob>();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
        if (Directory.Exists(_mediaRoot))
            Directory.Delete(_mediaRoot, recursive: true);
    }

    [Fact]
    public async Task Scan_Imports_Upserts_And_Removes()
    {
        // --- Scan 1: initial import -----------------------------------------
        // a.flac: MusicBrainz tags, embedded art, and a CP-1252 0x92 byte in the
        // title (invalid UTF-8 in a Vorbis comment, as written by some rippers).
        var fileA = CreateFlac("a.flac", t =>
        {
            t.Title = "When It~s All Gone"; // '~' is binary-patched to 0x92 below
            t.Artist = "Terror Reid";
            t.Album = "Hot Vodka 1";
            t.TrackNumber = 4;
            t.Year = 2020;
            t.Genre = "Rap";
            t.AdditionalFields["MUSICBRAINZ_RELEASETRACKID"] = ReleaseTrackId;
            t.AdditionalFields["MUSICBRAINZ_ALBUMID"] = AlbumMbid;
            t.AdditionalFields["MUSICBRAINZ_ARTISTID"] = ArtistMbid;
            t.EmbeddedPictures.Add(ATL.PictureInfo.fromBinaryData(TinyJpeg, ATL.PictureInfo.PIC_TYPE.Front));
        });
        PatchByte(fileA, (byte)'~', 0x92);

        var fileB = CreateFlac("b.flac", t =>
        {
            t.Title = "Second Song";
            t.Artist = "Terror Reid";
            t.Album = "Hot Vodka 1";
            t.TrackNumber = 5;
        });

        // c.flac: no tags at all - must fall back to Unknown Artist/Album.
        var fileC = CreateFlac("c.flac", _ => { });

        var log1 = await RunScanAsync();
        Assert.Equal("completed", log1.Status);
        Assert.Equal(3, log1.FilesAdded);
        Assert.Equal(0, log1.FilesRemoved);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

            var trackA = await db.Tracks.Include(t => t.Album).ThenInclude(a => a.Artist)
                .SingleAsync(t => t.FilePath == "a.flac");
            Assert.Equal("When It’s All Gone", trackA.Title); // CP-1252 byte repaired
            Assert.Equal(ReleaseTrackId, trackA.MusicBrainzId);
            Assert.NotNull(trackA.FileModifiedAt);
            Assert.Equal(AlbumMbid, trackA.Album.MusicBrainzId);
            Assert.Equal(ArtistMbid, trackA.Album.Artist.MusicBrainzId);
            Assert.Equal("Terror Reid", trackA.Album.Artist.Name);

            // Embedded art imported onto the album
            Assert.NotNull(trackA.Album.CoverBlob);
            Assert.Equal(TinyJpeg, trackA.Album.CoverBlob);
            Assert.Equal(2, trackA.Album.TrackCount);

            var trackC = await db.Tracks.Include(t => t.Album).ThenInclude(a => a.Artist)
                .SingleAsync(t => t.FilePath == "c.flac");
            Assert.Equal("c", trackC.Title); // file name fallback
            Assert.Equal("Unknown Artist", trackC.Album.Artist.Name);
            Assert.Equal("Unknown Album", trackC.Album.Name);
        }

        // --- Scan 2: Lidarr-style retag + move ------------------------------
        // New metadata is written in place, then the file is renamed. The track
        // must be matched by MusicBrainz id and updated, not deleted+recreated.
        Guid originalTrackAId;
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
            originalTrackAId = (await db.Tracks.SingleAsync(t => t.FilePath == "a.flac")).Id;
        }

        var atl = new AtlTrack(fileA);
        atl.Title = "When It's All Gone (Remastered)";
        Assert.True(atl.Save());

        var movedDir = Path.Combine(_mediaRoot, "Terror Reid", "Hot Vodka 1");
        Directory.CreateDirectory(movedDir);
        var movedPath = Path.Combine(movedDir, "04 - When It's All Gone.flac");
        File.Move(fileA, movedPath);

        var log2 = await RunScanAsync();
        Assert.Equal("completed", log2.Status);
        Assert.Equal(0, log2.FilesAdded);
        Assert.Equal(1, log2.FilesUpdated);
        Assert.Equal(0, log2.FilesRemoved);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
            Assert.Equal(3, await db.Tracks.CountAsync());

            var trackA = await db.Tracks.SingleAsync(t => t.MusicBrainzId == ReleaseTrackId);
            Assert.Equal(originalTrackAId, trackA.Id); // identity preserved across the move
            Assert.Equal("When It's All Gone (Remastered)", trackA.Title);
            Assert.Equal("Terror Reid/Hot Vodka 1/04 - When It's All Gone.flac", trackA.FilePath);
        }

        // --- Scan 3: deletion cleanup ---------------------------------------
        File.Delete(fileC);

        var log3 = await RunScanAsync();
        Assert.Equal("completed", log3.Status);
        Assert.Equal(1, log3.FilesRemoved);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
            Assert.Equal(2, await db.Tracks.CountAsync());

            var leftoverAlbums = await db.Albums.Select(a => new { a.Name, a.TrackCount }).ToListAsync();
            Assert.False(leftoverAlbums.Any(a => a.Name == "Unknown Album"),
                "albums left: " + string.Join(", ", leftoverAlbums.Select(a => $"{a.Name}({a.TrackCount})")));
            Assert.False(await db.Artists.AnyAsync(a => a.Name == "Unknown Artist"));

            var album = await db.Albums.SingleAsync(a => a.MusicBrainzId == AlbumMbid);
            Assert.Equal(2, album.TrackCount);
        }
    }

    // A retag that moves a track between albums used to leave the *source* album untouched:
    // the affected-album set was collected after the upsert had already reassigned AlbumId,
    // so only the destination was recomputed and the source kept an inflated TrackCount.
    [Fact]
    public async Task Scan_MovingATrackBetweenAlbums_RecomputesBothAlbums()
    {
        CreateFlac("one.flac", t =>
        {
            t.Title = "One";
            t.Artist = "Mover";
            t.Album = "Source Album";
            t.TrackNumber = 1;
        });
        var two = CreateFlac("two.flac", t =>
        {
            t.Title = "Two";
            t.Artist = "Mover";
            t.Album = "Source Album";
            t.TrackNumber = 2;
        });

        Assert.Equal("completed", (await RunScanAsync()).Status);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
            Assert.Equal(2, (await db.Albums.SingleAsync(a => a.Name == "Source Album")).TrackCount);
        }

        var atl = new AtlTrack(two);
        atl.Album = "Destination Album";
        Assert.True(atl.Save());
        // The retag rewrites the Vorbis comment inside the FLAC's existing padding, so the file
        // size is unchanged, and both scans run within ScanJob's 2s MTimeTolerance of each other.
        // Without an explicit bump the scanner correctly classifies the file as unchanged and
        // never re-reads it - the move this test is about would never be seen. In production the
        // recorded mtime is from an earlier scan, so a real retag is always outside the tolerance.
        File.SetLastWriteTimeUtc(two, DateTime.UtcNow.AddMinutes(5));

        Assert.Equal("completed", (await RunScanAsync()).Status);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
            Assert.Equal(1, (await db.Albums.SingleAsync(a => a.Name == "Source Album")).TrackCount);
            Assert.Equal(1, (await db.Albums.SingleAsync(a => a.Name == "Destination Album")).TrackCount);
        }
    }

    // The scan used to hold every track in the change tracker for its whole run; the entities are
    // now loaded per batch and the tracker is cleared after each flush. Two batches' worth of
    // files exercise the seam that creates: everything a later batch needs - the artists, albums
    // and genres an earlier one created, and the ScanLog row itself - has to survive being
    // detached. A re-scan then has to see all of it as unchanged.
    [Fact]
    public async Task Scan_AcrossMultipleBatches_ImportsEverythingAndSeesNoChangeOnRescan()
    {
        const int fileCount = 600; // > the 500-file batch size
        for (var i = 0; i < fileCount; i++)
        {
            var index = i;
            CreateFlac($"batched/{index:D4}.flac", t =>
            {
                t.Title = $"Track {index}";
                t.Artist = "Batch Artist";
                t.Album = "Batch Album";
                t.Genre = "Batch Genre";
                t.TrackNumber = index + 1;
            });
        }

        var log1 = await RunScanAsync();
        Assert.Equal("completed", log1.Status);
        Assert.Equal(fileCount, log1.FilesAdded);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
            Assert.Equal(fileCount, await db.Tracks.CountAsync());
            // One artist/album/genre, not one per batch: the second batch has to find what the
            // first created even though the change tracker no longer holds it.
            Assert.Single(await db.Artists.Where(a => a.Name == "Batch Artist").ToListAsync());
            var album = await db.Albums.SingleAsync(a => a.Name == "Batch Album");
            Assert.Equal(fileCount, album.TrackCount);
            Assert.Single(await db.Genres.Where(g => g.Name == "Batch Genre").ToListAsync());
        }

        var log2 = await RunScanAsync();
        Assert.Equal("completed", log2.Status);
        Assert.Equal(0, log2.FilesAdded);
        Assert.Equal(0, log2.FilesUpdated);
        Assert.Equal(0, log2.FilesRemoved);
    }

    // AllDirectories follows directory symlinks, so a link pointing at one of its own ancestors
    // made the walk enumerate forever: the scan hung with its ScanLog row stuck on "running".
    [Fact]
    public async Task Scan_WithASymlinkCycleInTheLibrary_CompletesAndImportsEachFileOnce()
    {
        CreateFlac("looped/song.flac", t =>
        {
            t.Title = "Looped";
            t.Artist = "Cycle Artist";
            t.Album = "Cycle Album";
        });
        Directory.CreateSymbolicLink(
            Path.Combine(_mediaRoot, "looped", "self"), Path.Combine(_mediaRoot, "looped"));

        var log = await RunScanAsync();
        Assert.Equal("completed", log.Status);
        Assert.Equal(1, log.FilesAdded);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
        Assert.Equal("looped/song.flac", (await db.Tracks.SingleAsync()).FilePath);
    }

    [Fact]
    public async Task Scan_Syncs_Sidecar_Language_And_Embedded_Lyrics()
    {
        // song.flac + a synced sidecar; other.flac with embedded unsynced lyrics only.
        var song = CreateFlac("song.flac", t =>
        {
            t.Title = "Song One";
            t.Artist = "Lyric Artist";
            t.Album = "Lyric Album";
        });
        File.WriteAllText(Path.Combine(_mediaRoot, "song.lrc"),
            "[ti:Song One]\n[00:01.00]First line\n[00:04.50]Second line\n");
        // A language-suffixed translation sidecar for the same track.
        File.WriteAllText(Path.Combine(_mediaRoot, "song.fr.lrc"),
            "[00:01.00]Premiere ligne\n[00:04.50]Deuxieme ligne\n");

        CreateFlac("other.flac", t =>
        {
            t.Title = "Song Two";
            t.Artist = "Lyric Artist";
            t.Album = "Lyric Album";
            t.Lyrics.Add(new ATL.LyricsInfo { UnsynchronizedLyrics = "Embedded line A\nEmbedded line B" });
        });

        var log1 = await RunScanAsync();
        Assert.Equal("completed", log1.Status);

        Guid songId, otherId;
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
            songId = (await db.Tracks.SingleAsync(t => t.FilePath == "song.flac")).Id;
            otherId = (await db.Tracks.SingleAsync(t => t.FilePath == "other.flac")).Id;

            var songLyrics = await db.Lyrics.Where(l => l.TrackId == songId).ToListAsync();
            Assert.Equal(2, songLyrics.Count); // default + French sidecar
            Assert.All(songLyrics, l => Assert.Equal(Lyric.SourceSidecar, l.Source));

            var synced = songLyrics.Single(l => l.SourcePath == "song.lrc");
            Assert.True(synced.Synced);
            var lines = Cosine.Infrastructure.Services.LyricsParsing.DeserializeLines(synced.LinesJson);
            Assert.Equal(1000, lines[0].Start);
            Assert.Equal("First line", lines[0].Value);

            var french = songLyrics.Single(l => l.SourcePath == "song.fr.lrc");
            Assert.Equal("fr", french.Lang);

            // other.flac has only embedded lyrics -> one embedded fallback row.
            var otherLyrics = await db.Lyrics.Where(l => l.TrackId == otherId).ToListAsync();
            var embedded = Assert.Single(otherLyrics);
            Assert.Equal(Lyric.SourceEmbedded, embedded.Source);
            Assert.False(embedded.Synced);
            Assert.Equal("Embedded line A\nEmbedded line B", embedded.PlainText);
        }

        // --- Scan 2: unchanged sidecars are not touched, unmatched sidecar removed ---
        File.Delete(Path.Combine(_mediaRoot, "song.fr.lrc"));
        var log2 = await RunScanAsync();
        Assert.Equal("completed", log2.Status);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
            var songLyrics = await db.Lyrics.Where(l => l.TrackId == songId).ToListAsync();
            var remaining = Assert.Single(songLyrics);
            Assert.Equal("song.lrc", remaining.SourcePath);
        }

        // --- Scan 3: adding a sidecar to a track supersedes its embedded fallback ---
        File.WriteAllText(Path.Combine(_mediaRoot, "other.lrc"), "[00:00.50]Now synced\n");
        var log3 = await RunScanAsync();
        Assert.Equal("completed", log3.Status);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
            var otherLyrics = await db.Lyrics.Where(l => l.TrackId == otherId).ToListAsync();
            var only = Assert.Single(otherLyrics);
            Assert.Equal(Lyric.SourceSidecar, only.Source); // embedded dropped
            Assert.True(only.Synced);
        }
    }

    private async Task<ScanLog> RunScanAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();
        var scanLog = new ScanLog { StartedAt = DateTimeOffset.UtcNow, Status = "running" };
        db.ScanLogs.Add(scanLog);
        await db.SaveChangesAsync();

        var job = scope.ServiceProvider.GetRequiredService<ScanJob>();
        await job.RunAsync(scanLog.Id, CancellationToken.None);

        return await db.ScanLogs.AsNoTracking().SingleAsync(sl => sl.Id == scanLog.Id);
    }

    private string CreateFlac(string relativePath, Action<AtlTrack> tag)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.flac");
        var target = Path.Combine(_mediaRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(fixturePath, target);

        var track = new AtlTrack(target);
        tag(track);
        Assert.True(track.Save(), $"failed to write tags to {relativePath}");
        return target;
    }

    private static void PatchByte(string path, byte from, byte to)
    {
        var bytes = File.ReadAllBytes(path);
        var patched = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == from)
            {
                bytes[i] = to;
                patched++;
            }
        }
        Assert.Equal(1, patched);
        File.WriteAllBytes(path, bytes);
    }

    private sealed class NoopSettingsService : ISettingsService
    {
        public IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>();

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SeedDefaultsAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NoopSearchService : ISearchService
    {
        public Task<SearchResults> SearchAsync(string query, int artistCount = 20, int albumCount = 20, int trackCount = 40, CancellationToken ct = default)
            => Task.FromResult(new SearchResults([], [], []));
        public Task<SearchResults> BrowseAllAsync(int artistCount, int artistOffset, int albumCount, int albumOffset, int trackCount, int trackOffset, CancellationToken ct = default)
            => Task.FromResult(new SearchResults([], [], []));
        public Task RebuildSearchIndexAsync(string? entityType = null, Guid? entityId = null, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task RebuildArtistAlbumIndexAsync(CancellationToken ct = default)
            => Task.CompletedTask;
        public Task RebuildSearchIndexForTracksAsync(IEnumerable<Guid> trackIds, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
