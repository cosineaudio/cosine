using System.Text.Json;
using System.Text.RegularExpressions;
using ATL;
using Cosine.Core.Dtos;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Turns ATL <see cref="LyricsInfo"/> (parsed from a .lrc/.elrc sidecar or an
/// embedded tag) into the flat, storable shape the scan persists and the API serves.
/// Line-level only: enhanced (word/syllable) timing is collapsed to the line text.
/// </summary>
public static class LyricsParsing
{
    // Defensive strip of enhanced LRC A2 inline word timestamps like <00:12.34>,
    // in case a source leaves them in the phrase text.
    private static readonly Regex InlineWordTag = new(@"<\d{1,2}:\d{2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Parsed(
        string Lang,
        bool Synced,
        int? OffsetMs,
        string? DisplayArtist,
        string? DisplayTitle,
        IReadOnlyList<LyricLineDto> Lines,
        string LinesJson,
        string PlainText);

    /// <summary>Parse raw .lrc/.elrc file contents. <paramref name="fileLang"/> is a language
    /// hint from a filename suffix (e.g. song.en.lrc). Returns null when there is no usable text.</summary>
    public static Parsed? ParseSidecar(string content, string? fileLang)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var info = new LyricsInfo();
        info.Parse(content);
        return FromLyricsInfo(info, fileLang);
    }

    /// <summary>Map an already-parsed ATL LyricsInfo (embedded tag or sidecar). Returns null when empty.</summary>
    public static Parsed? FromLyricsInfo(LyricsInfo info, string? fileLang = null)
    {
        var lines = new List<LyricLineDto>();
        var synced = info.SynchronizedLyrics.Count > 0;

        if (synced)
        {
            foreach (var phrase in info.SynchronizedLyrics.OrderBy(p => p.TimestampStart))
            {
                var text = Clean(phrase.Text);
                // TimestampStart is milliseconds; a negative value means "unset".
                lines.Add(new LyricLineDto(phrase.TimestampStart >= 0 ? phrase.TimestampStart : null, text));
            }
        }
        else if (!string.IsNullOrWhiteSpace(info.UnsynchronizedLyrics))
        {
            foreach (var raw in info.UnsynchronizedLyrics.Replace("\r\n", "\n").Split('\n'))
                lines.Add(new LyricLineDto(null, Clean(raw)));
        }

        // Drop trailing blank lines that split() may leave behind.
        while (lines.Count > 0 && string.IsNullOrEmpty(lines[^1].Value))
            lines.RemoveAt(lines.Count - 1);

        if (lines.Count == 0)
            return null;

        var meta = info.Metadata ?? new Dictionary<string, string>();

        var lang = FirstNonEmpty(info.LanguageCode, Get(meta, "la"), fileLang) ?? "xxx";
        var displayArtist = FirstNonEmpty(Get(meta, "ar"));
        var displayTitle = FirstNonEmpty(Get(meta, "ti"));
        int? offset = TryParseOffset(Get(meta, "offset"));

        var plain = string.Join("\n", lines.Select(l => l.Value));
        var linesJson = JsonSerializer.Serialize(lines, Json);

        return new Parsed(lang, synced, offset, displayArtist, displayTitle, lines, linesJson, plain);
    }

    public static List<LyricLineDto> DeserializeLines(string? linesJson)
    {
        if (string.IsNullOrWhiteSpace(linesJson))
            return [];
        return JsonSerializer.Deserialize<List<LyricLineDto>>(linesJson, Json) ?? [];
    }

    private static string Clean(string? text)
        => InlineWordTag.Replace(text ?? string.Empty, string.Empty).Trim();

    private static string? Get(IDictionary<string, string> meta, string key)
        => meta.TryGetValue(key, out var v) ? v : null;

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static int? TryParseOffset(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        // Values look like "+250" / "-100" / "250".
        return int.TryParse(raw.Trim().TrimStart('+'), out var ms) ? ms : null;
    }
}
