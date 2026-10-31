using ATL;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using StackExchange.Redis;

using AtlTrack = ATL.Track;
using TrackEntity = Cosine.Core.Entities.Track;

namespace Cosine.Infrastructure.Jobs;

[DisallowConcurrentExecution]
public class ScanJob : IJob
{
    public const string ScanLogIdKey = "ScanLogId";

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".oga", ".opus", ".aac", ".m4a",
        ".wma", ".wav", ".aiff", ".aif", ".ape", ".wv", ".mpc"
    };

    // Synced-lyric sidecar files, read alongside the audio tracks they name.
    private static readonly HashSet<string> LyricExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lrc", ".elrc"
    };

    // Tag timestamps can lose sub-second precision depending on filesystem.
    private static readonly TimeSpan MTimeTolerance = TimeSpan.FromSeconds(2);

    // Files per flush. Also bounds how many tracks are tracked at once - see the
    // ChangeTracker.Clear() at the end of each batch.
    private const int BatchSize = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<MediaOptions> _mediaOptions;
    private readonly ILogger<ScanJob> _logger;
    private readonly IHubContext<Hubs.ScanHub> _hubContext;
    private readonly Telemetry.CosineMetrics? _metrics;

    public ScanJob(
        IServiceScopeFactory scopeFactory,
        IOptions<MediaOptions> mediaOptions,
        ILogger<ScanJob> logger,
        IHubContext<Hubs.ScanHub> hubContext,
        Telemetry.CosineMetrics? metrics = null)
    {
        _scopeFactory = scopeFactory;
        _mediaOptions = mediaOptions;
        _logger = logger;
        _hubContext = hubContext;
        _metrics = metrics;
    }

    /// <summary>
    /// Scan lifecycle detail goes to admins only - see <see cref="Hubs.ScanHub.AdminGroup"/>.
    /// </summary>
    private Microsoft.AspNetCore.SignalR.IClientProxy Admins
        => _hubContext.Clients.Group(Hubs.ScanHub.AdminGroup);

    public async Task Execute(IJobExecutionContext context)
    {
        var dataMap = context.MergedJobDataMap;
        if (!dataMap.ContainsKey(ScanLogIdKey))
        {
            _logger.LogError("ScanJob triggered without ScanLogId in JobDataMap");
            return;
        }

        var scanLogId = Guid.Parse(dataMap.GetString(ScanLogIdKey)!);
        await RunAsync(scanLogId, context.CancellationToken);

        // New artists from this scan have no picture yet - fetch them in the background.
        await ArtistImageJob.TriggerAsync(context.Scheduler, context.CancellationToken);

        // New albums have only the credit their tags carried, which is usually just the primary
        // artist. Both jobs gate on their own setting and cost nothing while it is off.
        await TrackCreditJob.TriggerAsync(context.Scheduler, context.CancellationToken);

        // New files have no audio analysis, so similarity would fall back to tags for them.
        // Gates on pgvector being available and on its own setting.
        await AudioAnalysisJob.TriggerAsync(context.Scheduler, context.CancellationToken);
    }

    public async Task RunAsync(Guid scanLogId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Cosine.Infrastructure.Data.CosineDbContext>();
        var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
        var redis = scope.ServiceProvider.GetService<IConnectionMultiplexer>();

        var scanLog = await db.ScanLogs.FindAsync([scanLogId], ct);
        if (scanLog is null)
        {
            _logger.LogError("ScanJob: ScanLog {ScanLogId} not found", scanLogId);
            return;
        }

        try
        {
            // The admin setting overrides the static Media:RootPath configuration.
            var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var rootPath = await settingsService.GetAsync("library.media_root", ct);
            if (string.IsNullOrWhiteSpace(rootPath))
                rootPath = _mediaOptions.Value.RootPath;

            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            {
                _logger.LogError("Media root path not configured or does not exist: {RootPath}", rootPath ?? "(null)");
                await MarkFailedAsync(scanLog, db, "Media root path not configured or does not exist", _metrics);
                return;
            }

            _logger.LogInformation("Scan {ScanLogId}: walking {RootPath}", scanLogId, rootPath);
            await Admins.SendAsync("ScanStarted", scanLogId, ct);
            await Admins.SendAsync("ScanProgress", scanLogId, "scanning", 0, 0, "", ct);

            var normalizedRoot = rootPath.Replace('\\', '/');
            // Partitioned in one streaming pass: materializing every path in the library
            // first (including the artwork, cue sheets and .nfo files this discards) cost a
            // whole extra list for nothing.
            var diskFiles = new List<(string Abs, string Rel)>();
            var lyricFiles = new List<(string Abs, string Rel)>();
            foreach (var abs in EnumerateFilesWithoutCycles(rootPath))
            {
                var ext = Path.GetExtension(abs);
                if (SupportedExtensions.Contains(ext))
                    diskFiles.Add((abs, ToRelativePath(normalizedRoot, abs)));
                else if (LyricExtensions.Contains(ext))
                    lyricFiles.Add((abs, ToRelativePath(normalizedRoot, abs)));
            }

            _logger.LogInformation(
                "Scan {ScanLogId}: found {Count} audio files, {Lyrics} lyric sidecars on disk",
                scanLogId, diskFiles.Count, lyricFiles.Count);

            // Identity and file-stat only. The entities themselves are loaded per batch
            // (see below): holding the whole track table in the change tracker made every
            // SaveChangesAsync in the batch loop walk the entire library.
            var snapshots = await db.Tracks
                .AsNoTracking()
                .Select(t => new TrackSnapshot
                {
                    Id = t.Id,
                    FilePath = t.FilePath,
                    MusicBrainzId = t.MusicBrainzId,
                    FileSize = t.FileSize,
                    FileModifiedAt = t.FileModifiedAt,
                    AlbumId = t.AlbumId
                })
                .ToListAsync(ct);

            var snapshotsByPath = new Dictionary<string, TrackSnapshot>(StringComparer.OrdinalIgnoreCase);
            var snapshotsByMbid = new Dictionary<string, TrackSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in snapshots)
            {
                snapshotsByPath.TryAdd(t.FilePath, t);
                if (!string.IsNullOrEmpty(t.MusicBrainzId))
                    snapshotsByMbid.TryAdd(t.MusicBrainzId, t);
            }

            var diskPaths = new HashSet<string>(diskFiles.Select(f => f.Rel), StringComparer.OrdinalIgnoreCase);

            // Files to (re-)read: new paths, plus known paths whose size/mtime changed
            // (Lidarr rewrites tags in place when syncing with MusicBrainz).
            var toProcess = new List<(string Abs, string Rel)>();
            foreach (var (abs, rel) in diskFiles)
            {
                if (!snapshotsByPath.TryGetValue(rel, out var existing))
                {
                    toProcess.Add((abs, rel));
                    continue;
                }

                var fi = new FileInfo(abs);
                if (existing.FileModifiedAt is null
                    || existing.FileSize != fi.Length
                    || (existing.FileModifiedAt.Value - fi.LastWriteTimeUtc).Duration() > MTimeTolerance)
                {
                    toProcess.Add((abs, rel));
                }
            }

            _logger.LogInformation(
                "Scan {ScanLogId}: diff - {Changed} new/changed, {Unchanged} unchanged",
                scanLogId, toProcess.Count, diskFiles.Count - toProcess.Count);

            var addedCount = 0;
            var updatedCount = 0;
            var processedCount = 0;
            var touchedTrackIds = new List<Guid>();
            var affectedAlbumIds = new HashSet<Guid>();
            var albumArtHandled = new HashSet<Guid>();
            // Embedded lyrics for files re-read this scan, keyed by relative path.
            var embeddedLyricsByRel = new Dictionary<string, Services.LyricsParsing.Parsed>(StringComparer.OrdinalIgnoreCase);
            var totalToProcess = toProcess.Count;

            foreach (var batch in toProcess.Chunk(BatchSize))
            {
                ct.ThrowIfCancellationRequested();

                var results = new List<(string Abs, string Rel, TrackFileInfo Info)>();
                await Parallel.ForEachAsync(batch,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount), CancellationToken = ct },
                    (file, innerCt) =>
                {
                    var info = ReadFileTags(file.Abs);
                    if (info is not null)
                    {
                        lock (results)
                            results.Add((file.Abs, file.Rel, info));
                    }
                    return ValueTask.CompletedTask;
                });

                // Only the rows this batch will actually update are tracked, and only for
                // the length of the batch - resolved through the same helper the upsert
                // uses, so the preload can never miss a row the upsert then goes looking for.
                var batchIds = new HashSet<Guid>();
                foreach (var (_, rel, info) in results)
                {
                    if (ResolveSnapshot(rel, info, snapshotsByPath, snapshotsByMbid, diskPaths) is { } s)
                        batchIds.Add(s.Id);
                }

                var batchTracks = batchIds.Count == 0
                    ? new Dictionary<Guid, TrackEntity>()
                    : await db.Tracks.Where(t => batchIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

                foreach (var (abs, rel, info) in results)
                {
                    ct.ThrowIfCancellationRequested();

                    var (track, isNew, previousAlbumId) = await UpsertFileAsync(
                        db, abs, rel, info, snapshotsByPath, snapshotsByMbid, batchTracks, diskPaths, albumArtHandled, ct);

                    touchedTrackIds.Add(track.Id);
                    affectedAlbumIds.Add(track.AlbumId);
                    if (previousAlbumId is Guid movedFrom)
                        affectedAlbumIds.Add(movedFrom);
                    if (info.EmbeddedLyrics is not null)
                        embeddedLyricsByRel[rel] = info.EmbeddedLyrics;
                    if (isNew) addedCount++;
                    else updatedCount++;
                }

                await db.SaveChangesAsync(ct);
                // Drop the batch's graph - tracks, plus whatever artists/albums/genres it
                // touched and the cover blobs it imported. Without this the tracker still
                // grows with the library, just one batch at a time. Anything a later batch
                // needs is in the database by now and is re-read on demand.
                db.ChangeTracker.Clear();

                processedCount += batch.Length;
                var pct = totalToProcess > 0 ? (int)((long)processedCount * 100 / totalToProcess) : 100;
                // Debug, not Information: a large library emits hundreds of these per
                // scan, and the SignalR ScanProgress push below already drives the UI.
                _logger.LogDebug(
                    "Scan {ScanLogId}: progress {Processed}/{Total} ({Pct}%)",
                    scanLogId, processedCount, totalToProcess, pct);

                await Admins.SendAsync(
                    "ScanProgress", scanLogId, "importing", processedCount, totalToProcess, "", ct);
            }

            // Delete tracks whose file is gone. Tracks matched by MusicBrainz id above had
            // their snapshot's FilePath updated in place, so moved/renamed files are not
            // deleted here. Loaded in chunks - the rows have to be materialized for EF to
            // cascade the play history, playlist entries and stars hanging off them.
            var removedSnapshots = snapshots.Where(t => !diskPaths.Contains(t.FilePath)).ToList();
            foreach (var snapshot in removedSnapshots)
                affectedAlbumIds.Add(snapshot.AlbumId);

            foreach (var chunk in removedSnapshots.Chunk(BatchSize))
            {
                var ids = chunk.Select(s => s.Id).ToHashSet();
                db.Tracks.RemoveRange(await db.Tracks.Where(t => ids.Contains(t.Id)).ToListAsync(ct));
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }

            if (removedSnapshots.Count > 0)
                _logger.LogInformation("Scan {ScanLogId}: removed {Count} deleted tracks", scanLogId, removedSnapshots.Count);

            await RecomputeAlbumStatsAsync(db, affectedAlbumIds, ct);

            var lyricAffected = await SyncLyricsAsync(db, scanLogId, lyricFiles, embeddedLyricsByRel, ct);

            var orphanedAlbums = await db.Albums
                .Where(a => !a.Tracks.Any())
                .ToListAsync(ct);
            db.Albums.RemoveRange(orphanedAlbums);
            // Flush album deletions before the artist query below, which checks
            // Albums membership against the database.
            if (orphanedAlbums.Count > 0)
                await db.SaveChangesAsync(ct);

            var orphanedArtists = await db.Artists
                .Where(a => !a.Albums.Any() && !a.Tracks.Any() && !a.TrackArtists.Any())
                .ToListAsync(ct);
            db.Artists.RemoveRange(orphanedArtists);

            await db.SaveChangesAsync(ct);

            if (orphanedAlbums.Count > 0 || orphanedArtists.Count > 0)
            {
                _logger.LogInformation(
                    "Scan {ScanLogId}: cleaned up {Albums} orphaned albums, {Artists} orphaned artists",
                    scanLogId, orphanedAlbums.Count, orphanedArtists.Count);
            }

            // Union lyric-only changes (sidecars added/edited/removed next to unchanged
            // audio, which never enter touchedTrackIds) so their LyricsVector is rebuilt.
            var toReindex = new HashSet<Guid>(touchedTrackIds);
            toReindex.UnionWith(lyricAffected);
            if (toReindex.Count > 0)
            {
                _logger.LogInformation(
                    "Scan {ScanLogId}: rebuilding search index for {Count} tracks",
                    scanLogId, toReindex.Count);
                await searchService.RebuildSearchIndexForTracksAsync(toReindex, ct);
            }

            // Artist/album search entries aren't tied to individual touched tracks (a rename,
            // add, or orphan cleanup can change them with no file delta), and they're cheap
            // relative to tracks - rebuild them every scan so search always has current
            // artist/album rows. Without this they were never indexed and search returned
            // songs only.
            await searchService.RebuildArtistAlbumIndexAsync(ct);

            // Re-read: the per-batch ChangeTracker.Clear() above detaches this row too, and a
            // detached entity's mutations are never written.
            var completedLog = await db.ScanLogs.FindAsync([scanLogId], ct) ?? scanLog;
            completedLog.Status = "completed";
            completedLog.FinishedAt = DateTimeOffset.UtcNow;
            completedLog.FilesAdded = addedCount;
            completedLog.FilesUpdated = updatedCount;
            completedLog.FilesRemoved = removedSnapshots.Count;
            await db.SaveChangesAsync(ct);

            // Keep the caller's instance consistent with what was written, as MarkFailedAsync does.
            scanLog.Status = completedLog.Status;
            scanLog.FinishedAt = completedLog.FinishedAt;
            scanLog.FilesAdded = completedLog.FilesAdded;
            scanLog.FilesUpdated = completedLog.FilesUpdated;
            scanLog.FilesRemoved = completedLog.FilesRemoved;

            _metrics?.RecordScanCompleted(addedCount, updatedCount, removedSnapshots.Count);

            await FlushLibraryCacheAsync(redis);

            _logger.LogInformation(
                "Scan {ScanLogId}: completed - {Added} added, {Updated} updated, {Removed} removed",
                scanLogId, addedCount, updatedCount, removedSnapshots.Count);

            await Admins.SendAsync("ScanCompleted", scanLogId, ct);
            // Content-free: every client needs to know its cached browse data is stale,
            // but only admins get the scan detail above.
            await _hubContext.Clients.All.SendAsync("LibraryChanged", ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Scan {ScanLogId}: cancelled", scanLogId);
            await MarkFailedAsync(scanLog, db, "Scan cancelled", _metrics);
            await Admins.SendAsync("ScanError", scanLogId, "Scan cancelled", CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan {ScanLogId}: failed - {Message}", scanLogId, ex.Message);
            await MarkFailedAsync(scanLog, db, ex.Message, _metrics);
            await Admins.SendAsync("ScanError", scanLogId, ex.Message, CancellationToken.None);
        }
    }

    private static string ToRelativePath(string normalizedRoot, string normalizedFullPath)
        => normalizedFullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            ? normalizedFullPath[normalizedRoot.Length..].TrimStart('/')
            : normalizedFullPath;

    /// <summary>
    /// Depth-first walk yielding every file under <paramref name="root"/> with '/'-normalized
    /// paths. <see cref="SearchOption.AllDirectories"/> follows directory symlinks, so a link
    /// pointing at one of its own ancestors - an ordinary thing to find in a library assembled
    /// from several mounts - made the scan enumerate forever. Directories are keyed by their
    /// *resolved* target, so a link into already-visited territory is skipped rather than
    /// re-walked. A directory that cannot be read is skipped too, instead of failing the scan.
    /// </summary>
    public static IEnumerable<string> EnumerateFilesWithoutCycles(string root)
    {
        // Ordinal: on a case-sensitive filesystem "/A" and "/a" really are two directories.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            string[] files;
            string[] subdirectories;
            try
            {
                var info = new DirectoryInfo(dir);
                var identity = Path.GetFullPath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName);
                if (!visited.Add(identity))
                    continue;

                files = Directory.GetFiles(dir);
                subdirectories = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
                pending.Push(subdirectory);

            foreach (var file in files)
                yield return file.Replace('\\', '/');
        }
    }

    /// <summary>
    /// What the scan knows about an existing track without holding its entity: enough to diff
    /// against the file on disk, and enough to find the row again when it turns out to need
    /// writing. Mutable, because a file matched by MusicBrainz id after a move updates its path
    /// here - that is what keeps the deletion pass below from treating the old path as gone.
    /// </summary>
    private sealed class TrackSnapshot
    {
        public required Guid Id { get; set; }
        public required string FilePath { get; set; }
        public string? MusicBrainzId { get; set; }
        public long? FileSize { get; set; }
        public DateTimeOffset? FileModifiedAt { get; set; }
        public Guid AlbumId { get; set; }
    }

    /// <summary>
    /// Finds the track a file belongs to: by path first, then by MusicBrainz id so a file Lidarr
    /// moved/renamed updates the existing row instead of delete+recreate (which would cascade
    /// away play history, playlist entries, stars, ...). The id fallback only claims a track
    /// whose own path is no longer on disk.
    /// </summary>
    private static TrackSnapshot? ResolveSnapshot(
        string relPath,
        TrackFileInfo info,
        Dictionary<string, TrackSnapshot> snapshotsByPath,
        Dictionary<string, TrackSnapshot> snapshotsByMbid,
        HashSet<string> diskPaths)
    {
        if (snapshotsByPath.TryGetValue(relPath, out var existing))
            return existing;

        if (info.MusicBrainzTrackId is not null
            && snapshotsByMbid.TryGetValue(info.MusicBrainzTrackId, out var moved)
            && !diskPaths.Contains(moved.FilePath))
        {
            return moved;
        }

        return null;
    }

    /// <summary>
    /// Records the failure and nothing else. Whatever brought us here (a cancellation, a
    /// throw) can fire mid-batch, and the context still tracks every entity mutated since
    /// the last flush - so saving the bookkeeping write on its own would also commit that
    /// partial, unreconciled scan state. The tracked graph is dropped first, which detaches
    /// <paramref name="scanLog"/> too, so the row is re-read before it is updated.
    /// </summary>
    private static async Task MarkFailedAsync(
        ScanLog scanLog, Cosine.Infrastructure.Data.CosineDbContext db, string message,
        Telemetry.CosineMetrics? metrics)
    {
        metrics?.RecordScanFailed();

        db.ChangeTracker.Clear();

        var log = await db.ScanLogs.FindAsync(scanLog.Id);
        if (log is null)
            return;

        log.Status = "failed";
        log.FinishedAt = DateTimeOffset.UtcNow;
        log.ErrorMessage = message;
        await db.SaveChangesAsync();

        // Keep the caller's instance consistent with what was written.
        scanLog.Status = log.Status;
        scanLog.FinishedAt = log.FinishedAt;
        scanLog.ErrorMessage = log.ErrorMessage;
    }

    private sealed record TrackFileInfo(
        string Title, string SortTitle,
        string ArtistName, string SortArtistName,
        string AlbumName, string SortAlbumName,
        string? AlbumArtistName, string? AlbumArtistSortName,
        int? TrackNumber, int? DiscNumber, int? Year,
        long DurationMs, long FileSize,
        int? BitRate, string Format, string ContentType,
        string[] Genres,
        string? MusicBrainzTrackId, string? MusicBrainzAlbumId,
        string? MusicBrainzArtistId, string? MusicBrainzAlbumArtistId,
        string? MusicBrainzRecordingId,
        string? ArtistsTag,
        string? ReleaseType,
        DateTimeOffset FileModifiedAt, bool HasEmbeddedArt,
        Services.LyricsParsing.Parsed? EmbeddedLyrics);

    private TrackFileInfo? ReadFileTags(string filePath)
    {
        try
        {
            var atlTrack = new AtlTrack(filePath);
            if (atlTrack.Duration == 0)
            {
                _logger.LogWarning("Skipping {FilePath}: duration is zero", filePath);
                return null;
            }

            var fi = new FileInfo(filePath);
            var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            var contentType = ext switch
            {
                "mp3" => "audio/mpeg",
                "flac" => "audio/flac",
                "ogg" or "oga" => "audio/ogg",
                "opus" => "audio/opus",
                "aac" => "audio/aac",
                "m4a" => "audio/mp4",
                "wma" => "audio/x-ms-wma",
                "wav" => "audio/wav",
                _ => "audio/" + ext
            };

            var rawTitle = Services.TagEncodingRepair.RepairC1Controls(NullIfEmpty(atlTrack.Title));
            var rawArtist = Services.TagEncodingRepair.RepairC1Controls(NullIfEmpty(atlTrack.Artist));
            var rawAlbum = Services.TagEncodingRepair.RepairC1Controls(NullIfEmpty(atlTrack.Album));
            var rawAlbumArtist = Services.TagEncodingRepair.RepairC1Controls(NullIfEmpty(atlTrack.AlbumArtist));
            var rawGenre = Services.TagEncodingRepair.RepairC1Controls(NullIfEmpty(atlTrack.Genre));

            // ATL decodes Vorbis comments as strict UTF-8, so CP-1252 bytes in
            // mis-tagged FLACs come through as U+FFFD. Recover them by re-reading
            // the comment block with a lenient decoder.
            if (ext == "flac" && HasReplacementChar(rawTitle, rawArtist, rawAlbum, rawAlbumArtist, rawGenre))
            {
                var comments = Services.TagEncodingRepair.TryReadFlacVorbisComments(filePath);
                if (comments is not null)
                {
                    rawTitle = OverrideIfBroken(rawTitle, comments, "TITLE");
                    rawArtist = OverrideIfBroken(rawArtist, comments, "ARTIST");
                    rawAlbum = OverrideIfBroken(rawAlbum, comments, "ALBUM");
                    rawAlbumArtist = OverrideIfBroken(rawAlbumArtist, comments, "ALBUMARTIST");
                    rawGenre = OverrideIfBroken(rawGenre, comments, "GENRE");
                }
            }

            var title = rawTitle ?? Path.GetFileNameWithoutExtension(filePath);
            var artistName = rawArtist ?? "Unknown Artist";
            var albumName = rawAlbum ?? "Unknown Album";
            var albumArtistName = rawAlbumArtist;

            // Prefer sort names written into the tags (Lidarr/Picard write these),
            // fall back to the computed article-flipped form.
            var sortTitle = Services.TagEncodingRepair.RepairC1Controls(NullIfEmpty(atlTrack.SortTitle)) ?? GetSortName(title);
            var sortArtistName = Services.TagEncodingRepair.RepairC1Controls(NullIfEmpty(atlTrack.SortArtist)) ?? GetSortName(artistName);
            var sortAlbumName = Services.TagEncodingRepair.RepairC1Controls(NullIfEmpty(atlTrack.SortAlbum)) ?? GetSortName(albumName);
            var albumArtistSortName = albumArtistName is null
                ? null
                : Services.TagEncodingRepair.RepairC1Controls(NullIfEmpty(atlTrack.SortAlbumArtist)) ?? GetSortName(albumArtistName);

            var genres = rawGenre?.Split(new[] { ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .ToArray() ?? [];

            var fields = atlTrack.AdditionalFields ?? new Dictionary<string, string>();
            var mbTrackId = GetMusicBrainzId(fields, "MUSICBRAINZRELEASETRACKID")
                ?? GetMusicBrainzId(fields, "MUSICBRAINZTRACKID");
            // Despite the tag name, MUSICBRAINZTRACKID is the *recording* id - the only one of
            // the two with a lookup endpoint, and the key the credit enrichment matches on. It
            // used to be read only as a fallback for track identity and then dropped, because
            // MUSICBRAINZRELEASETRACKID is present on every Picard/Lidarr-tagged file.
            var mbRecordingId = GetMusicBrainzId(fields, "MUSICBRAINZTRACKID");
            var mbAlbumId = GetMusicBrainzId(fields, "MUSICBRAINZALBUMID");
            var mbArtistId = GetMusicBrainzId(fields, "MUSICBRAINZARTISTID");
            var mbAlbumArtistId = GetMusicBrainzId(fields, "MUSICBRAINZALBUMARTISTID");

            // MusicBrainz release-group primary type. Deliberately keyed on
            // MUSICBRAINZALBUMTYPE, not RELEASETYPE: the key normalizer collapses
            // "RELEASE TYPE" (a scene tag = "Normal release") and "RELEASETYPE" to
            // the same key, so an EndsWith match there is non-deterministic.
            var releaseType = NormalizeReleaseType(GetTagValue(fields, "MUSICBRAINZALBUMTYPE"));

            // Embedded lyrics tags (USLT / UNSYNCEDLYRICS / SYLT). Used only as a
            // fallback when the track has no .lrc/.elrc sidecar (see SyncLyricsAsync).
            Services.LyricsParsing.Parsed? embeddedLyrics = null;
            if (atlTrack.Lyrics is { Count: > 0 })
            {
                foreach (var li in atlTrack.Lyrics)
                {
                    embeddedLyrics = Services.LyricsParsing.FromLyricsInfo(li);
                    if (embeddedLyrics is not null) break;
                }
            }

            return new TrackFileInfo(
                title, sortTitle,
                artistName, sortArtistName,
                albumName, sortAlbumName,
                albumArtistName, albumArtistSortName,
                atlTrack.TrackNumber > 0 ? atlTrack.TrackNumber : null,
                atlTrack.DiscNumber > 0 ? atlTrack.DiscNumber : null,
                atlTrack.Year > 0 ? atlTrack.Year : null,
                (long)atlTrack.DurationMs,
                fi.Length,
                atlTrack.Bitrate > 0 ? atlTrack.Bitrate : null,
                ext,
                contentType,
                genres,
                mbTrackId, mbAlbumId, mbArtistId, mbAlbumArtistId,
                mbRecordingId,
                // Picard writes the full credit here as a multi-valued tag; ATL surfaces the
                // raw field, which ArtistCreditParsing splits. Absent on most files.
                Services.TagEncodingRepair.RepairC1Controls(NullIfEmpty(GetExactTagValue(fields, "ARTISTS"))),
                releaseType,
                fi.LastWriteTimeUtc,
                atlTrack.EmbeddedPictures is { Count: > 0 },
                embeddedLyrics);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read tags from {FilePath}", filePath);
            return null;
        }
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HasReplacementChar(params string?[] values)
        => values.Any(v => v is not null && v.Contains(Services.TagEncodingRepair.ReplacementChar));

    private static string? OverrideIfBroken(string? current, IReadOnlyDictionary<string, string> comments, string key)
    {
        if (current is null || !current.Contains(Services.TagEncodingRepair.ReplacementChar))
            return current;
        return comments.TryGetValue(key, out var value)
            ? Services.TagEncodingRepair.RepairC1Controls(NullIfEmpty(value)) ?? current
            : current;
    }

    // Matches the MusicBrainz tag conventions across formats by normalizing keys:
    // Vorbis "MUSICBRAINZ_TRACKID", ID3 TXXX "MusicBrainz Track Id",
    // MP4 "----:com.apple.iTunes:MusicBrainz Track Id" all normalize to a
    // string ending in "MUSICBRAINZTRACKID".
    private static string? GetMusicBrainzId(IDictionary<string, string> fields, string normalizedKey)
    {
        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var normalized = new string(key.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (!normalized.EndsWith(normalizedKey, StringComparison.Ordinal))
                continue;

            // Multi-value fields (e.g. collaborations) are separated by ; or /.
            foreach (var token in value.Split(new[] { ';', '/', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = token.Trim();
                if (Guid.TryParse(trimmed, out _))
                    return trimmed;
            }
        }
        return null;
    }

    // Like GetMusicBrainzId's key matching (letter/digit-only, uppercased, EndsWith)
    // but returns the raw string value - no GUID filter - for non-id tags such as
    // the release type.
    /// <summary>
    /// Like <see cref="GetTagValue"/> but requiring the whole normalized key to match. ARTISTS
    /// needs this: the suffix match that serves the MusicBrainz keys would also claim
    /// ALBUMARTISTS, which is a different credit entirely.
    /// </summary>
    private static string? GetExactTagValue(IDictionary<string, string> fields, string normalizedKey)
    {
        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var normalized = new string(key.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (normalized.Equals(normalizedKey, StringComparison.Ordinal))
                return value.Trim();
        }
        return null;
    }

    private static string? GetTagValue(IDictionary<string, string> fields, string normalizedKey)
    {
        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var normalized = new string(key.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (normalized.EndsWith(normalizedKey, StringComparison.Ordinal))
                return value.Trim();
        }
        return null;
    }

    // The tag may carry secondary types (e.g. "Album; Compilation") separated by
    // ; or /. Keep the primary (first) token, normalized to lowercase.
    private static string? NormalizeReleaseType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var primary = raw
            .Split(new[] { ';', '/', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim();

        return string.IsNullOrWhiteSpace(primary) ? null : primary.ToLowerInvariant();
    }

    private static string GetSortName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 4)
            return trimmed[4..] + ", The";
        if (trimmed.StartsWith("A ", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 2)
            return trimmed[2..] + ", A";
        return trimmed;
    }

    private async Task<(TrackEntity Track, bool IsNew, Guid? PreviousAlbumId)> UpsertFileAsync(
        Cosine.Infrastructure.Data.CosineDbContext db,
        string absPath,
        string relPath,
        TrackFileInfo info,
        Dictionary<string, TrackSnapshot> snapshotsByPath,
        Dictionary<string, TrackSnapshot> snapshotsByMbid,
        Dictionary<Guid, TrackEntity> batchTracks,
        HashSet<string> diskPaths,
        HashSet<Guid> albumArtHandled,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var albumArtistName = info.AlbumArtistName ?? info.ArtistName;
        var albumArtistSortName = info.AlbumArtistSortName ?? info.SortArtistName;
        var albumArtistMbid = info.MusicBrainzAlbumArtistId ?? (info.AlbumArtistName is null ? info.MusicBrainzArtistId : null);

        var albumArtist = await GetOrCreateArtistAsync(db, albumArtistName, albumArtistSortName, albumArtistMbid, now, ct);

        // The credit the tags describe. Its first name is the primary artist, which differs from
        // the raw ARTIST tag only when that tag was itself a compound credit - without this,
        // "Skrillex feat. Damian Marley" becomes an artist in its own right and sits in the
        // artist list forever.
        var credits = Services.ArtistCreditParsing.Parse(info.ArtistName, info.ArtistsTag, info.Title);
        var primaryName = credits.Count > 0 ? credits[0].Name : info.ArtistName;
        var primarySortName = primaryName == info.ArtistName ? info.SortArtistName : GetSortName(primaryName);

        var trackArtist = primaryName == albumArtistName
            ? albumArtist
            : await GetOrCreateArtistAsync(db, primaryName, primarySortName, info.MusicBrainzArtistId, now, ct);

        var album = await GetOrCreateAlbumAsync(
            db, info.AlbumName, info.SortAlbumName, albumArtist.Id, info.Year, info.MusicBrainzAlbumId, info.ReleaseType, now, ct);

        var snapshot = ResolveSnapshot(relPath, info, snapshotsByPath, snapshotsByMbid, diskPaths);
        TrackEntity? track = null;
        if (snapshot is not null)
            batchTracks.TryGetValue(snapshot.Id, out track);

        var isNew = track is null;
        // Captured before the reassignment below: when a retag moves a track between albums the
        // source album needs recomputing too, and after the write only the destination is visible.
        var previousAlbumId = track?.AlbumId;
        if (track is null)
        {
            track = new TrackEntity { CreatedAt = now };
            db.Tracks.Add(track);
            // Visible to the rest of this batch, so two files resolving to the same track
            // update one row rather than inserting a second.
            batchTracks[track.Id] = track;
        }

        track.Title = info.Title;
        track.SortTitle = info.SortTitle;
        track.AlbumId = album.Id;
        track.ArtistId = trackArtist.Id;
        track.TrackNumber = info.TrackNumber;
        track.DiscNumber = info.DiscNumber;
        track.DurationMs = info.DurationMs;
        track.FilePath = relPath;
        track.FileSize = info.FileSize;
        track.BitRate = info.BitRate;
        track.Format = info.Format;
        track.ContentType = info.ContentType;
        track.Year = info.Year;
        track.MusicBrainzId = info.MusicBrainzTrackId ?? track.MusicBrainzId;
        track.MusicBrainzRecordingId = info.MusicBrainzRecordingId ?? track.MusicBrainzRecordingId;
        track.FileModifiedAt = info.FileModifiedAt;
        track.UpdatedAt = now;

        // Keep the snapshot index in step with the row that was just written: the deletion
        // pass and every later batch resolve against it, not against the entity.
        if (snapshot is null)
        {
            snapshot = new TrackSnapshot { Id = track.Id, FilePath = relPath };
            snapshotsByPath.TryAdd(relPath, snapshot);
        }
        else if (!string.Equals(snapshot.FilePath, relPath, StringComparison.OrdinalIgnoreCase))
        {
            snapshotsByPath.Remove(snapshot.FilePath);
            snapshot.FilePath = relPath;
            snapshotsByPath.TryAdd(relPath, snapshot);
        }
        snapshot.AlbumId = track.AlbumId;
        if (track.MusicBrainzId is not null)
        {
            snapshot.MusicBrainzId = track.MusicBrainzId;
            snapshotsByMbid.TryAdd(track.MusicBrainzId, snapshot);
        }

        await SyncTrackGenresAsync(db, track, info.Genres, isNew, ct);
        await SyncTrackArtistsAsync(db, track, trackArtist, credits, isNew, now, ct);

        if (info.HasEmbeddedArt && albumArtHandled.Add(album.Id))
            ApplyAlbumArt(album, absPath, now);

        return (track, isNew, previousAlbumId != album.Id ? previousAlbumId : null);
    }

    private void ApplyAlbumArt(Album album, string absPath, DateTimeOffset now)
    {
        try
        {
            var pictures = new AtlTrack(absPath).EmbeddedPictures;
            if (pictures is not { Count: > 0 })
                return;

            var picture = pictures.FirstOrDefault(p => p.PicType == PictureInfo.PIC_TYPE.Front) ?? pictures[0];
            var data = picture.PictureData;
            if (data is not { Length: > 0 })
                return;

            if (album.CoverBlob is not null && data.AsSpan().SequenceEqual(album.CoverBlob))
                return;

            album.CoverBlob = data;
            album.UpdatedAt = now;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read embedded artwork from {FilePath}", absPath);
        }
    }

    private static async Task SyncTrackGenresAsync(
        Cosine.Infrastructure.Data.CosineDbContext db,
        TrackEntity track,
        string[] genreNames,
        bool isNew,
        CancellationToken ct)
    {
        var desired = new List<Genre>();
        foreach (var genreName in genreNames)
            desired.Add(await GetOrCreateGenreAsync(db, genreName, ct));

        if (isNew)
        {
            foreach (var genre in desired)
                db.TrackGenres.Add(new TrackGenre { TrackId = track.Id, GenreId = genre.Id });
            return;
        }

        var existing = await db.TrackGenres
            .Where(tg => tg.TrackId == track.Id)
            .ToListAsync(ct);

        var desiredIds = desired.Select(g => g.Id).ToHashSet();
        var existingIds = existing.Select(tg => tg.GenreId).ToHashSet();

        foreach (var stale in existing.Where(tg => !desiredIds.Contains(tg.GenreId)))
            db.TrackGenres.Remove(stale);

        foreach (var genre in desired.Where(g => !existingIds.Contains(g.Id)))
            db.TrackGenres.Add(new TrackGenre { TrackId = track.Id, GenreId = genre.Id });
    }

    /// <summary>
    /// Writes the track's artist credit - one <see cref="TrackArtist"/> row per credited artist,
    /// in credit order, ordinal 0 being the primary. Single-artist tracks get exactly one row, so
    /// "no rows" always means "not yet scanned" rather than "no guests".
    /// <para>
    /// Refuses to touch a track whose credit came from MusicBrainz. On a Lidarr-managed library
    /// the tags are strictly worse than what the enrichment found - Lidarr writes the release
    /// artist into every track's ARTIST frame - so letting a re-scan win would silently undo
    /// every guest credit on the next nightly scan.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Public for the same reason as <see cref="BuildStemIndex"/>: it is the part of the scan
    /// worth testing on its own, and the alternative is driving a whole scan over a fixture
    /// directory to assert on two join rows.
    /// </remarks>
    public static async Task SyncTrackArtistsAsync(
        Cosine.Infrastructure.Data.CosineDbContext db,
        TrackEntity track,
        Artist primary,
        IReadOnlyList<Services.ParsedCredit> credits,
        bool isNew,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var existing = isNew
            ? []
            : await db.TrackArtists.Where(ta => ta.TrackId == track.Id).ToListAsync(ct);

        if (existing.Any(ta => ta.Source == TrackArtistSource.MusicBrainz))
            return;

        // Resolved in credit order, deduplicated by artist: the row key is (track, artist), and a
        // tag that credits the same artist twice would otherwise be a duplicate-key failure that
        // takes the whole scan batch down.
        var desired = new List<(Artist Artist, Services.ParsedCredit Credit, int Ordinal)>();
        var seen = new HashSet<Guid>();
        foreach (var credit in credits)
        {
            var artist = desired.Count == 0
                ? primary
                : await GetOrCreateArtistAsync(db, credit.Name, GetSortName(credit.Name), null, now, ct);

            if (seen.Add(artist.Id))
                desired.Add((artist, credit, desired.Count));
        }

        var desiredIds = desired.Select(d => d.Artist.Id).ToHashSet();
        foreach (var stale in existing.Where(ta => !desiredIds.Contains(ta.ArtistId)))
            db.TrackArtists.Remove(stale);

        foreach (var (artist, credit, ordinal) in desired)
        {
            var row = existing.FirstOrDefault(ta => ta.ArtistId == artist.Id);
            if (row is null)
            {
                db.TrackArtists.Add(new TrackArtist
                {
                    TrackId = track.Id,
                    ArtistId = artist.Id,
                    Ordinal = ordinal,
                    CreditedAs = credit.Name,
                    JoinPhrase = credit.JoinPhrase,
                    Source = TrackArtistSource.Tag
                });
                continue;
            }

            // A retag can reorder or rephrase a credit without changing who is on it.
            row.Ordinal = ordinal;
            row.CreditedAs = credit.Name;
            row.JoinPhrase = credit.JoinPhrase;
            row.Source = TrackArtistSource.Tag;
        }
    }

    private static async Task<Artist> GetOrCreateArtistAsync(
        Cosine.Infrastructure.Data.CosineDbContext db, string name, string sortName, string? mbid,
        DateTimeOffset now, CancellationToken ct)
    {
        // Check the change tracker first: within a batch, entities added for earlier
        // tracks are not yet persisted, so a DB-only lookup would create duplicates.
        Artist? artist = null;
        if (mbid is not null)
        {
            artist = db.Artists.Local.FirstOrDefault(a => a.MusicBrainzId == mbid)
                ?? await db.Artists.FirstOrDefaultAsync(a => a.MusicBrainzId == mbid, ct);
        }

        // Name fallback only claims artists without a (different) MusicBrainz id,
        // so two distinct artists sharing a name are kept apart once tagged.
        artist ??= db.Artists.Local.FirstOrDefault(a => a.Name == name && (mbid is null || a.MusicBrainzId is null))
            ?? await db.Artists.FirstOrDefaultAsync(a => a.Name == name && (mbid == null || a.MusicBrainzId == null), ct);

        if (artist is not null)
        {
            var changed = false;
            if (artist.Name != name || artist.SortName != sortName)
            {
                artist.Name = name;
                artist.SortName = sortName;
                changed = true;
            }
            if (mbid is not null && artist.MusicBrainzId != mbid)
            {
                artist.MusicBrainzId = mbid;
                changed = true;
            }
            if (changed) artist.UpdatedAt = now;
            return artist;
        }

        artist = new Artist
        {
            Name = name,
            SortName = sortName,
            MusicBrainzId = mbid,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Artists.Add(artist);
        return artist;
    }

    private static async Task<Album> GetOrCreateAlbumAsync(
        Cosine.Infrastructure.Data.CosineDbContext db, string name, string sortName, Guid artistId, int? year,
        string? mbid, string? releaseType, DateTimeOffset now, CancellationToken ct)
    {
        Album? album = null;
        if (mbid is not null)
        {
            album = db.Albums.Local.FirstOrDefault(a => a.MusicBrainzId == mbid)
                ?? await db.Albums.FirstOrDefaultAsync(a => a.MusicBrainzId == mbid, ct);
        }

        album ??= db.Albums.Local.FirstOrDefault(a => a.Name == name && a.ArtistId == artistId && (mbid is null || a.MusicBrainzId is null))
            ?? await db.Albums.FirstOrDefaultAsync(a => a.Name == name && a.ArtistId == artistId && (mbid == null || a.MusicBrainzId == null), ct);

        if (album is not null)
        {
            var changed = false;
            if (album.Name != name || album.SortName != sortName)
            {
                album.Name = name;
                album.SortName = sortName;
                changed = true;
            }
            if (album.ArtistId != artistId)
            {
                album.ArtistId = artistId;
                changed = true;
            }
            if (year is not null && album.Year != year)
            {
                album.Year = year;
                changed = true;
            }
            if (mbid is not null && album.MusicBrainzId != mbid)
            {
                album.MusicBrainzId = mbid;
                changed = true;
            }
            if (releaseType is not null && album.ReleaseType != releaseType)
            {
                album.ReleaseType = releaseType;
                changed = true;
            }
            if (changed) album.UpdatedAt = now;
            return album;
        }

        album = new Album
        {
            Name = name,
            SortName = sortName,
            ArtistId = artistId,
            Year = year,
            MusicBrainzId = mbid,
            ReleaseType = releaseType,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Albums.Add(album);
        return album;
    }

    private static async Task<Genre> GetOrCreateGenreAsync(
        Cosine.Infrastructure.Data.CosineDbContext db, string name, CancellationToken ct)
    {
        var genre = db.Genres.Local.FirstOrDefault(g => g.Name == name)
            ?? await db.Genres.FirstOrDefaultAsync(g => g.Name == name, ct);
        if (genre is not null) return genre;

        genre = new Genre { Name = name };
        db.Genres.Add(genre);
        return genre;
    }

    private static async Task RecomputeAlbumStatsAsync(
        Cosine.Infrastructure.Data.CosineDbContext db,
        HashSet<Guid> albumIds,
        CancellationToken ct)
    {
        if (albumIds.Count == 0) return;

        var stats = await db.Tracks
            .Where(t => albumIds.Contains(t.AlbumId))
            .GroupBy(t => t.AlbumId)
            .Select(g => new { AlbumId = g.Key, Count = g.Count(), Duration = g.Sum(t => t.DurationMs ?? 0) })
            .ToDictionaryAsync(s => s.AlbumId, ct);

        foreach (var albumId in albumIds)
        {
            var album = db.Albums.Local.FirstOrDefault(a => a.Id == albumId)
                ?? await db.Albums.FindAsync([albumId], ct);
            if (album is null) continue;

            var (count, duration) = stats.TryGetValue(albumId, out var s)
                ? (s.Count, s.Duration)
                : (0, 0L);

            album.TrackCount = count;
            album.DurationMs = duration;
        }

        await db.SaveChangesAsync(ct);
    }

    // Reconciles lyrics against the library. Sidecars are matched to tracks by name and
    // synced for every track (they change independently of the audio files); embedded
    // lyrics act as a fallback, kept only for tracks that have no sidecar.
    //
    // Returns the ids of every track whose lyric rows were added, changed, or removed this
    // scan. A lyric-only change (a sidecar added/edited next to an *unchanged* audio file)
    // never appears in touchedTrackIds, so the caller unions these into the search rebuild
    // set to re-index LyricsVector.
    private async Task<IReadOnlyCollection<Guid>> SyncLyricsAsync(
        Cosine.Infrastructure.Data.CosineDbContext db,
        Guid scanLogId,
        List<(string Abs, string Rel)> lyricFiles,
        Dictionary<string, Services.LyricsParsing.Parsed> embeddedLyricsByRel,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var affected = new HashSet<Guid>();

        // Map each track's audio path (minus extension) -> id, to attach sidecars by name.
        var tracks = await db.Tracks.Select(t => new { t.Id, t.FilePath }).ToListAsync(ct);
        var trackByStem = BuildStemIndex(tracks.Select(t => (t.FilePath, t.Id)));
        var trackIdByPath = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
            trackIdByPath[t.FilePath] = t.Id;

        // ---- sidecar reconciliation ----
        var existingSidecar = await db.Lyrics
            .Where(l => l.Source == Lyric.SourceSidecar)
            .ToListAsync(ct);
        var sidecarByPath = existingSidecar
            .Where(l => l.SourcePath is not null)
            .ToDictionary(l => l.SourcePath!, StringComparer.OrdinalIgnoreCase);

        var seenSidecarPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedOrUpdated = 0;

        foreach (var (abs, rel) in lyricFiles)
        {
            var match = MatchLyricToTrack(rel, trackByStem);
            if (match is null)
                continue; // orphan sidecar - no track with a matching name

            var (trackId, fileLang) = match.Value;
            seenSidecarPaths.Add(rel);

            var fi = new FileInfo(abs);
            sidecarByPath.TryGetValue(rel, out var existing);
            if (existing is not null
                && existing.TrackId == trackId
                && existing.SourceSize == fi.Length
                && existing.SourceModifiedAt is { } mod
                && (mod - fi.LastWriteTimeUtc).Duration() <= MTimeTolerance)
            {
                continue; // unchanged since last scan
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(abs, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read lyric sidecar {Path}", abs);
                continue;
            }

            var parsed = Services.LyricsParsing.ParseSidecar(content, fileLang);
            if (parsed is null)
                continue;

            var row = existing;
            if (row is null)
            {
                row = new Lyric { CreatedAt = now, Source = Lyric.SourceSidecar, SourcePath = rel };
                db.Lyrics.Add(row);
            }
            ApplyParsedLyrics(row, parsed, trackId, now);
            row.SourceSize = fi.Length;
            row.SourceModifiedAt = fi.LastWriteTimeUtc;
            affected.Add(trackId);
            addedOrUpdated++;
        }

        // Remove sidecar rows whose file has disappeared.
        var removed = 0;
        foreach (var stale in existingSidecar.Where(l => l.SourcePath is not null && !seenSidecarPaths.Contains(l.SourcePath!)))
        {
            db.Lyrics.Remove(stale);
            affected.Add(stale.TrackId);
            removed++;
        }

        // ---- embedded fallback (only for files re-read this scan) ----
        foreach (var (rel, parsed) in embeddedLyricsByRel)
        {
            if (!trackIdByPath.TryGetValue(rel, out var trackId))
                continue;

            var priorEmbedded = await db.Lyrics
                .Where(l => l.TrackId == trackId && l.Source == Lyric.SourceEmbedded)
                .ToListAsync(ct);
            db.Lyrics.RemoveRange(priorEmbedded);

            var row = new Lyric { CreatedAt = now, Source = Lyric.SourceEmbedded };
            db.Lyrics.Add(row);
            ApplyParsedLyrics(row, parsed, trackId, now);
            affected.Add(trackId);
        }

        await db.SaveChangesAsync(ct);

        // Embedded lyrics are only a fallback: drop them for any track that also has a
        // sidecar. Done as a set-based delete so it self-heals regardless of scan diff.
        // Capture the affected track ids before the delete so their LyricsVector is rebuilt.
        var redundantTrackIds = await db.Lyrics
            .Where(l => l.Source == Lyric.SourceEmbedded
                && db.Lyrics.Any(s => s.Source == Lyric.SourceSidecar && s.TrackId == l.TrackId))
            .Select(l => l.TrackId)
            .Distinct()
            .ToListAsync(ct);

        var redundant = await db.Lyrics
            .Where(l => l.Source == Lyric.SourceEmbedded
                && db.Lyrics.Any(s => s.Source == Lyric.SourceSidecar && s.TrackId == l.TrackId))
            .ExecuteDeleteAsync(ct);

        affected.UnionWith(redundantTrackIds);

        if (addedOrUpdated > 0 || removed > 0 || redundant > 0)
        {
            _logger.LogInformation(
                "Scan {ScanLogId}: lyrics - {Upserted} sidecars upserted, {Removed} removed, {Redundant} embedded superseded",
                scanLogId, addedOrUpdated, removed, redundant);
        }

        return affected;
    }

    private static void ApplyParsedLyrics(Lyric row, Services.LyricsParsing.Parsed p, Guid trackId, DateTimeOffset now)
    {
        row.TrackId = trackId;
        row.Lang = p.Lang;
        row.Synced = p.Synced;
        row.OffsetMs = p.OffsetMs;
        row.DisplayArtist = p.DisplayArtist;
        row.DisplayTitle = p.DisplayTitle;
        row.LinesJson = p.LinesJson;
        row.PlainText = p.PlainText;
        row.UpdatedAt = now;
    }

    /// <summary>
    /// Indexes tracks by their path minus the audio extension, which is how a sidecar finds its
    /// track. An album carrying both <c>song.flac</c> and <c>song.mp3</c> collides here: plain
    /// last-write-wins handed <c>song.lrc</c> to whichever the enumeration reached last, so the
    /// lyrics moved between the two on re-scans for no reason the user can see. The winner is now
    /// the lexicographically first full path - still arbitrary between two equally valid
    /// candidates, but *stable*, so a given library always resolves the same way.
    /// </summary>
    public static Dictionary<string, Guid> BuildStemIndex(IEnumerable<(string FilePath, Guid Id)> tracks)
    {
        var byStem = new Dictionary<string, (string FilePath, Guid Id)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
        {
            var stem = StripExtension(t.FilePath);
            if (byStem.TryGetValue(stem, out var incumbent)
                && string.CompareOrdinal(incumbent.FilePath, t.FilePath) <= 0)
            {
                continue;
            }
            byStem[stem] = t;
        }

        return byStem.ToDictionary(kv => kv.Key, kv => kv.Value.Id, StringComparer.OrdinalIgnoreCase);
    }

    // Attach "dir/song.lrc" (or "dir/song.en.lrc") to the track at "dir/song.<audio-ext>".
    private static (Guid TrackId, string? Lang)? MatchLyricToTrack(string lyricRel, Dictionary<string, Guid> trackByStem)
    {
        var stem = StripExtension(lyricRel); // drops .lrc/.elrc
        if (trackByStem.TryGetValue(stem, out var id))
            return (id, null);

        // Retry treating the last remaining extension as a language suffix.
        var slash = stem.LastIndexOf('/');
        var dot = stem.LastIndexOf('.');
        if (dot > slash + 1 && dot < stem.Length - 1)
        {
            var baseStem = stem[..dot];
            var lang = stem[(dot + 1)..];
            if (trackByStem.TryGetValue(baseStem, out var langId))
                return (langId, lang);
        }
        return null;
    }

    // Removes only the final extension, respecting '/'-normalized paths.
    private static string StripExtension(string path)
    {
        var slash = path.LastIndexOf('/');
        var dot = path.LastIndexOf('.');
        return dot > slash ? path[..dot] : path;
    }

    private async Task FlushLibraryCacheAsync(IConnectionMultiplexer? redis)
    {
        if (redis is null) return;

        var server = redis.GetServer(redis.GetEndPoints().First());
        // One SCAN over the "cosine:" namespace, matched client-side. SCAN takes a single
        // pattern, so a pattern per prefix meant ten full passes over the keyspace per scan.
        var keys = server.Keys(pattern: "cosine:*")
            .Where(k => Services.LibraryCache.IsLibraryKey(k.ToString()))
            .ToList();

        if (keys.Count > 0)
        {
            var db = redis.GetDatabase();
            foreach (var batch in keys.Chunk(100))
            {
                await db.KeyDeleteAsync(batch.Select(k => (RedisKey)k).ToArray());
            }
            _logger.LogInformation("Flushed {Count} Redis cache keys after scan", keys.Count);
        }
    }
}
