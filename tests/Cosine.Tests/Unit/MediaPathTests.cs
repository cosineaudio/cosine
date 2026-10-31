using Cosine.Core.Security;

namespace Cosine.Tests.Unit;

/// <summary>
/// Containment for stored media paths (#49). <c>Path.Combine</c> silently drops the root when the
/// second argument is absolute - which <c>ScanJob.ToRelativePath</c> can produce - so the resolver
/// has to reject rather than combine.
/// </summary>
public class MediaPathTests
{
    [Theory]
    [InlineData("Artist/Album/song.flac")]
    [InlineData("song.flac")]
    [InlineData("Artist/Album with spaces/01 - song.mp3")]
    [InlineData("Artist/./Album/song.flac")]
    [InlineData("Artist/Album/../Album/song.flac")]
    public void Resolve_PathsUnderTheRoot_AreResolved(string relative)
    {
        var resolved = MediaPath.Resolve("/media", relative);

        Assert.NotNull(resolved);
        Assert.StartsWith("/media/", resolved);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("Artist/../../etc/passwd")]
    [InlineData("/etc/passwd")]                 // absolute: Path.Combine would discard the root
    [InlineData("/media-other/song.flac")]      // shares a textual prefix, is not a child
    [InlineData("..")]
    public void Resolve_PathsOutsideTheRoot_AreRejected(string relative)
    {
        Assert.Null(MediaPath.Resolve("/media", relative));
    }

    [Fact]
    public void Resolve_TheRootItself_IsRejected()
    {
        // The root is a directory, never a media file - and accepting it would let "" through.
        Assert.Null(MediaPath.Resolve("/media", "."));
    }

    [Theory]
    [InlineData(null, "song.flac")]
    [InlineData("", "song.flac")]
    [InlineData("   ", "song.flac")]
    [InlineData("/media", null)]
    [InlineData("/media", "")]
    public void Resolve_EmptyInputs_AreRejected(string? root, string? relative)
    {
        Assert.Null(MediaPath.Resolve(root, relative));
    }

    [Fact]
    public void Resolve_TrailingSeparatorOnTheRoot_IsHandled()
    {
        Assert.Equal(
            MediaPath.Resolve("/media", "a/song.flac"),
            MediaPath.Resolve("/media/", "a/song.flac"));
    }

    [Fact]
    public void Resolve_RelativeRoot_IsMadeAbsolute()
    {
        var resolved = MediaPath.Resolve("media", "song.flac");

        Assert.NotNull(resolved);
        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith(Path.Combine("media", "song.flac"), resolved);
    }
}
