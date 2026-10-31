using System.Text;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Repairs common tag-encoding damage:
/// - C1 control characters (U+0080–U+009F) that appear when CP-1252 text was
///   decoded as ISO-8859-1 (typical for mis-tagged ID3v2 frames).
/// - U+FFFD replacement characters from strict UTF-8 decoding of Vorbis comments
///   that actually contain CP-1252 bytes (e.g. 0x92 for ’), recovered by
///   re-reading the FLAC comment block with a lenient decoder.
/// </summary>
public static class TagEncodingRepair
{
    public const char ReplacementChar = '�';

    // CP-1252 mapping for the 0x80–0x9F range; '\0' marks undefined bytes.
    private static readonly char[] Cp1252C1Map =
    {
        '€', '\0', '‚', 'ƒ', '„', '…', '†', '‡',
        'ˆ', '‰', 'Š', '‹', 'Œ', '\0', 'Ž', '\0',
        '\0', '‘', '’', '“', '”', '•', '–', '-',
        '˜', '™', 'š', '›', 'œ', '\0', 'ž', 'Ÿ'
    };

    /// <summary>Remaps C1 control characters through CP-1252; drops undefined ones.</summary>
    public static string? RepairC1Controls(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Any(c => c is >= '\u0080' and <= '\u009F'))
            return value;

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is >= '\u0080' and <= '\u009F')
            {
                var mapped = Cp1252C1Map[c - 0x80];
                if (mapped != '\0') sb.Append(mapped);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Strict UTF-8; falls back to CP-1252-style Latin-1 when the bytes are invalid UTF-8.</summary>
    public static string DecodeLenient(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (ArgumentException)
        {
            return RepairC1Controls(Encoding.Latin1.GetString(bytes))!;
        }
    }

    /// <summary>
    /// Reads the VORBIS_COMMENT block of a native FLAC file with lenient text decoding.
    /// Returns null when the file is not FLAC or has no comment block.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? TryReadFlacVorbisComments(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);

            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4 || !magic.SequenceEqual("fLaC"u8))
                return null;

            Span<byte> header = stackalloc byte[4];
            while (fs.Read(header) == 4)
            {
                var isLast = (header[0] & 0x80) != 0;
                var blockType = header[0] & 0x7F;
                var size = (header[1] << 16) | (header[2] << 8) | header[3];

                if (blockType == 4)
                {
                    var block = new byte[size];
                    fs.ReadExactly(block);
                    return ParseVorbisComments(block);
                }

                fs.Seek(size, SeekOrigin.Current);
                if (isLast) break;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }

    private static Dictionary<string, string>? ParseVorbisComments(byte[] block)
    {
        try
        {
            var comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pos = 0;

            var vendorLength = ReadUInt32Le(block, ref pos);
            pos += (int)vendorLength;

            var count = ReadUInt32Le(block, ref pos);
            for (var i = 0; i < count; i++)
            {
                var length = (int)ReadUInt32Le(block, ref pos);
                if (pos + length > block.Length) return comments;

                var field = block.AsSpan(pos, length);
                pos += length;

                var eq = field.IndexOf((byte)'=');
                if (eq <= 0) continue;

                var key = Encoding.ASCII.GetString(field[..eq]);
                var value = DecodeLenient(field[(eq + 1)..]).Trim();
                if (value.Length > 0)
                    comments.TryAdd(key, value);
            }

            return comments;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static uint ReadUInt32Le(byte[] data, ref int pos)
    {
        var value = BitConverter.ToUInt32(data, pos);
        pos += 4;
        return value;
    }
}
