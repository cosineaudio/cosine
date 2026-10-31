namespace Cosine.Infrastructure.Services;

/// <summary>
/// Identifies an image by its magic bytes. Artwork is stored as an opaque blob - embedded
/// tag pictures are routinely PNG or WebP - and every response used to be labelled
/// <c>image/jpeg</c> regardless, which browsers only tolerate because they sniff too.
/// </summary>
public static class ImageFormats
{
    public const string Fallback = "application/octet-stream";

    public static string DetectContentType(ReadOnlySpan<byte> bytes)
    {
        if (StartsWith(bytes, [0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (StartsWith(bytes, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (StartsWith(bytes, "GIF87a"u8) || StartsWith(bytes, "GIF89a"u8)) return "image/gif";
        if (StartsWith(bytes, "BM"u8)) return "image/bmp";
        // RIFF....WEBP
        if (StartsWith(bytes, "RIFF"u8) && bytes.Length >= 12 && bytes[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";

        return Fallback;
    }

    /// <summary>Whether a resized copy should keep an alpha channel (PNG) rather than become JPEG.</summary>
    public static bool HasTransparency(ReadOnlySpan<byte> bytes) =>
        DetectContentType(bytes) is "image/png" or "image/gif" or "image/webp";

    private static bool StartsWith(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> prefix) =>
        bytes.Length >= prefix.Length && bytes[..prefix.Length].SequenceEqual(prefix);
}
