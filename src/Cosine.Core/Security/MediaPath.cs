namespace Cosine.Core.Security;

/// <summary>
/// Resolves a stored, scanner-produced <c>Track.FilePath</c> against the media root and refuses
/// anything that lands outside it.
///
/// Nothing user-supplied reaches these paths today - but the scanner itself can produce one that
/// escapes: <c>ScanJob.ToRelativePath</c> falls back to the *absolute* path when a file does not
/// sit under the configured root, and <see cref="Path.Combine(string, string)"/> silently discards
/// the root when handed an absolute second argument. A retagged library, a moved root, or a future
/// caller that takes the path from a request would all stream files from anywhere on disk.
///
/// The check is lexical: <see cref="Path.GetFullPath(string)"/> collapses <c>..</c> segments but
/// does not follow symlinks, so a symlink inside the library that points outside it still resolves.
/// That is deliberate - symlinked media directories are a normal self-hosted layout, and the target
/// of one is an explicit choice by whoever set the library up.
/// </summary>
public static class MediaPath
{
    /// <summary>
    /// Returns the absolute path of <paramref name="relativePath"/> under
    /// <paramref name="rootPath"/>, or <c>null</c> when it escapes the root (or either input is
    /// empty or malformed).
    /// </summary>
    public static string? Resolve(string? rootPath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(relativePath))
            return null;

        string root, combined;
        try
        {
            root = Path.GetFullPath(rootPath);
            combined = Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // Compare against the root *with* its separator so "/media-other/x" cannot pass as a
        // child of "/media".
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return combined.StartsWith(prefix, comparison) ? combined : null;
    }
}
