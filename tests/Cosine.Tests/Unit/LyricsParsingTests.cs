using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class LyricsParsingTests
{
    [Fact]
    public void ParseSidecar_SyncedLrc_ExtractsTimestampsMetadataAndOffset()
    {
        var lrc = string.Join('\n',
            "[ti:My Song]",
            "[ar:The Artist]",
            "[la:en]",
            "[offset:+250]",
            "[00:12.34]Hello world",
            "[00:15.00]Line two");

        var parsed = LyricsParsing.ParseSidecar(lrc, fileLang: null);

        Assert.NotNull(parsed);
        Assert.True(parsed!.Synced);
        Assert.Equal("en", parsed.Lang);
        Assert.Equal("The Artist", parsed.DisplayArtist);
        Assert.Equal("My Song", parsed.DisplayTitle);
        Assert.Equal(250, parsed.OffsetMs);
        Assert.Equal(2, parsed.Lines.Count);
        Assert.Equal(12340, parsed.Lines[0].Start);
        Assert.Equal("Hello world", parsed.Lines[0].Value);
        Assert.Equal(15000, parsed.Lines[1].Start);
        Assert.Equal("Hello world\nLine two", parsed.PlainText);
    }

    [Fact]
    public void ParseSidecar_PlainText_IsUnsyncedWithNullStarts()
    {
        var parsed = LyricsParsing.ParseSidecar("First line\nSecond line\n", fileLang: null);

        Assert.NotNull(parsed);
        Assert.False(parsed!.Synced);
        Assert.All(parsed.Lines, l => Assert.Null(l.Start));
        Assert.Equal(2, parsed.Lines.Count);
        Assert.Equal("First line", parsed.Lines[0].Value);
    }

    [Fact]
    public void ParseSidecar_FileLang_UsedWhenNoTag()
    {
        var parsed = LyricsParsing.ParseSidecar("[00:01.00]Bonjour", fileLang: "fr");

        Assert.NotNull(parsed);
        Assert.Equal("fr", parsed!.Lang);
    }

    [Fact]
    public void ParseSidecar_NoLangAnywhere_DefaultsToXxx()
    {
        var parsed = LyricsParsing.ParseSidecar("[00:01.00]Hi", fileLang: null);

        Assert.NotNull(parsed);
        Assert.Equal("xxx", parsed!.Lang);
    }

    [Fact]
    public void ParseSidecar_EnhancedWordTimestamps_StrippedForLineText()
    {
        // Enhanced LRC A2: inline <mm:ss.xx> word cues must not leak into line text.
        var parsed = LyricsParsing.ParseSidecar("[00:12.00]Hello <00:12.80>world", fileLang: null);

        Assert.NotNull(parsed);
        Assert.True(parsed!.Synced);
        Assert.DoesNotContain("<", parsed.Lines[0].Value);
        Assert.Equal(12000, parsed.Lines[0].Start);
    }

    [Fact]
    public void ParseSidecar_EmptyOrWhitespace_ReturnsNull()
    {
        Assert.Null(LyricsParsing.ParseSidecar("", fileLang: null));
        Assert.Null(LyricsParsing.ParseSidecar("   \n  ", fileLang: null));
    }

    [Fact]
    public void DeserializeLines_RoundTripsParserOutput()
    {
        var parsed = LyricsParsing.ParseSidecar("[00:01.00]A\n[00:02.00]B", fileLang: null);
        var lines = LyricsParsing.DeserializeLines(parsed!.LinesJson);

        Assert.Equal(2, lines.Count);
        Assert.Equal(1000, lines[0].Start);
        Assert.Equal("B", lines[1].Value);
    }

    [Fact]
    public void DeserializeLines_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(LyricsParsing.DeserializeLines(null));
        Assert.Empty(LyricsParsing.DeserializeLines(""));
    }
}
