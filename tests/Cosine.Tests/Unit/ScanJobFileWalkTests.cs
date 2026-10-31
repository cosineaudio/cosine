using Cosine.Infrastructure.Jobs;

namespace Cosine.Tests.Unit;

/// <summary>
/// The scan walked the library with <see cref="SearchOption.AllDirectories"/>, which follows
/// directory symlinks - a link pointing at one of its own ancestors made the walk enumerate
/// forever, so the scan never finished and never reported an error.
/// </summary>
public class ScanJobFileWalkTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"cosine-walk-{Guid.NewGuid():N}");

    public ScanJobFileWalkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Touch(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
        return full.Replace('\\', '/');
    }

    private void Link(string linkRelativePath, string targetRelativePath)
        => Directory.CreateSymbolicLink(
            Path.Combine(_root, linkRelativePath), Path.Combine(_root, targetRelativePath));

    [Fact]
    public void Walk_ReturnsEveryFileInEveryDirectory()
    {
        var expected = new[]
        {
            Touch("top.mp3"),
            Touch("artist/album/one.flac"),
            Touch("artist/album/art.jpg"),
            Touch("artist/other/deep/two.flac")
        };

        var found = ScanJob.EnumerateFilesWithoutCycles(_root);

        Assert.Equal(
            expected.OrderBy(p => p, StringComparer.Ordinal),
            found.OrderBy(p => p, StringComparer.Ordinal));
    }

    [SymlinkFact]
    public void Walk_WithASymlinkCycle_TerminatesAndYieldsEachFileOnce()
    {
        var song = Touch("music/song.flac");
        Link("music/loop", "music"); // a link to its own parent

        Assert.Equal([song], ScanJob.EnumerateFilesWithoutCycles(_root).ToList());
    }

    [SymlinkFact]
    public void Walk_WithADeeperSymlinkCycle_Terminates()
    {
        var song = Touch("a/b/c/song.flac");
        Link("a/b/c/back", "a"); // grandparent, two levels up

        Assert.Equal([song], ScanJob.EnumerateFilesWithoutCycles(_root).ToList());
    }

    [SymlinkFact]
    public void Walk_WithASiblingSymlink_VisitsTheTargetOnlyOnce()
    {
        var song = Touch("real/song.flac");
        Link("mirror", "real");

        // Whichever route won, the file is enumerated once - not once per path to it.
        Assert.Equal(song, Assert.Single(ScanJob.EnumerateFilesWithoutCycles(_root)));
    }

    [UnprivilegedUnixFact]
    public void Walk_SkipsUnreadableDirectoriesInsteadOfFailingTheScan()
    {
        if (OperatingSystem.IsWindows()) return; // the attribute skips it; this is for the analyzer

        var song = Touch("readable/song.flac");
        Touch("locked/hidden.flac");

        var locked = Path.Combine(_root, "locked");
        var mode = File.GetUnixFileMode(locked);
        try
        {
            File.SetUnixFileMode(locked, UnixFileMode.None);
            Assert.Equal([song], ScanJob.EnumerateFilesWithoutCycles(_root).ToList());
        }
        finally
        {
            File.SetUnixFileMode(locked, mode);
        }
    }

    /// <summary>Symlink creation needs a privilege or developer mode on Windows.</summary>
    private sealed class SymlinkFactAttribute : FactAttribute
    {
        public SymlinkFactAttribute()
        {
            if (!Supported.Value) Skip = "symlinks cannot be created here";
        }

        private static readonly Lazy<bool> Supported = new(() =>
        {
            var probe = Path.Combine(Path.GetTempPath(), $"cosine-linkprobe-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateSymbolicLink(probe, Path.GetTempPath());
                Directory.Delete(probe);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        });
    }

    /// <summary>Unix permission bits, and root ignores them.</summary>
    private sealed class UnprivilegedUnixFactAttribute : FactAttribute
    {
        public UnprivilegedUnixFactAttribute()
        {
            if (OperatingSystem.IsWindows()) Skip = "unix file modes only";
            else if (Environment.IsPrivilegedProcess) Skip = "running privileged - permissions are not enforced";
        }
    }
}
