using Cosine.Audio;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Integration;

/// <summary>
/// The decode seam, against real files produced by a real ffmpeg. Everything downstream of this
/// is covered by synthetic-signal unit tests, but nothing else checks that the arguments handed
/// to ffmpeg actually yield mono 22.05 kHz float samples in the order the analyzer assumes - a
/// wrong <c>-f</c> or a missing <c>-ac</c> would produce a plausible-looking vector built from
/// interleaved or byte-swapped garbage.
/// </summary>
public sealed class AudioDecoderTests : IAsyncLifetime
{
    private string _directory = null!;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"cosine-decode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_tone_survives_decoding_with_its_pitch_intact()
    {
        // 5 s of 440 Hz, written as stereo 44.1 kHz so the decoder has to downmix and resample.
        var path = await SynthesizeAsync("tone.flac", "sine=frequency=440:sample_rate=44100:duration=5", stereo: true);

        var samples = await new AudioDecoder("ffmpeg").DecodeExcerptAsync(path, 5_000);

        Assert.NotNull(samples);
        Assert.InRange(samples!.Length, AudioAnalyzer.SampleRate * 4, AudioAnalyzer.SampleRate * 6);

        // If the samples were misinterpreted the chroma peak would land anywhere but A.
        var features = AudioAnalyzer.Extract(samples);
        Assert.Equal(9, features.KeyRoot);
        Assert.All(features.Vector, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public async Task The_excerpt_is_taken_from_the_middle_of_a_long_track()
    {
        // Silence, then a tone, then silence - 180 s total. A centred 120 s excerpt lands on the
        // tone; decoding from zero would return mostly silence and analyse the wrong thing.
        var path = await SynthesizeAsync(
            "long.flac",
            "aevalsrc='if(between(t,60,120), 0.5*sin(2*PI*440*t), 0)':s=44100:d=180",
            stereo: false);

        var samples = await new AudioDecoder("ffmpeg").DecodeExcerptAsync(path, 180_000);

        Assert.NotNull(samples);

        double peak = 0;
        foreach (var sample in samples!) peak = Math.Max(peak, Math.Abs(sample));

        Assert.True(peak > 0.1, $"the centred excerpt was near-silent (peak {peak:F4})");
    }

    [Fact]
    public async Task A_track_shorter_than_the_excerpt_is_decoded_whole()
    {
        var path = await SynthesizeAsync("short.flac", "sine=frequency=330:sample_rate=44100:duration=3", stereo: false);

        var samples = await new AudioDecoder("ffmpeg").DecodeExcerptAsync(path, 3_000);

        Assert.NotNull(samples);
        Assert.InRange(samples!.Length, AudioAnalyzer.SampleRate * 2, AudioAnalyzer.SampleRate * 4);
    }

    [Fact]
    public async Task Silence_decodes_and_analyses_without_failing()
    {
        var path = await SynthesizeAsync("quiet.flac", "anullsrc=r=44100:cl=mono:d=4", stereo: false);

        var samples = await new AudioDecoder("ffmpeg").DecodeExcerptAsync(path, 4_000);

        Assert.NotNull(samples);
        Assert.All(AudioAnalyzer.Extract(samples!).Vector, v => Assert.True(float.IsFinite(v)));
    }

    /// <summary>
    /// One unreadable file must surface as an exception the analysis job can log and skip, not as
    /// an empty vector that would be stored as though the track had genuinely been analysed.
    /// </summary>
    [Fact]
    public async Task A_file_that_is_not_audio_is_reported_rather_than_silently_empty()
    {
        var path = Path.Combine(_directory, "not-audio.flac");
        await File.WriteAllTextAsync(path, "this is not a FLAC file");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AudioDecoder("ffmpeg").DecodeExcerptAsync(path, 1_000));
    }

    /// <summary>
    /// A file that decodes, but only barely. Damaged FLACs give ffmpeg a clean exit code and a
    /// handful of frames, and testing only for "nothing at all" let a 192-second track that
    /// yielded 14 milliseconds through - long enough to build a full feature vector out of one
    /// zero-padded frame, and that vector then went into the index.
    /// </summary>
    [Fact]
    public async Task A_decode_far_shorter_than_the_track_is_reported_rather_than_analysed()
    {
        var path = await SynthesizeAsync("stub.flac", "sine=frequency=440:sample_rate=44100:duration=3", stereo: false);

        // Three real seconds of audio, but the track claims sixty: whatever is on disk, this is
        // not the file the library thinks it is.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AudioDecoder("ffmpeg").DecodeExcerptAsync(path, 60_000));

        Assert.Contains("decoded only", error.Message);
    }

    /// <summary>
    /// The other side of that guard: a genuinely short track is not a damaged one. Skits and
    /// album interludes are a real part of a library and have to keep analysing.
    /// </summary>
    [Fact]
    public async Task A_short_track_with_an_honest_duration_still_decodes()
    {
        var path = await SynthesizeAsync("skit.flac", "sine=frequency=440:sample_rate=44100:duration=2", stereo: false);

        var samples = await new AudioDecoder("ffmpeg").DecodeExcerptAsync(path, 2_000);

        Assert.NotNull(samples);
        Assert.InRange(samples!.Length, AudioAnalyzer.SampleRate, AudioAnalyzer.SampleRate * 3);
    }

    private async Task<string> SynthesizeAsync(string name, string filter, bool stereo)
    {
        var path = Path.Combine(_directory, name);

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in new[] { "-v", "error", "-f", "lavfi", "-i", filter, "-ac", stereo ? "2" : "1", "-y", path })
            startInfo.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"could not synthesize {name}: {stderr}");
        return path;
    }
}
